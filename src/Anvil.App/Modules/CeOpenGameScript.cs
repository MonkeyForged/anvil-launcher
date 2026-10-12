using Microsoft.Extensions.Logging;

namespace Anvil.App.Modules;

/// <summary>
/// The autorun script that makes CE show which game it has opened (s10.1).
///
/// <para>
/// <b>Why a script at all:</b> <c>--OPENPID</c> opens the process but leaves CE's title at
/// "ProcessID &lt;n&gt;". Opening the same process once more from Lua sets its name
/// (<c>docs/referenz/cheat-engine-machbarkeit.md</c> §10.1). CE runs every file in its
/// <c>autorun/</c> folder at start, so the script lies next to the program in the taken-over
/// release folder — never in the store.
/// </para>
///
/// <para>
/// <b>Always placed, also with the box off:</b> without <c>--OPENPID</c> the process id is 0 and
/// the script does nothing.
/// </para>
/// </summary>
public static class CeOpenGameScript
{
    /// <summary>The file name inside the release folder's <c>autorun/</c> folder.</summary>
    public const string FileName = "anvil-open-game.lua";

    /// <summary>
    /// What the file holds. The delay gives CE time to finish opening the process from the
    /// command line before it is opened again; the timer destroys itself so it runs once.
    /// </summary>
    public const string Content = """
        -- Named reopen after --OPENPID (docs/referenz/cheat-engine-machbarkeit.md section 10.1).
        -- --OPENPID opens the process but leaves Cheat Engine's title at "ProcessID <n>".
        -- Opening the same process again from Lua sets the name, so the user sees which game
        -- is attached. Without --OPENPID no process is open and nothing happens.
        local timer = createTimer(nil, false)
        timer.Interval = 1500
        timer.OnTimer = function(t)
          t.destroy()
          local pid = getOpenedProcessID()
          if pid ~= 0 then openProcess(pid) end
        end
        timer.Enabled = true

        """;

    private const string AutorunFolder = "autorun";

    /// <summary>
    /// Writes the script into <paramref name="applicationFolder"/> (the taken-over release folder)
    /// if it is missing or its content differs; a file that is already right is not touched.
    /// Never touches the store. Returns false (and logs) when the write fails — attaching goes
    /// on without it, and CE then opens with "ProcessID &lt;n&gt;" in its title.
    /// </summary>
    public static bool Ensure(string applicationFolder, ILogger? logger)
    {
        var path = Path.Combine(applicationFolder, AutorunFolder, FileName);
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == Content)
            {
                return true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Content);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(
                exception, "CE open-game script could not be written to {Path}; CE opens without its name",
                path);
            return false;
        }
    }
}
