using Anvil.Core.Modules;
using Microsoft.Extensions.Logging;

namespace Anvil.App.ViewModels;

/// <summary>The global module level: one row per registry entry, in registry order.</summary>
public sealed class ModulesViewModel : PageViewModel
{
    private readonly ModuleRegistry _registry;
    private readonly Action<Action> _post;
    private readonly ILogger? _logger;

    public ModulesViewModel(ModuleRegistry registry, Action<Action> post, ILogger? logger = null)
    {
        _registry = registry;
        _post = post;
        _logger = logger;

        Modules = registry.Modules
            .Select(module => new ModuleItemViewModel(module, registry, TriggerCheck))
            .ToList();

        // The report path comes from the background (Spec §3.4): a module's check is foreign
        // code and may fire on a foreign thread. Jump to the UI thread first, then ask the row
        // concerned — the same shape as LogViewModel and LibraryViewModel.
        _registry.ReadinessChanged += OnReadinessChanged;

        // The page starts the first check, not the registry (Spec E-5): SetEnabled stays
        // synchronous and calls nothing on its own, or a task would run that nobody holds and
        // whose exceptions nobody sees. That is exactly one place when the page is built (here,
        // for every non-core module already switched on) and one when the switch is flipped
        // (ModuleItemViewModel.IsEnabled). Core modules are never asked — their row carries
        // `Core` and knows no finding (Spec E-9).
        foreach (var module in registry.Modules)
        {
            if (!module.IsCore && registry.IsEnabled(module.Key))
            {
                TriggerCheck(module.Key);
            }
        }
    }

    public override string Title => "Modules";

    public override string Subtitle =>
        "Installed, enabled, dependencies. Active modules appear in the library's right column.";

    /// <summary>Never empty: the registry always carries the core entry (design request 5).</summary>
    public override EmptyState? EmptyState => null;

    public IReadOnlyList<ModuleItemViewModel> Modules { get; }

    private void OnReadinessChanged(object? sender, string key)
        => _post(() => Modules.FirstOrDefault(module => module.Key == key)?.NotifyReadinessChanged());

    /// <summary>
    /// Starts the check as a task of its own and observes its exception instead of using
    /// <c>async void</c>: an unobserved exception would be a bug in this project, not a side
    /// note. <see cref="ModuleRegistry.CheckAsync"/> already catches exceptions from the module
    /// itself (Spec E-7) — this catch is for the rare rest, such as a subscriber of
    /// <see cref="ModuleRegistry.ReadinessChanged"/> that throws.
    /// </summary>
    private void TriggerCheck(string key) => _ = CheckSafelyAsync(key);

    private async Task CheckSafelyAsync(string key)
    {
        try
        {
            await _registry.CheckAsync(key);
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "module {Key} readiness check failed", key);
        }
    }
}
