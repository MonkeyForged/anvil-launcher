namespace Anvil.Core.Modules;

/// <summary>
/// What a module wants started in a running game's container (Spec S4.4 §3.2) — the first
/// contract member of the bridge seam.
///
/// <para>
/// <b>Three fields, and no fourth.</b> The call needs no more (project plan M4, decision 2: the
/// seam grows only as far as this story needs it). Whoever wants a field added here has either
/// a new caller or a speculative abstraction.
/// </para>
///
/// <para>
/// <b>Checked in <see cref="AttachDeclaration.Start"/></b>, not here: a record's field
/// initializer does not run again on <c>with</c>, and exactly this trap already struck once in
/// S4.1.
/// </para>
/// </summary>
/// <param name="WorkingDirectory">
/// The working folder <b>inside the container</b> — it becomes <c>--directory=</c> at the start
/// client. It is why Proton's <c>vkd3d-proton.cache</c> ends up in the program folder and
/// nowhere else (messungen-anhaengen.md §5).
/// </param>
/// <param name="ExecutablePath">
/// The <b>Linux path</b> of the program. <c>wine start /unix</c> turns it into the Windows path
/// <c>Z:\…</c> inside the container; Anvil does not build that itself (Spec E-8).
/// </param>
/// <param name="Arguments">
/// What follows the program, one entry per argument — no shell in between, so nothing is quoted.
/// Wand Bridge passes the address that opens Wand on the game (story s7.1), or nothing.
/// </param>
public sealed record AttachProgram(
    string WorkingDirectory,
    string ExecutablePath,
    IReadOnlyList<string> Arguments);
