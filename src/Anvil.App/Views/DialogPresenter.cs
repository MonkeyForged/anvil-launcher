using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Anvil.App.Views;

/// <summary>
/// Shows a dialog as a window of its own in the package's frame (story s14.1, I-1). It sits
/// invisibly in a view and follows <see cref="IsOpen"/>: true opens the window over the window
/// the presenter sits in, false closes it. Closing by the user — frame X, Esc, Alt+F4 — runs
/// <see cref="CancelCommand"/>, as the dialog's cancelling button would. One place for every
/// dialog window, so the view models keep their state and the views need no code-behind.
/// </summary>
public sealed class DialogPresenter : Control
{
    /// <summary>Whether the dialog is shown; usually bound to the view model's pending state.</summary>
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<DialogPresenter, bool>(nameof(IsOpen));

    /// <summary>Run when the user closes the window (frame X, Esc, Alt+F4), if it can execute.</summary>
    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<DialogPresenter, ICommand?>(nameof(CancelCommand));

    /// <summary>The window title; the frame shows it in capitals.</summary>
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<DialogPresenter, string>(nameof(Title), "Anvil");

    /// <summary>The width of the window's content; the height follows the content.</summary>
    public static readonly StyledProperty<double> DialogWidthProperty =
        AvaloniaProperty.Register<DialogPresenter, double>(nameof(DialogWidth), 376);

    /// <summary>The content of the window; its DataContext is the presenter's.</summary>
    public static readonly StyledProperty<object?> DialogContentProperty =
        AvaloniaProperty.Register<DialogPresenter, object?>(nameof(DialogContent));

    private readonly DialogLifecycle _lifecycle = new();
    private Window? _window;

    static DialogPresenter()
    {
        // Nothing of the presenter itself is drawn or takes the pointer; it only holds the window.
        IsVisibleProperty.OverrideDefaultValue<DialogPresenter>(false);
    }

    /// <summary>
    /// How a dialog window is shown: (dialog, owner) → a task that ends when it closes. Default:
    /// modal over the owner. The guide tool replaces it to render the window headless. The
    /// presenter follows the window's Closed event, not this task, so a replacement may return
    /// at once.
    /// </summary>
    public static Func<Window, Window, Task> ShowWindow { get; set; } =
        (dialog, owner) => dialog.ShowDialog(owner);

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public ICommand? CancelCommand
    {
        get => GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public double DialogWidth
    {
        get => GetValue(DialogWidthProperty);
        set => SetValue(DialogWidthProperty, value);
    }

    public object? DialogContent
    {
        get => GetValue(DialogContentProperty);
        set => SetValue(DialogContentProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsOpenProperty)
        {
            FollowIsOpen();
        }
    }

    // A state that turned open before the view stood in a window is caught up here.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        FollowIsOpen();
    }

    // A view that leaves the window takes its dialog along, without cancelling: the state still
    // says open, and the view that shows it next opens the window again.
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        CloseWindow();
    }

    private void FollowIsOpen()
    {
        if (IsOpen)
        {
            OpenWindow();
        }
        else
        {
            CloseWindow();
        }
    }

    private void OpenWindow()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || !_lifecycle.Open())
        {
            return;
        }

        _window = BuildWindow(owner);
        // The window's Closed event ends the dialog, so the task is not awaited (see ShowWindow).
        _ = ShowWindow(_window, owner);
    }

    private void CloseWindow()
    {
        if (_lifecycle.CloseByState())
        {
            _window?.Close();
        }
    }

    /// <summary>
    /// The tallest a dialog's content may be on a screen, so that content and frame fit its working
    /// area (taskbar and panels excluded) — the content scrolls beyond it.
    /// </summary>
    /// <param name="workingAreaHeight">The working area's height in physical pixels.</param>
    /// <param name="scaling">The screen's scaling: physical pixels per device-independent pixel.</param>
    /// <returns>The maximum content height in device-independent pixels.</returns>
    public static double MaxContentHeight(int workingAreaHeight, double scaling)
        => (workingAreaHeight / scaling) - StartWindowSize.FrameSize.Height;

    private Window BuildWindow(Window owner)
    {
        // A 768-px laptop screen is shorter than About; without a screen nothing is capped.
        var screen = owner.Screens.ScreenFromWindow(owner);
        var window = new Window
        {
            Title = Title,
            Width = DialogWidth,
            MaxHeight = screen is null
                ? double.PositiveInfinity
                : MaxContentHeight(screen.WorkingArea.Height, screen.Scaling),
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            CanMinimize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = DialogContent,
        };
        // Theme/Styles/Dialogs.axaml: the dialog's content stands in this window without a frame of its own.
        window.Classes.Add("dialog");
        window.DataContext = DataContext;
        window.KeyDown += CloseOnEscape;
        window.Closed += OnWindowClosed;
        return window;
    }

    private static void CloseOnEscape(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not Window window)
        {
            return;
        }

        e.Handled = true;
        window.Close();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.KeyDown -= CloseOnEscape;
            window.Closed -= OnWindowClosed;
            // The content goes back to the presenter: the next window takes the same control,
            // which may have one parent only.
            window.Content = null;
        }

        _window = null;
        if (_lifecycle.WindowClosed() && CancelCommand is { } cancel && cancel.CanExecute(null))
        {
            cancel.Execute(null);
        }
    }
}
