namespace Anvil.Core.Steam;

/// <summary>
/// Die Plattform-Regel aus Spec §4. Gegenüber dem Vorgänger
/// (<c>GamePlatformProbe</c>) an einer Stelle verschärft: „nativ" braucht einen
/// <b>positiven</b> ELF-Beweis, statt aus der Abwesenheit von MZ geschlossen zu werden.
///
/// <para>
/// Gemessen am 2026-09-04 über die ganze Owner-Bibliothek (Spec §2.4): Die alte Regel prüft
/// nur <c>.exe</c>-Dateien, und zwei Spiele haben keine einzige — Rise of the Tomb Raider
/// (Feral-Port, Programm <c>bin/RiseOfTheTombRaider</c>) und Slots &amp; Diapers (Unity,
/// <c>Slots_Diapers.x86_64</c>). Beide sind tatsächlich nativ, die alte Regel urteilt also
/// richtig — aber sie urteilt genauso über einen leeren oder halb heruntergeladenen Ordner.
/// Die neue Regel ist bei 17 von 17 richtig, jedes Mal mit Beweis, und braucht 0,04 s statt
/// 2,1 s.
/// </para>
///
/// <para>
/// <b>Warum eine <c>.so</c> nie als Nativ-Beweis zählt:</b> Windows-Spiele liefern regelmäßig
/// <c>libsteam_api.so</c> mit (Steamworks-Redistributable). Zählte sie, würde ein solches
/// Spiel zu „nativ" — und ab M4 grundlos für Wine-Module gesperrt. Auf der Owner-Bibliothek
/// tritt der Fall heute nicht auf; die Regel darf nicht darauf bauen.
/// </para>
/// </summary>
public sealed class GamePlatformProbe
{
    /// <summary>
    /// Obergrenze geprüfter Dateien. Das größte Spiel der Owner-Bibliothek kommt auf 10 399
    /// (Quasimorph); der Deckel schützt vor Ausreißern, nicht vor dem Normalfall.
    /// </summary>
    public const int DefaultMaxEntries = 20_000;

    /// <summary>Die ersten vier Bytes eines Linux-Programms: <c>\x7f</c> E L F.</summary>
    private static ReadOnlySpan<byte> ElfMagic => [0x7F, 0x45, 0x4C, 0x46];

    /// <summary>Die ersten zwei Bytes eines Windows-Programms: M Z.</summary>
    private static ReadOnlySpan<byte> WindowsMagic => [0x4D, 0x5A];

    /// <summary>Endungen, hinter denen ein Hauptprogramm stecken kann.</summary>
    private static readonly string[] ProgramSuffixes = [".exe", ".x86_64", ".x86", ".bin"];

    /// <summary>Endungen, die nur eine Bibliothek sein können — nie ein Nativ-Beweis.</summary>
    private static readonly string[] LibrarySuffixes = [".so", ".dll"];

    private readonly ICollection<string>? _warnings;
    private readonly int _maxEntries;

    /// <param name="warnings">
    /// Nimmt nicht-fatale Befunde entgegen (Deckel erreicht, Ordner unlesbar). Ohne Liste
    /// verschwinden sie still — die Sonde wirft nie.
    /// </param>
    /// <param name="maxEntries">Nur für Tests herabsetzen.</param>
    public GamePlatformProbe(
        ICollection<string>? warnings = null, int maxEntries = DefaultMaxEntries)
    {
        _warnings = warnings;
        _maxEntries = maxEntries;
    }

    /// <summary>
    /// Entscheidet die Plattform. <paramref name="prefixPath"/> ist der bereits bewiesene
    /// Prefix aus <see cref="GamePrefix"/> — ist er gesetzt, wird der Ordner gar nicht erst
    /// durchsucht.
    /// </summary>
    public GamePlatform Detect(string installDirectory, string? prefixPath)
    {
        // Regel 0: Der Prefix schlägt alles. Steam baut für nichts anderes einen Wine-Prefix.
        // Das spart die Arbeit bei neun von siebzehn Spielen — der Grund für die 0,04 s.
        if (!string.IsNullOrEmpty(prefixPath))
        {
            return GamePlatform.Windows;
        }

        if (string.IsNullOrEmpty(installDirectory) || !Directory.Exists(installDirectory))
        {
            // Kein Fehlen ist doppelt teuer: über den fehlenden Ordner warnt bereits S2.2.
            return GamePlatform.Unknown;
        }

        // IgnoreInaccessible überspringt unlesbare Unterordner, statt den Lauf abzubrechen.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        };

        var seen = 0;
        var sawWindows = false;
        try
        {
            foreach (var file in Directory.EnumerateFiles(installDirectory, "*", options))
            {
                if (++seen > _maxEntries)
                {
                    _warnings?.Add(
                        $"Platform check gave up after {_maxEntries} files: {installDirectory}");
                    return GamePlatform.Unknown;
                }

                var isProgram = IsProgramCandidate(file);
                if (!isProgram && !HasSuffix(file, LibrarySuffixes))
                {
                    continue;
                }

                switch (KindOf(file))
                {
                    // Regel 1: Ein ELF-Programm beweist den nativen Build — sofort, und nur
                    // aus einem Programm. Eine ELF-Bibliothek beweist gar nichts.
                    case ExecutableKind.Elf when isProgram:
                        return GamePlatform.NativeLinux;

                    // Regel 2: MZ nur merken. Eine echte Windows-Exe (Crash-Handler,
                    // Redistributable) darf neben einem nativen Build liegen — der Durchlauf
                    // muss weiterlaufen, sonst geht Regel 1 verloren.
                    case ExecutableKind.Windows:
                        sawWindows = true;
                        break;

                    default:
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _warnings?.Add(
                $"Platform check failed for {installDirectory}: {exception.Message}");
            return GamePlatform.Unknown;
        }

        // Regel 3: Ohne jeden Beweis wird nichts behauptet.
        return sawWindows ? GamePlatform.Windows : GamePlatform.Unknown;
    }

    /// <summary>
    /// Kann diese Datei ein Hauptprogramm sein? Bekannte Programm-Endung, oder gar keine
    /// Endung und das Ausführungsrecht gesetzt — so heißen native Ports (Feral, Unity).
    /// </summary>
    private static bool IsProgramCandidate(string file)
    {
        if (HasSuffix(file, ProgramSuffixes))
        {
            return true;
        }

        if (Path.GetFileName(file).Contains('.'))
        {
            return false;
        }

        try
        {
            return File.GetUnixFileMode(file).HasFlag(UnixFileMode.UserExecute);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasSuffix(string file, string[] suffixes)
    {
        foreach (var suffix in suffixes)
        {
            if (file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Was die ersten Bytes verraten.</summary>
    private enum ExecutableKind
    {
        /// <summary>Weder ELF noch MZ — oder gar nicht lesbar.</summary>
        Unknown,

        /// <summary>Ein natives Linux-Programm.</summary>
        Elf,

        /// <summary>Ein Windows-Programm.</summary>
        Windows,
    }

    /// <summary>
    /// Liest die ersten vier Bytes. Wirft nie: eine zu kurze, gerade gelöschte oder nicht
    /// lesbare Datei gilt schlicht als <see cref="ExecutableKind.Unknown"/>.
    /// </summary>
    private static ExecutableKind KindOf(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            Span<byte> head = stackalloc byte[4];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

            if (read >= ElfMagic.Length && head[..ElfMagic.Length].SequenceEqual(ElfMagic))
            {
                return ExecutableKind.Elf;
            }

            return read >= WindowsMagic.Length
                && head[..WindowsMagic.Length].SequenceEqual(WindowsMagic)
                    ? ExecutableKind.Windows
                    : ExecutableKind.Unknown;
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            return ExecutableKind.Unknown;
        }
    }
}
