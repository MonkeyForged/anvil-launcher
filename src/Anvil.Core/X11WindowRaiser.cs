using System.Runtime.InteropServices;
using System.Text;

namespace Anvil.Core;

/// <summary>Was <see cref="X11WindowRaiser.Raise"/> erreicht hat — ein Wert, keine Ausnahme.</summary>
public enum RaiseOutcome
{
    /// <summary>Die Aktivierungsnachricht ist beim X-Server; KWin hebt das Fenster.</summary>
    Raised,

    /// <summary>Kein passendes Fenster in der Liste — die Anwendung laeuft (noch) nicht.</summary>
    NoWindow,

    /// <summary>Kein X-Display erreichbar oder <c>libX11</c> fehlt (natives Wayland, Tests).</summary>
    Unavailable,
}

/// <summary>
/// Ein Fenster aus <c>_NET_CLIENT_LIST</c>, auf das reduziert, was die Wahl braucht.
/// <paramref name="ResourceClass"/> ist das zweite Feld von <c>WM_CLASS</c>;
/// <paramref name="IsNormal"/> heisst Typ <c>_NET_WM_WINDOW_TYPE_NORMAL</c> oder ohne Typ.
/// </summary>
public sealed record X11WindowInfo(nint Id, string? ResourceClass, string? Title, bool IsNormal);

/// <summary>
/// Holt ein fremdes X11-Fenster nach vorn (S3.8). Der Fall: Nach „Show in Steam" bittet der
/// laufende Steam-Client KWin um Aktivierung, aber ohne den Zeitstempel einer Nutzereingabe —
/// KWins Vorbeugung gegen Aktivierungsuebernahme lehnt ab und laesst nur die Taskleiste
/// blinken (<c>_NET_WM_STATE_DEMANDS_ATTENTION</c>, gemessen 2026-09-06). Eine
/// <c>_NET_ACTIVE_WINDOW</c>-Nachricht mit <b>Quelle 2 (Pager)</b> dagegen erzwingt KWin:
/// Sie ist fuer Werkzeuge gedacht, die im Auftrag des Nutzers handeln, und genau das ist der
/// Klick (Spec S3.8 E-B).
///
/// <para>
/// <b>Wahl und Wirkung getrennt</b> wie bei <see cref="DesktopShell"/>: <see cref="ChooseTarget"/>
/// ist rein und getestet, <see cref="Raise"/> spricht mit dem X-Server und wird live
/// abgenommen. Das geht, weil beide Seiten X11-Clients sind — Steam ist es, und Anvil laeuft
/// mit Avalonia 11 unter Xwayland; <c>Avalonia.X11</c> bindet <c>libX11.so.6</c> bereits.
/// Mit einem nativen Wayland-Backend gaebe es kein Display mehr, dann kommt
/// <see cref="RaiseOutcome.Unavailable"/> zurueck und nichts bricht (Spec §8).
/// </para>
/// </summary>
public static unsafe partial class X11WindowRaiser
{
    private const string LibX11 = "libX11.so.6";

    private const int ClientMessage = 33;

    private const int PropertyFormatLong = 32;

    private const nint AtomAtom = 4;

    private const nint AtomString = 31;

    private const nint AtomWindow = 33;

    private const nint AtomWmClass = 67;

    /// <summary>EWMH-Quelle „Pager/Werkzeug" — fuer sie erzwingt KWin die Aktivierung.</summary>
    private const long SourcePager = 2;

    private const nint SubstructureNotifyMask = 1 << 19;

    private const nint SubstructureRedirectMask = 1 << 20;

    /// <summary>
    /// Welches Fenster gehoben wird: nur normale Fenster der Klasse; unter denen das erste mit
    /// dem bevorzugten Titel, sonst das erste ueberhaupt; keins → 0. Steams Freundesliste ist
    /// ein normales Fenster derselben Klasse — deshalb der Titel als zweites Kriterium
    /// (Spec S3.8 E-I).
    /// </summary>
    public static nint ChooseTarget(
        IReadOnlyList<X11WindowInfo> windows, string resourceClass, string preferredTitle)
    {
        nint fallback = 0;
        foreach (var window in windows)
        {
            if (!window.IsNormal
                || !string.Equals(window.ResourceClass, resourceClass, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(window.Title, preferredTitle, StringComparison.Ordinal))
            {
                return window.Id;
            }

            if (fallback == 0)
            {
                fallback = window.Id;
            }
        }

        return fallback;
    }

    /// <summary>
    /// Hebt das Fenster, das <see cref="ChooseTarget"/> waehlt. Eigene Display-Verbindung je
    /// Aufruf, nichts wird gehalten (Spec E-E). Ungeprueft — hier spricht Anvil mit dem
    /// X-Server; geprueft ist die Wahl.
    /// </summary>
    public static RaiseOutcome Raise(string resourceClass, string preferredTitle)
    {
        nint display;
        try
        {
            display = XOpenDisplay(0);
        }
        catch (DllNotFoundException)
        {
            return RaiseOutcome.Unavailable;
        }

        if (display == 0)
        {
            return RaiseOutcome.Unavailable;
        }

        // Xlibs Standard-Fehlerhandler beendet den Prozess — etwa bei BadWindow, wenn ein
        // Fenster zwischen Liste und Abfrage verschwindet. Avalonia hat seinen eigenen
        // Handler gesetzt; fuer die Dauer dieses Aufrufs gilt unserer, danach wieder seiner.
        // Alles laeuft auf dem UI-Faden, es gibt keinen zweiten Aufrufer (Spec E-F).
        var previous = XSetErrorHandler((nint)(delegate* unmanaged<nint, nint, int>)&IgnoreError);
        try
        {
            var root = XDefaultRootWindow(display);
            var target = ChooseTarget(ListWindows(display, root), resourceClass, preferredTitle);
            if (target == 0)
            {
                return RaiseOutcome.NoWindow;
            }

            Activate(display, root, target);
            return RaiseOutcome.Raised;
        }
        finally
        {
            // XSync holt ausstehende Fehlerantworten ab, solange noch unser Handler gilt.
            XSync(display, 0);
            XSetErrorHandler(previous);
            XCloseDisplay(display);
        }
    }

    private static List<X11WindowInfo> ListWindows(nint display, nint root)
    {
        var clientList = XInternAtom(display, "_NET_CLIENT_LIST", 0);
        var windowType = XInternAtom(display, "_NET_WM_WINDOW_TYPE", 0);
        var typeNormal = XInternAtom(display, "_NET_WM_WINDOW_TYPE_NORMAL", 0);
        var wmName = XInternAtom(display, "_NET_WM_NAME", 0);
        var utf8String = XInternAtom(display, "UTF8_STRING", 0);

        var windows = new List<X11WindowInfo>();
        foreach (var id in ReadItems(display, root, clientList, AtomWindow))
        {
            // WM_CLASS ist "instanz\0klasse\0" — Steam: "steamwebhelper\0steam\0".
            var classFields = ReadText(display, id, AtomWmClass, AtomString);
            var types = ReadItems(display, id, windowType, AtomAtom);
            var titleFields = ReadText(display, id, wmName, utf8String);

            windows.Add(new X11WindowInfo(
                id,
                classFields.Length > 1 ? classFields[1] : null,
                titleFields.Length > 0 ? titleFields[0] : null,
                types.Length == 0 || types.Contains(typeNormal)));
        }

        return windows;
    }

    private static void Activate(nint display, nint root, nint target)
    {
        var message = new XClientMessageEvent
        {
            Type = ClientMessage,
            Window = target,
            MessageType = XInternAtom(display, "_NET_ACTIVE_WINDOW", 0),
            Format = PropertyFormatLong,
            Source = SourcePager,
            Timestamp = 0,
            Requestor = 0,
        };

        XSendEvent(display, root, 0, SubstructureRedirectMask | SubstructureNotifyMask, ref message);
        XFlush(display);
    }

    /// <summary>
    /// Eine Eigenschaft als Bytes. Format 32 liefert Xlib als C-<c>long</c> — 8 Byte je
    /// Eintrag, auch fuer 32-Bit-Werte; die Aufrufer rechnen damit.
    /// </summary>
    private static byte[] ReadProperty(nint display, nint window, nint property, nint type)
    {
        var status = XGetWindowProperty(
            display, window, property, 0, int.MaxValue, 0, type,
            out _, out var format, out var count, out _, out var data);
        if (status != 0 || data == 0)
        {
            return [];
        }

        try
        {
            var bytesPerItem = format switch
            {
                PropertyFormatLong => sizeof(nint),
                16 => 2,
                _ => 1,
            };
            var bytes = new byte[(int)count * bytesPerItem];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            XFree(data);
        }
    }

    private static nint[] ReadItems(nint display, nint window, nint property, nint type)
    {
        var bytes = ReadProperty(display, window, property, type);
        var items = new nint[bytes.Length / sizeof(nint)];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = (nint)BitConverter.ToInt64(bytes, i * sizeof(nint));
        }

        return items;
    }

    /// <summary>NUL-getrennte Felder; ein leeres letztes Feld faellt weg.</summary>
    private static string[] ReadText(nint display, nint window, nint property, nint type)
    {
        var bytes = ReadProperty(display, window, property, type);
        return Encoding.UTF8.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    [UnmanagedCallersOnly]
    private static int IgnoreError(nint display, nint error) => 0;

    /// <summary>
    /// <c>XClientMessageEvent</c> in der 192-Byte-Union <c>XEvent</c>; nur die Felder, die
    /// Anvil setzt. Serial, send_event und display fuellt Xlib beim Senden.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct XClientMessageEvent
    {
        [FieldOffset(0)]
        public int Type;

        [FieldOffset(32)]
        public nint Window;

        [FieldOffset(40)]
        public nint MessageType;

        [FieldOffset(48)]
        public int Format;

        [FieldOffset(56)]
        public long Source;

        [FieldOffset(64)]
        public long Timestamp;

        [FieldOffset(72)]
        public long Requestor;
    }

    [LibraryImport(LibX11)]
    private static partial nint XOpenDisplay(nint name);

    [LibraryImport(LibX11)]
    private static partial int XCloseDisplay(nint display);

    [LibraryImport(LibX11)]
    private static partial int XSync(nint display, int discard);

    [LibraryImport(LibX11)]
    private static partial int XFlush(nint display);

    [LibraryImport(LibX11)]
    private static partial nint XDefaultRootWindow(nint display);

    [LibraryImport(LibX11, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint XInternAtom(nint display, string name, int onlyIfExists);

    [LibraryImport(LibX11)]
    private static partial int XGetWindowProperty(
        nint display, nint window, nint property, long offset, long length, int delete,
        nint requestedType, out nint actualType, out int actualFormat, out nuint itemCount,
        out nuint bytesAfter, out nint data);

    [LibraryImport(LibX11)]
    private static partial int XFree(nint data);

    [LibraryImport(LibX11)]
    private static partial int XSendEvent(
        nint display, nint window, int propagate, nint eventMask, ref XClientMessageEvent message);

    [LibraryImport(LibX11)]
    private static partial nint XSetErrorHandler(nint handler);
}
