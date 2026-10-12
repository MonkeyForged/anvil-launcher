using Microsoft.Extensions.Logging;

namespace Anvil.Core.Steam;

/// <summary>
/// Liest eine ganze Steam-Installation. <see cref="Scan"/> wirft nie: was nicht lesbar ist,
/// wird zur Warnung, und der Rest wird gelesen (Spec §8). Eine Maschine ohne Steam ist kein
/// Absturzgrund.
///
/// <para>
/// Seit S2.3 baut der Scanner auch den Steckbrief je Spiel (Spec §7, E5): es gibt <b>eine</b>
/// Stelle, die die Platte liest, und der Rescan aus S2.5 bleibt ein zweiter
/// <see cref="Scan"/>-Aufruf und nichts weiter. Gemessen kostet das 0,04 s über die ganze
/// Owner-Bibliothek — kein Fortschrittsbalken, kein Zwischenspeicher.
/// </para>
/// </summary>
public sealed class SteamScanner
{
    private readonly Func<string, string?> _getEnv;
    private readonly ILogger? _logger;

    public SteamScanner(Func<string, string?> getEnv, ILogger? logger = null)
    {
        _getEnv = getEnv;
        _logger = logger;
    }

    /// <summary>Liest alles neu von der Platte und liefert ein frisches Abbild.</summary>
    public SteamSnapshot Scan()
    {
        var location = SteamRoot.Find(_getEnv);
        if (location is null)
        {
            var checkedPaths = string.Join(", ", SteamRoot.Candidates(_getEnv));
            _logger?.LogWarning("no steam installation found (checked: {Checked})", checkedPaths);
            return SteamSnapshot.Empty($"No Steam installation found. Checked: {checkedPaths}.");
        }

        _logger?.LogInformation(
            "steam root found at {Root} (candidate: {Candidate})",
            location.Path, location.Candidate);

        var warnings = new List<string>();
        var libraries = SteamLibraryFolders.Read(location.Path, warnings);
        _logger?.LogInformation(
            "{Count} librar{Suffix}", libraries.Count, libraries.Count == 1 ? "y" : "ies");

        var games = new List<GameProfile>();
        var tools = new List<SteamGame>();
        var seen = new HashSet<uint>();

        foreach (var library in libraries)
        {
            var steamapps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamapps))
            {
                warnings.Add($"Library folder is not available: {library}");
                continue;
            }

            // Sortiert, damit „der erste Fund gewinnt" bei doppelten AppIds keine Laune des
            // Dateisystems ist, sondern wiederholbar.
            var manifests = Directory.GetFiles(steamapps, "appmanifest_*.acf");
            Array.Sort(manifests, StringComparer.Ordinal);
            _logger?.LogDebug("{Library}: {Count} manifests", library, manifests.Length);

            foreach (var manifest in manifests)
            {
                var entry = AppManifest.Read(manifest, library, warnings);
                if (entry is null)
                {
                    continue;
                }

                if (!seen.Add(entry.AppId))
                {
                    warnings.Add(
                        $"Ignored a second copy of app {entry.AppId} ({entry.Name}) in {library}.");
                    continue;
                }

                if (AppManifest.IsTool(entry))
                {
                    tools.Add(entry);
                    continue;
                }

                // Fehlt der Ordner, bleibt das Spiel trotzdem stehen (Spec §8, E4). Die Folgen
                // bleiben ehrlich: S2.3 meldet `unknown`, S2.6 öffnet nichts.
                if (!Directory.Exists(entry.InstallDirectory))
                {
                    warnings.Add(
                        $"{entry.Name} is marked as installed, "
                        + $"but {entry.InstallDirectory} is missing.");
                }

                // Erst der sichere Rückfall — die Steckbrief-Werte trägt die zweite Schleife
                // nach, sobald der Katalog steht (siehe unten).
                games.Add(new GameProfile(entry, GamePlatform.Unknown, null, null, null, null, null));
            }
        }

        tools.Sort(ToolByName);

        // Die Momentaufnahme steht; jetzt der Steckbrief je Spiel (S2.3). Der Katalog braucht
        // die Werkzeuge aus der Schleife, also entsteht er danach — nichts wird zweimal gelesen.
        var catalog = CompatToolCatalog.Build(location.Path, tools, warnings);
        _logger?.LogInformation(
            "compat tool catalog: {Count} tools, {Selectable} selectable",
            catalog.Tools.Count, catalog.Selectable.Count);

        var keyless = catalog.Tools.Where(tool => tool.Key is null).Select(tool => tool.Name).ToList();
        if (keyless.Count > 0)
        {
            // Runtimes und Beigaben — erwartet, keine Warnung (Spec S4.3 E-C).
            _logger?.LogDebug("compat tools without a write key: {Names}", string.Join(", ", keyless));
        }

        var mapping = CompatToolMapping.Load(location.Path, warnings);
        _logger?.LogDebug("compat tool mapping: {Count} entries", mapping.Count);

        var user = SteamUsers.FindActive(location.Path, warnings);
        var launchOptions = user is null
            ? GameLaunchOptions.Empty
            : GameLaunchOptions.Load(location.Path, user.AccountId, warnings);

        if (user is not null)
        {
            _logger?.LogInformation("steam user {AccountId} selected", user.AccountId);
        }

        var probe = new GamePlatformProbe(warnings);
        var profiles = new List<GameProfile>(games.Count);

        foreach (var profile in games)
        {
            var game = profile.Game;
            var prefix = GamePrefix.Find(game.LibraryPath, game.AppId);
            var platform = probe.Detect(game.InstallDirectory, prefix);

            var configInfo = ProtonConfigInfo.Read(game.LibraryPath, game.AppId, warnings);
            var proton = configInfo is null
                ? null
                : new ProtonRuntime(
                    configInfo.Version, catalog.Resolve(configInfo.ToolPath), configInfo.ToolPath);

            profiles.Add(profile with
            {
                Platform = platform,
                PrefixPath = prefix,
                Proton = proton,
                CompatToolKey = mapping.TryGet(game.AppId),
                LaunchOptions = launchOptions.TryGet(game.AppId),
                ArtworkPath = GameArtwork.Find(location.Path, game.AppId, warnings),
            });

            _logger?.LogDebug(
                "{AppId}: platform {Platform}, prefix {Prefix}, proton {Proton}",
                game.AppId, platform, prefix is null ? "no" : "yes",
                proton?.Version ?? "none");
        }

        profiles.Sort(ByName);

        foreach (var warning in warnings)
        {
            _logger?.LogWarning("{Warning}", warning);
        }

        _logger?.LogDebug(
            "profiles: {Windows} windows, {Native} native, {Unknown} unknown, {Prefixed} with prefix",
            profiles.Count(entry => entry.Platform == GamePlatform.Windows),
            profiles.Count(entry => entry.Platform == GamePlatform.NativeLinux),
            profiles.Count(entry => entry.Platform == GamePlatform.Unknown),
            profiles.Count(entry => entry.PrefixPath is not null));

        _logger?.LogDebug(
            "artwork: {Found} of {Total} games have library art",
            profiles.Count(entry => entry.ArtworkPath is not null),
            profiles.Count);

        _logger?.LogInformation(
            "scan complete: {Games} games, {Tools} tools", profiles.Count, tools.Count);

        return new SteamSnapshot(location.Path, libraries, profiles, tools, catalog, user, warnings);
    }

    private static int ByName(GameProfile left, GameProfile right)
        => string.Compare(left.Game.Name, right.Game.Name, StringComparison.CurrentCultureIgnoreCase);

    private static int ToolByName(SteamGame left, SteamGame right)
        => string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
}
