namespace Anvil.App.Views;

/// <summary>
/// Where a dialog window stands, kept apart from the window so it can be tested without one
/// (story s14.1, I-1): closed, open, or being closed because its state turned closed. It tells
/// <see cref="DialogPresenter"/> what each event means, so that cancel runs only when the user
/// closed the window, and at most once per window.
/// </summary>
public sealed class DialogLifecycle
{
    private enum Phase
    {
        Closed,
        Open,
        ClosingByState,
    }

    private Phase _phase = Phase.Closed;

    /// <summary>Whether a window is shown — also while it is being closed by the state.</summary>
    public bool IsWindowShown => _phase != Phase.Closed;

    /// <summary>The state turned open. Returns whether a window is to be shown: not while one is.</summary>
    public bool Open()
    {
        if (_phase != Phase.Closed)
        {
            return false;
        }

        _phase = Phase.Open;
        return true;
    }

    /// <summary>The state turned closed. Returns whether the shown window is to be closed.</summary>
    public bool CloseByState()
    {
        if (_phase != Phase.Open)
        {
            return false;
        }

        _phase = Phase.ClosingByState;
        return true;
    }

    /// <summary>
    /// The window has closed, by whatever means. Returns whether the user closed it (frame X,
    /// Esc, Alt+F4) — then the dialog's cancel is due; a close the state asked for is none.
    /// </summary>
    public bool WindowClosed()
    {
        var isClosedByUser = _phase == Phase.Open;
        _phase = Phase.Closed;
        return isClosedByUser;
    }
}
