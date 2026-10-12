using Anvil.Core.Modules;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.App.ViewModels;

/// <summary>
/// One row of the Modules page. The enabled state is not held here but read from the registry
/// and written through it — it is the one source of truth, logs and saves. The readiness
/// finding (S3.4) lives in the registry as well; this row only reads it.
/// </summary>
public sealed class ModuleItemViewModel : ObservableObject
{
    private readonly IModule _module;
    private readonly ModuleRegistry _registry;

    /// <summary>
    /// Starts a check without this row needing <c>async void</c> or leaving an exception
    /// unobserved — <c>ModulesViewModel</c> hands in the delegate because it already needs the
    /// logger and the same operation for building the page (Spec §3.3, E-5: whoever switches on
    /// checks, and that is exactly this row and the page build, no third place).
    /// </summary>
    private readonly Action<string> _triggerCheck;

    public ModuleItemViewModel(IModule module, ModuleRegistry registry, Action<string> triggerCheck)
    {
        _module = module;
        _registry = registry;
        _triggerCheck = triggerCheck;
    }

    public string Key => _module.Key;

    public string DisplayName => _module.DisplayName;

    public string Description => _module.Description;

    /// <summary>
    /// The row's tag, in the order of Spec §3.4: `Core` for core entries, `off` for switched-off
    /// ones, `checking` while the registry holds no finding yet, otherwise `active` or
    /// `not ready` after the last stored finding (design request 27, colour values in the
    /// answer table).
    ///
    /// <para>
    /// <b>The order is the contract, not just readability.</b> A switched-off module shows
    /// `off` even if the registry still carries an older negative finding — that is why the
    /// switch case comes before the question about the finding, not after. A module one has just
    /// switched off must not suddenly look "broken".
    /// </para>
    ///
    /// <para>There is no "available" — modules are compiled in.</para>
    /// </summary>
    public string Tag
    {
        get
        {
            if (!CanToggle)
            {
                return "Core";
            }

            if (!IsEnabled)
            {
                return "off";
            }

            var readiness = _registry.ReadinessOf(Key);
            if (readiness is null)
            {
                return "checking";
            }

            return readiness.IsReady ? "active" : "not ready";
        }
    }

    /// <summary>
    /// The four tag classes for the surface (five cases). <b>Grey means off, everywhere</b>
    /// (s15.7): a core entry is always on, so it wears the active tone like an enabled, ready
    /// module — only its text differs. Separate bools instead of a state text, because Avalonia
    /// binds through <c>Classes.x="{Binding …}"</c> — the same shape as
    /// <c>Classes.is-running</c> in the library.
    /// </summary>
    public bool IsTagOff => CanToggle && !IsEnabled;

    /// <inheritdoc cref="IsTagOff"/>
    public bool IsTagActive => !CanToggle || (IsEnabled && _registry.ReadinessOf(Key) is { IsReady: true });

    /// <inheritdoc cref="IsTagOff"/>
    public bool IsTagChecking => CanToggle && IsEnabled && _registry.ReadinessOf(Key) is null;

    /// <inheritdoc cref="IsTagOff"/>
    public bool IsTagNotReady => CanToggle && IsEnabled && _registry.ReadinessOf(Key) is { IsReady: false };

    /// <summary>
    /// The reason line under the description appears only on a negative finding (design answer
    /// to 27) — the same condition as <see cref="IsTagNotReady"/>, under its own name because it
    /// binds at another place of the surface.
    /// </summary>
    public bool HasReason => IsTagNotReady;

    /// <summary>
    /// The module's sentence on why it is not ready — empty while <see cref="HasReason"/> is
    /// false. The sentence itself comes from the module (S3.4, Spec §3.8); this row invents none.
    /// </summary>
    public string Reason => IsTagNotReady ? _registry.ReadinessOf(Key)!.Reason ?? string.Empty : string.Empty;

    /// <summary>Core cannot be switched off; the switch is then locked.</summary>
    public bool CanToggle => !_module.IsCore;

    public bool IsEnabled
    {
        get => _registry.IsEnabled(Key);
        set
        {
            if (!CanToggle || value == IsEnabled)
            {
                return;
            }

            _registry.SetEnabled(Key, value);
            OnPropertyChanged();
            RaiseTagChanged();

            if (value)
            {
                // Whoever switches on checks (Spec §3.3, E-5) — the registry starts nothing on
                // its own. Off needs no check: the tag then does not even read the finding.
                _triggerCheck(Key);
            }
        }
    }

    /// <summary>
    /// Announces all tag and reason-line properties at once — one list, two callers: the row's
    /// own switch above and <see cref="NotifyReadinessChanged"/>, which <c>ModulesViewModel</c>
    /// calls when <c>ModuleRegistry.ReadinessChanged</c> concerns this row. So nobody maintains
    /// the same list in two places.
    /// </summary>
    private void RaiseTagChanged()
    {
        OnPropertyChanged(nameof(Tag));
        OnPropertyChanged(nameof(IsTagOff));
        OnPropertyChanged(nameof(IsTagActive));
        OnPropertyChanged(nameof(IsTagChecking));
        OnPropertyChanged(nameof(IsTagNotReady));
        OnPropertyChanged(nameof(HasReason));
        OnPropertyChanged(nameof(Reason));
    }

    /// <summary>
    /// This module's finding has changed. Public because the notice comes from outside:
    /// <c>ModulesViewModel</c> listens to <c>ModuleRegistry.ReadinessChanged</c> — which may fire
    /// on a foreign thread — and passes it to the affected row only after hopping to the UI
    /// thread.
    /// </summary>
    public void NotifyReadinessChanged() => RaiseTagChanged();
}
