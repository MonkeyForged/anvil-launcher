using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Anvil.Core.Steam;

/// <summary>
/// Steams binärer App-Cache <c>appcache/appinfo.vdf</c>, gelesen so weit, wie ein Eintrag
/// gebraucht wird (Spec S4.3a §2, §3.1).
///
/// <para>
/// <b>Format 29</b> (gemessen 2026-09-07, alle Zahlen little-endian): Magic u32, Universe u32,
/// Offset der String-Tabelle i64, ab Byte 16 Einträge — AppId u32 (0 = Ende), Größe u32,
/// 60 Bytes Rumpfkopf (state, last_updated, access_token, SHA-1, change_number, SHA-1 der
/// KeyValues), dann binäre KeyValues bis zum Ende des Rumpfs. Die String-Tabelle am Offset:
/// Anzahl u32, nullterminierte UTF-8-Zeichenketten; Schlüssel in den KeyValues sind Indizes
/// darauf. <b>Nur diese Version</b> (E-M): die Formate 27/28 schreibt Steam seit 2023 nicht
/// mehr, ein künftiges meldet <see cref="Read"/> laut statt zu raten.
/// </para>
///
/// <para>
/// Fremde Einträge werden nach Größe übersprungen — App 891390 liegt hinter 1305 anderen,
/// der Weg dorthin kostet unter 1 ms.
/// </para>
/// </summary>
public static class AppInfoFile
{
    /// <summary>Format 29 — der einzige gemessene und der einzige gelesene Stand.</summary>
    public const uint SupportedMagic = 0x07564429;

    private const int FirstEntryOffset = 16;

    /// <summary>state (4) + last_updated (4) + access_token (8) + SHA-1 (20) + change_number (4) + SHA-1 (20).</summary>
    private const int EntryHeaderSize = 60;

    private const byte TypeObject = 0;
    private const byte TypeString = 1;
    private const byte TypeInt32 = 2;
    private const byte TypeFloat = 3;
    private const byte TypeInt64 = 7;
    private const byte TypeEnd = 8;

    /// <summary>Die Versionskennung in den ersten vier Bytes (<paramref name="data"/> hat mindestens vier).</summary>
    public static uint Magic(byte[] data) => BinaryPrimitives.ReadUInt32LittleEndian(data);

    /// <summary>
    /// Der KeyValues-Block des Eintrags <paramref name="appId"/> als Wurzel-Container (Schlüssel
    /// leer, darunter <c>appinfo</c>), oder <c>null</c>, wenn die Datei ihn nicht führt. Wirft
    /// <see cref="InvalidDataException"/> bei fremder Version oder verstümmeltem Inhalt und
    /// <see cref="EndOfStreamException"/> (eine <see cref="IOException"/>), wenn die Datei
    /// mitten in einem Wert endet.
    /// </summary>
    public static AppInfoObject? Read(byte[] data, uint appId)
    {
        using var stream = new MemoryStream(data, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        var magic = reader.ReadUInt32();
        if (magic != SupportedMagic)
        {
            throw new InvalidDataException($"unknown format 0x{magic:X8}");
        }

        reader.ReadUInt32(); // universe — nicht ausgewertet
        var tableOffset = reader.ReadInt64();
        if (tableOffset < FirstEntryOffset || tableOffset >= data.Length)
        {
            throw new InvalidDataException(
                $"string table offset {tableOffset} is outside the file ({data.Length} bytes)");
        }

        var strings = ReadStringTable(reader, data, tableOffset);

        stream.Position = FirstEntryOffset;
        while (true)
        {
            var entryAppId = reader.ReadUInt32();
            if (entryAppId == 0)
            {
                return null;
            }

            var size = reader.ReadUInt32();
            var end = stream.Position + size;
            if (end > tableOffset)
            {
                throw new InvalidDataException($"entry {entryAppId} runs past the string table");
            }

            if (entryAppId != appId)
            {
                stream.Position = end;
                continue;
            }

            stream.Position += EntryHeaderSize;
            return ReadObject(reader, data, strings, key: string.Empty);
        }
    }

    private static string[] ReadStringTable(BinaryReader reader, byte[] data, long offset)
    {
        reader.BaseStream.Position = offset;
        var count = reader.ReadUInt32();

        // Jede Zeichenkette braucht mindestens ihr Nullbyte — mehr Einträge als Bytes ist Unsinn.
        if (count > data.Length - offset)
        {
            throw new InvalidDataException($"string table claims {count} entries");
        }

        var strings = new string[count];
        for (var index = 0; index < strings.Length; index++)
        {
            strings[index] = ReadString(reader, data);
        }

        return strings;
    }

    private static string ReadString(BinaryReader reader, byte[] data)
    {
        var start = (int)reader.BaseStream.Position;
        if (start >= data.Length)
        {
            throw new EndOfStreamException("string expected past the end of the file");
        }

        var end = Array.IndexOf(data, (byte)0, start);
        if (end < 0)
        {
            throw new EndOfStreamException("unterminated string");
        }

        reader.BaseStream.Position = end + 1;
        return Encoding.UTF8.GetString(data, start, end - start);
    }

    private static AppInfoObject ReadObject(
        BinaryReader reader, byte[] data, string[] strings, string key)
    {
        var objects = new List<AppInfoObject>();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            var kind = reader.ReadByte();
            if (kind == TypeEnd)
            {
                return new AppInfoObject(key, objects, values);
            }

            var index = reader.ReadUInt32();
            if (index >= strings.Length)
            {
                throw new InvalidDataException(
                    $"string index {index} is outside the table of {strings.Length} entries");
            }

            var name = strings[index];
            switch (kind)
            {
                case TypeObject:
                    objects.Add(ReadObject(reader, data, strings, name));
                    break;
                case TypeString:
                    values[name] = ReadString(reader, data);
                    break;
                case TypeInt32:
                    values[name] = reader.ReadInt32().ToString(CultureInfo.InvariantCulture);
                    break;
                case TypeFloat:
                    values[name] = reader.ReadSingle().ToString("R", CultureInfo.InvariantCulture);
                    break;
                case TypeInt64:
                    values[name] = reader.ReadInt64().ToString(CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new InvalidDataException(
                        $"unknown value type {kind} at offset {reader.BaseStream.Position - 5}");
            }
        }
    }
}
