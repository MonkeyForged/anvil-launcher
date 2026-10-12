using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Anvil.App.ViewModels;

/// <summary>What the settings page's confirmation asks (design 61).</summary>
/// <param name="Title">The question, e.g. <c>Open the Cheat Engine installer?</c></param>
/// <param name="Text">The explanation below it, with its one emphasized word.</param>
/// <param name="CancelLabel">The outlined button; closing the dialog's window answers the same.</param>
/// <param name="ConfirmLabel">The accent button.</param>
public sealed record ConfirmationRequest(
    string Title, EmphasizedText Text, string CancelLabel, string ConfirmLabel);

/// <summary>
/// The settings page's confirmation (design 61): one question at a time, in a window of its own
/// over the main window (story s14.1), in the form of the stop confirmation in the library. <b>A module asks through a
/// delegate to <see cref="AskAsync"/></b> and never draws the dialog itself — modules report,
/// the core draws.
///
/// <para>
/// Every way out answers: <c>Confirm</c> with true, <c>Cancel</c> and closing the window (frame X,
/// Esc, Alt+F4) with false. A second question while one stands answers the first with false, so no caller waits
/// forever.
/// </para>
/// </summary>
public sealed partial class SettingsConfirmationViewModel : ObservableObject
{
    /// <summary>The answer the standing question waits for; null while none stands.</summary>
    private TaskCompletionSource<bool>? _answer;

    /// <summary>The standing question; null = the dialog is not shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    private ConfirmationRequest? _pending;

    /// <summary>Does a question stand? The view shows the dialog while it does.</summary>
    public bool IsPending => Pending is not null;

    /// <summary>Puts <paramref name="request"/> up and waits for the answer.</summary>
    /// <returns>true on Confirm; false on Cancel, a closed window, or a later question.</returns>
    public Task<bool> AskAsync(ConfirmationRequest request)
    {
        Answer(false);

        // Asynchronous continuations: the caller's code after the await must not run inside
        // Answer, while the dialog is still being taken down.
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _answer = answer;
        Pending = request;
        return answer.Task;
    }

    [RelayCommand]
    private void Confirm() => Answer(true);

    [RelayCommand]
    private void Cancel() => Answer(false);

    /// <summary>Takes the standing question down and answers it; without one, nothing.</summary>
    private void Answer(bool isConfirmed)
    {
        if (_answer is not { } answer)
        {
            return;
        }

        _answer = null;
        Pending = null;
        answer.TrySetResult(isConfirmed);
    }
}
