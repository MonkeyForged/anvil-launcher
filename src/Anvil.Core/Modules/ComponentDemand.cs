using Anvil.Core.Prefixes;

namespace Anvil.Core.Modules;

/// <summary>
/// Ein Bedarf der Fläche <c>Dependencies</c> (Spec S3.5b §3.1): eine Komponente, und <b>alle</b>
/// registrierten Module, die sie fordern — unabhängig vom Schalter (E-2).
/// </summary>
/// <param name="Required">Die Forderung des zuerst registrierten Moduls (E-3).</param>
/// <param name="ModuleKeys">Alle fordernden Module, in Registrierungsreihenfolge.</param>
public sealed record ComponentDemand(PrefixComponent Required, IReadOnlyList<string> ModuleKeys);
