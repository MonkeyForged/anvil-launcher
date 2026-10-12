using System.Diagnostics;
using System.Globalization;
using System.Text;
using Anvil.Core.Modules;

namespace Anvil.Core.Sessions;

/// <summary>
/// Steam's launch client: the mouthpiece into the container of a running game (spec §4.4).
///
/// <para>
/// <b>Why it is needed.</b> SIGTERM from outside bounces off a Wine world — measured on Boltgun,
/// 60 s without effect (handover §2). What works is <c>wineserver -k</c> <i>inside</i> the
/// container, and only this client gets there. After that the session is gone in about two
/// seconds.
/// </para>
///
/// <para>
/// <b>The call's return value says nothing</b> and is not evaluated: it is often 143, because
/// the client takes itself down too. Whether the stop worked is told by the watcher
/// (spec §5.5).
/// </para>
/// </summary>
public static class SteamLaunchClient
{
    private const string RuntimeDirectoryPrefix = "SteamLinuxRuntime";
    private const string ClientFileName = "steam-runtime-launch-client";

    /// <summary>
    /// Keeps Steam's overlay from starting for an attached program (s10.4). Steam starts its own
    /// overlay process for every program started in the game's container; one of them once held a
    /// lock in Steam so the game's overlay never ended and kept the monitor awake (measured
    /// 2026-10-02, <c>docs/releases/1.2.0/stories/s10.4-ohne-overlay/messung/</c>). With this
    /// switch the program starts without an overlay and keeps working.
    /// The trailing <c>1</c> is the version of the Vulkan layer; if Steam renames it, the
    /// overlay is back — the roadmap idea "read the switch from Steam's manifest" covers that.
    /// </summary>
    private const string OverlaySwitch = "--env=DISABLE_VK_LAYER_VALVE_steam_overlay_1=1";

    /// <summary>
    /// Searches the client under the Steam root; <c>null</c> if there is none. It is not on the
    /// PATH (spec §2.3). The owner's machine has several runtimes side by side — the order is
    /// fixed by name so the choice is reproducible; which client it becomes does not matter.
    /// </summary>
    public static string? Find(string steamRootPath)
    {
        var common = Path.Combine(steamRootPath, "steamapps", "common");
        string[] runtimes;
        try
        {
            runtimes = Directory.GetDirectories(common, RuntimeDirectoryPrefix + "*");
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // No Steam tree at this place: no client, no error.
            return null;
        }

        foreach (var runtime in runtimes.OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var client = Path.Combine(runtime, "pressure-vessel", "bin", ClientFileName);
            if (File.Exists(client))
            {
                return client;
            }
        }

        return null;
    }

    /// <summary>
    /// The call that clears a game's Wine world. Since S4.3 the bus name comes from
    /// <see cref="SteamBusName"/>; <see cref="GameSession"/> still deliberately does not carry it
    /// (spec S3.1 §4.3).
    /// </summary>
    public static ProcessStartInfo StartInfoFor(string clientPath, uint appId)
    {
        var info = new ProcessStartInfo(clientPath) { UseShellExecute = false };
        info.ArgumentList.Add("--bus-name=" + SteamBusName.ForApp(appId));
        info.ArgumentList.Add("--");
        info.ArgumentList.Add("wineserver");
        info.ArgumentList.Add("-k");
        return info;
    }

    /// <summary>
    /// The call that starts a program <b>in the game's container</b> (spec S4.4 §3.3) — the
    /// counterpart to ending it above.
    ///
    /// <para>
    /// <b>The proven command</b> from the predecessor project (poc-findings.md §3, six live
    /// acceptances): bus name, working folder in the container, then
    /// <c>wine start /unix &lt;path&gt;</c> — with <b>one deviation</b>: the overlay switch
    /// (<see cref="OverlaySwitch"/>, s10.4). It sits before <c>--</c>, because behind it the
    /// client would pass it to <c>wine</c> as part of the program's command line.
    /// </para>
    ///
    /// <para>
    /// <b>A Linux path, not a Windows path</b> (E-8): <c>/unix</c> tells Wine exactly that. The
    /// <c>Z:\…</c> that S4.5 recognises in the command line is formed by the container itself —
    /// building it here would mean guessing the drive letter and the character set rules.
    /// </para>
    ///
    /// <para>
    /// <b>The environment is inherited, not filtered</b> (E-9). The predecessor passed six
    /// variables one by one because Python has to set each one; here the process inherits
    /// anyway, and the stop path of the same client does too.
    /// </para>
    ///
    /// <para>
    /// <b>Whoever starts this call never awaits it.</b> The reason is given at
    /// <c>AttachStarter</c>; the rule is here because this is where the call is made.
    /// </para>
    /// </summary>
    public static ProcessStartInfo StartInfoFor(string clientPath, uint appId, AttachProgram program)
    {
        var info = new ProcessStartInfo(clientPath) { UseShellExecute = false };
        info.ArgumentList.Add("--bus-name=" + SteamBusName.ForApp(appId));
        info.ArgumentList.Add("--directory=" + program.WorkingDirectory);
        info.ArgumentList.Add(OverlaySwitch);
        info.ArgumentList.Add("--");
        info.ArgumentList.Add("wine");
        info.ArgumentList.Add("start");
        info.ArgumentList.Add("/unix");
        info.ArgumentList.Add(program.ExecutablePath);

        foreach (var argument in program.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    /// <summary>
    /// The question "who runs in your Wine world?" (spec S4.5 §3.3). <b>Awaited</b>, unlike the
    /// start — the answer is the output.
    ///
    /// <para>
    /// Under Proton 8 Wine's <c>tasklist</c> returns nothing, under Proton 11 the complete list
    /// (messungen-anhaengen.md §3). There is no fallback: Wand only runs from Proton 9 on anyway,
    /// and a guessed detach is the most dangerous spot of the module (project plan M4,
    /// decision 6).
    /// </para>
    /// </summary>
    public static ProcessStartInfo TasklistInfoFor(string clientPath, uint appId)
    {
        var info = new ProcessStartInfo(clientPath) { UseShellExecute = false };
        info.ArgumentList.Add("--bus-name=" + SteamBusName.ForApp(appId));
        info.ArgumentList.Add("--");
        info.ArgumentList.Add("wine");
        info.ArgumentList.Add("tasklist");
        return info;
    }

    /// <summary>
    /// The question "who started whom?" (spec s10.1): <c>wmic</c> lists name, pid and parent pid
    /// of every process in the container. <b>Awaited</b>, the answer is the output.
    ///
    /// <para>
    /// <b>No output encoding is forced.</b> wmic writes UTF-16 with a byte order mark only into a
    /// file; into a pipe, the way Anvil reads it, it writes single-byte text (measured 2026-10-01,
    /// s10.1). Forcing UTF-16 garbled every name. The default reader detects a byte order mark by
    /// itself, so either form reads correctly.
    /// </para>
    /// </summary>
    public static ProcessStartInfo ProcessParentsInfoFor(string clientPath, uint appId)
    {
        var info = new ProcessStartInfo(clientPath) { UseShellExecute = false };
        info.ArgumentList.Add("--bus-name=" + SteamBusName.ForApp(appId));
        info.ArgumentList.Add("--");
        info.ArgumentList.Add("wine");
        info.ArgumentList.Add("wmic");
        info.ArgumentList.Add("process");
        info.ArgumentList.Add("get");
        info.ArgumentList.Add("Name,ParentProcessId,ProcessId");
        return info;
    }

    /// <summary>
    /// The question "how much memory does everyone hold?" (spec s10.1): <c>tasklist</c> as CSV
    /// without header. Unlike <see cref="TasklistInfoFor"/> (default format, used by detaching) a
    /// line is unambiguous even for names with spaces. <b>Awaited</b>.
    /// </summary>
    public static ProcessStartInfo ProcessMemoryInfoFor(string clientPath, uint appId)
    {
        var info = new ProcessStartInfo(clientPath) { UseShellExecute = false };
        info.ArgumentList.Add("--bus-name=" + SteamBusName.ForApp(appId));
        info.ArgumentList.Add("--");
        info.ArgumentList.Add("wine");
        info.ArgumentList.Add("tasklist");
        info.ArgumentList.Add("/fo");
        info.ArgumentList.Add("csv");
        info.ArgumentList.Add("/nh");
        return info;
    }

    /// <summary>
    /// The call that ends the own processes in the container (spec S4.5 §3.3) — <b>and only
    /// them</b>. Which ones those are is decided by the <see cref="Detacher"/>; only the call is
    /// here.
    ///
    /// <para>
    /// <b>Without <paramref name="force"/> it is a request to close</b> (s9.2): Wine passes it on
    /// as a close message, and a program can shut down cleanly. <c>/F</c> ends it on the spot —
    /// measured, with Cheat Engine's debugger attached that takes the game along. So the
    /// <see cref="Detacher"/> asks first and forces only what is still listed after the grace.
    /// </para>
    ///
    /// <para>
    /// <b>All pids at once</b> (E-6): ending the main process takes the family along; the others
    /// report <c>Could not find process</c> afterwards. With single calls every following one
    /// would be a race against dying processes, and every "no longer exists" would have to be
    /// explained as a non-error on its own. The return value says nothing either way — the
    /// verdict comes from looking again.
    /// </para>
    ///
    /// <para>
    /// <b>Never <c>wineserver -k</c></b>: that is the stop path for the whole game
    /// (<see cref="StartInfoFor(string, uint)"/>), not for a program inside it.
    /// </para>
    /// </summary>
    /// <param name="pids">The processes to end; at least one.</param>
    /// <param name="force"><c>true</c> appends <c>/F</c> as the last argument.</param>
    /// <returns>The call, not yet started.</returns>
    public static ProcessStartInfo TaskkillInfoFor(
        string clientPath, uint appId, IReadOnlyList<int> pids, bool force)
    {
        ArgumentNullException.ThrowIfNull(pids);
        if (pids.Count == 0)
        {
            throw new ArgumentException("Nothing to terminate — name at least one pid.", nameof(pids));
        }

        var info = new ProcessStartInfo(clientPath) { UseShellExecute = false };
        info.ArgumentList.Add("--bus-name=" + SteamBusName.ForApp(appId));
        info.ArgumentList.Add("--");
        info.ArgumentList.Add("wine");
        info.ArgumentList.Add("taskkill");
        foreach (var pid in pids)
        {
            info.ArgumentList.Add("/PID");
            info.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
        }

        if (force)
        {
            info.ArgumentList.Add("/F");
        }

        return info;
    }
}
