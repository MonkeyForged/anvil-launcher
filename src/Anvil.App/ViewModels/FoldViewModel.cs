using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Anvil.App.ViewModels;

/// <summary>
/// The heading row of one fold in the right column (spec s11.1): title, open/closed state, and
/// what stays visible while it is closed — a value and a dot for an unsaved wish.
///
/// <para>
/// <b>Holds no state of its own.</b> The open state lives in the <see cref="FoldMemory"/>, which
/// outlives a game switch; value, dot and dimming are read through delegates on every access,
/// because their sources (pending changes, the selected choice) change elsewhere. Whoever changes
/// such a source calls <see cref="Sync"/>.
/// </para>
/// </summary>
public sealed partial class FoldViewModel : ObservableObject
{
    private readonly FoldMemory _memory;
    private readonly string _key;
    private readonly Func<bool> _hasUnsavedChange;
    private readonly Func<string?>? _value;
    private readonly Func<bool>? _isMuted;

    /// <param name="memory">Where the open state is kept.</param>
    /// <param name="key">The fold's key in <paramref name="memory"/>.</param>
    /// <param name="title">The label of the heading row.</param>
    /// <param name="hasUnsavedChange">Is an unsaved wish of this game behind the fold?</param>
    /// <param name="value">The value shown right while collapsed, or null (no value).</param>
    /// <param name="isMuted">Label and value dimmed (native/unknown platform).</param>
    public FoldViewModel(
        FoldMemory memory,
        string key,
        string title,
        Func<bool> hasUnsavedChange,
        Func<string?>? value = null,
        Func<bool>? isMuted = null)
    {
        _memory = memory;
        _key = key;
        Title = title;
        _hasUnsavedChange = hasUnsavedChange;
        _value = value;
        _isMuted = isMuted;
    }

    public string Title { get; }

    public bool IsExpanded => _memory.IsOpen(_key);

    /// <summary>The dot says "there is something unsaved in here" — only while the fold hides it.</summary>
    public bool ShowsUnsavedDot => !IsExpanded && _hasUnsavedChange();

    public string? Value => _value?.Invoke();

    /// <summary>An open fold shows its content instead of the value.</summary>
    public bool ShowsValue => !IsExpanded && Value is not null;

    public bool IsMuted => _isMuted?.Invoke() ?? false;

    /// <summary>Re-reads everything: call after a source behind a delegate changed.</summary>
    public void Sync()
    {
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(ShowsUnsavedDot));
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(ShowsValue));
        OnPropertyChanged(nameof(IsMuted));
    }

    [RelayCommand]
    private void Toggle()
    {
        _memory.SetOpen(_key, !IsExpanded);
        Sync();
    }
}
