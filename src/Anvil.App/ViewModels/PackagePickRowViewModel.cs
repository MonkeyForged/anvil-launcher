using Anvil.Core.Packages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Anvil.App.ViewModels;

/// <summary>
/// The selection row of a module card with state (Spec S3.5b §3.7, build plan §8.2): title and
/// subline on the left, below them the box with field, state, sentence, action and expandable
/// file list. Replaces the stateless selection row from S3.4 (<c>PackageRowViewModel</c>).
///
/// <para>
/// <b>It reads through <see cref="IPackageChoices"/></b> (s9.1 E-6), not from a concrete store:
/// Wand's store lists single files, CE Bridge's lists folders and zips — one state logic for both.
/// What differs per module is switched per row: the vendor builds (<see cref="IVendorBuilds"/>,
/// null = none), the sentence for an empty list (<c>emptyNote</c>, "" = none) and the file list
/// (<see cref="ShowsFileList"/>).
/// </para>
///
/// <para>
/// <b>Vendor builds</b> (Spec S3.5b §3.7, s9.3 E-11): below the folder files stand the offers
/// that do <i>not</i> lie in the store (<see cref="PackageChoice.Vendor"/>). A click on one does
/// not choose but obtains — and only <b>after success</b> does it choose. <b>How</b> it is
/// obtained is the module's way, not the row's: Wand fetches (<see cref="FetchedVendorBuilds"/>),
/// CE Bridge asks, downloads, runs the installer and checks. The row draws the stages
/// (<see cref="StateText"/>, <see cref="Note"/>, <see cref="ShowsStageCancel"/>) and the download
/// with bar, cancel and red sentence (<see cref="Fetch"/>), the same mechanics as in the entry of
/// the <c>Dependencies</c> surface.
/// </para>
///
/// <para>
/// <b>Taken over from S3.4, unchanged:</b> re-reading on expanding only when names changed,
/// silencing the reporting paths during construction and re-read (<see cref="_syncing"/>), and
/// the fallback to <c>none selected</c> when the ComboBox reports <c>null</c>.
/// </para>
///
/// <para>
/// <b>It stands next to the section, not inside it</b> (instead of a nested class): the view needs
/// the type for its <c>DataTemplate</c>, and a nested type is called <c>Outer+Inner</c> in XAML.
/// The same shape as <see cref="ProtonChoice"/> next to <see cref="ProtonSectionViewModel"/>.
/// </para>
/// </summary>
public sealed partial class PackagePickRowViewModel : ObservableObject
{
    /// <summary>
    /// The topmost entry of the list — <c>none selected</c>, muted, with the separator line below
    /// it. It stands where the Proton list has <c>Auto</c>, and for the same reason: the line says
    /// that this entry is not a file, but its absence.
    /// </summary>
    public const string NoneLabel = "none selected";

    /// <summary>
    /// The empty sentence of the open list (mockup, <c>selEmptyStyle</c>) — <b>without a period at
    /// the end</b>, delivered exactly like that.
    /// </summary>
    public const string EmptyNote = "No packages found — copy them into the storage location";

    /// <summary>State: a file is chosen (muted, Spec S3.5b §3.7).</summary>
    public const string StateReady = "ready to attach";

    /// <summary>State: the folder is empty (warning).</summary>
    public const string StateEmpty = "nothing here yet";

    /// <summary>State: files lie there, none is chosen (warning).</summary>
    public const string StateNothingSelected = "nothing selected";

    /// <summary>State while fetching (Ember, dot).</summary>
    public const string StateFetching = "fetching";

    /// <summary>State while the vendor's installer is open (Ember, pulsing dot; spec s9.3).</summary>
    public const string StateInstallerOpen = "installer open";

    /// <summary>State while the obtained program is checked (pulsing dot; spec s9.3).</summary>
    public const string StateChecking = "checking";

    /// <summary>
    /// The sentence when something is chosen <b>and</b> a vendor build is missing (Spec S3.5b
    /// §3.7, E-13). <b>Wording per drawing, reported as request 44:</b> Anvil does not know
    /// "newer" — it compares no versions, it only knows that a catalog build is not in the folder.
    /// </summary>
    public static string VendorNoteFor(string folder)
        => $"A newer build is available from the vendor. It lands in {folder} and stays selectable.";

    /// <summary>The sentence while fetching (Spec S3.5b §3.7).</summary>
    public static string FetchingNoteFor(string fileName, string folder)
        => $"Fetching {fileName} into {folder}.";

    /// <summary>The action that obtains a missing vendor build: <c>Get WeMod 11.6.0</c>,
    /// <c>Get CE 7.7</c>.</summary>
    public static string GetTextFor(VendorOffer offer) => "Get " + offer.DisplayName;

    /// <summary>What the row lists and where it writes the choice.</summary>
    private readonly IPackageChoices _packages;

    private readonly IVendorBuilds? _vendor;
    private readonly string _folderText;
    private readonly string _emptyNote;
    private readonly Action _requestCheck;
    private readonly Action<Action> _post;
    private readonly ILogger? _logger;

    /// <summary>
    /// Holds the reporting paths while the row sets its own fields — during construction and
    /// re-read. <b>Setting the selection during construction is not the user's wish</b> and must
    /// therefore neither write nor trigger a check; the same shape and the same reasoning as in
    /// <see cref="ProtonSectionViewModel"/>.
    /// </summary>
    private bool _syncing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFiles), nameof(Files), nameof(FilesToggleText))]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsStateWarning), nameof(Note), nameof(NoteEmphasis))]
    [NotifyPropertyChangedFor(nameof(IsNoteSecondary), nameof(IsNoteBright))]
    [NotifyPropertyChangedFor(nameof(ActionText), nameof(IsActionEnabled))]
    [NotifyCanExecuteChangedFor(nameof(ActionCommand))]
    private IReadOnlyList<PackageChoice> _choices;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Files))]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsStateWarning), nameof(Note), nameof(NoteEmphasis))]
    [NotifyPropertyChangedFor(nameof(IsNoteSecondary), nameof(ActionText), nameof(IsActionEnabled))]
    [NotifyCanExecuteChangedFor(nameof(ActionCommand))]
    private PackageChoice _selectedChoice;

    /// <summary>
    /// Is the list open? <b>Bound to <c>ComboBox.IsDropDownOpen</c></b>, and the re-read hangs on
    /// exactly that — see <see cref="OnIsListOpenChanged"/>.
    /// </summary>
    [ObservableProperty]
    private bool _isListOpen;

    /// <summary>Is the file list below the box open? Transient, closed by default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilesToggleText))]
    private bool _isFilesOpen;

    /// <param name="label">Title on the left, e.g. <c>Wand version</c>.</param>
    /// <param name="sub">Subline on the left.</param>
    /// <param name="boxName">The name above the field in the box, e.g. <c>Wand application</c>.</param>
    /// <param name="folderText">
    /// The folder as the sentences name it (<c>wand_bridge/wand/</c>) — relative to the data root,
    /// not the full path: that stands under <c>Data location</c>.
    /// </param>
    /// <param name="emptyNote">The sentence below the box while nothing is listed; "" = no
    /// sentence. Wand passes <see cref="EmptyFolderNoteFor"/>.</param>
    /// <param name="choices">What the row reads and where it writes the choice.</param>
    /// <param name="vendor">
    /// The vendor's offers and the module's way of obtaining them, with its download — passed by
    /// the module that knows them; the row knows no catalog. null = no vendor entries, no
    /// <c>Get …</c> action, no download.
    /// </param>
    /// <param name="showsFileList">false hides the toggle <c>Show n files</c> and the file
    /// list.</param>
    /// <param name="post">
    /// Marshals onto the UI thread — here for the jump back after a click on a vendor build
    /// (<see cref="OnSelectedChoiceChanged(PackageChoice, PackageChoice)"/>).
    /// </param>
    /// <param name="requestCheck">Triggers the check of the own module — the coupling (S3.4 §3.7).</param>
    /// <param name="logger">null means silent.</param>
    public PackagePickRowViewModel(
        string label, string sub, string boxName, string folderText,
        string emptyNote,
        IPackageChoices choices,
        IVendorBuilds? vendor,
        bool showsFileList,
        Action<Action> post, Action requestCheck, ILogger? logger = null)
    {
        Fetch = vendor?.Fetch;
        CancelStageCommand = vendor?.CancelStageCommand;
        _post = post;
        _vendor = vendor;
        Label = label;
        Sub = sub;
        BoxName = boxName;
        _folderText = folderText;
        _emptyNote = emptyNote;
        _packages = choices;
        ShowsFileList = showsFileList;
        _requestCheck = requestCheck;
        _logger = logger;

        // Straight into the fields and not via the properties: through them the reporting path
        // would run, and building a card is not the user's wish.
        _choices = Build(_packages.Names());
        _selectedChoice = ChoiceFor(_packages.Selected);

        if (vendor is not null)
        {
            vendor.PropertyChanged += (_, args) =>
            {
                // A new stage or a new sentence changes state, sentence and action.
                if (args.PropertyName is nameof(IVendorBuilds.Stage) or nameof(IVendorBuilds.StageNote)
                    or nameof(IVendorBuilds.BlockedNote))
                {
                    NotifyVendorStateChanged();
                }
            };
        }
    }

    /// <summary>This row's download (Spec S3.5b §3.7, "loading, errors as in the entry"); null
    /// for a row without vendor builds.</summary>
    public FetchProgressViewModel? Fetch { get; }

    /// <summary>The <c>Cancel</c> while the installer is open (<see cref="ShowsStageCancel"/>);
    /// null for a row without vendor builds.</summary>
    public IRelayCommand? CancelStageCommand { get; }

    /// <summary>Is a vendor build being obtained? Then the row shows the stage, a dot, and no
    /// action.</summary>
    public bool IsBusy => Stage != VendorStage.Idle;

    /// <summary>
    /// Is it a stage one waits for — installer open, checking? Then the dot pulses (design 27,
    /// Settings-CeBezug); while fetching it stands still, since the bar already moves.
    /// </summary>
    public bool IsWaiting => Stage is VendorStage.InstallerOpen or VendorStage.Checking;

    /// <summary>Does the row show the stage's own <c>Cancel</c>? Only while the installer is open
    /// — the download has its own next to the bar.</summary>
    public bool ShowsStageCancel => Stage == VendorStage.InstallerOpen;

    /// <summary>
    /// Does the state stand in Ember? While fetching and while the installer is open — the two
    /// stages where something happens because of the user's click. <c>checking</c> stands muted
    /// with its pulsing dot: Anvil works on its own there (Settings-CeBezug).
    /// </summary>
    public bool IsStateActive => Stage is VendorStage.Fetching or VendorStage.InstallerOpen;

    public string Label { get; }

    public string Sub { get; }

    public string BoxName { get; }

    /// <summary>
    /// Does the empty sentence stand muted instead of in <c>Secondary</c>? CE Bridge's does, as
    /// drawn (design 03d): it describes what <c>Get</c> will do rather than being the only way
    /// forward. Off by default, so Wand's row stays as it is.
    /// </summary>
    public bool IsEmptyNoteMuted { get; init; }

    /// <summary>Does the row show the toggle <c>Show n files</c> and the file list below the box?
    /// Fixed per row (s9.1 I-3): CE Bridge's row has neither (design 58).</summary>
    public bool ShowsFileList { get; }

    /// <summary>Does at least one file lie in the folder? Then there is a toggle and a choice.</summary>
    public bool HasFiles => FirstFile is not null;

    /// <summary>
    /// The state on the right in the box (Spec S3.5b §3.7). The order of the questions is that of
    /// the table: an empty folder is <c>nothing here yet</c>, even if an old choice is stored —
    /// <see cref="IPackageChoices.Selected"/> checks against the listed names anyway. Fetching
    /// comes before everything else, and so does every later stage of obtaining (spec s9.3).
    /// </summary>
    public string StateText
        => Stage switch
        {
            VendorStage.Fetching => StateFetching,
            VendorStage.InstallerOpen => StateInstallerOpen,
            VendorStage.Checking => StateChecking,
            _ => !HasFiles ? StateEmpty
                : SelectedChoice.IsNone ? StateNothingSelected
                : StateReady,
        };

    /// <summary>
    /// Warning for <c>nothing here yet</c> and <c>nothing selected</c>: in both cases the module
    /// is stuck on this row, and the badge on the Modules page says <c>not ready</c>.
    /// </summary>
    public bool IsStateWarning => !IsBusy && (!HasFiles || SelectedChoice.IsNone);

    /// <summary>The sentence below the box — see <see cref="CurrentNote"/>.</summary>
    public string Note => CurrentNote.Text;

    /// <summary>The word the view sets apart in <see cref="Note"/>; "" = none (spec s9.3:
    /// <c>Decline</c>, <c>bubblewrap</c>).</summary>
    public string NoteEmphasis => CurrentNote.Emphasis;

    /// <summary>
    /// The sentence stands in <c>Secondary</c> when it is the only way forward — empty folder and
    /// while fetching —, otherwise muted (Spec S3.5b §3.7). Not when it stands bright
    /// (<see cref="IsNoteBright"/>) and not when the row mutes its empty sentence
    /// (<see cref="IsEmptyNoteMuted"/>); while checking it stays muted, as drawn.
    /// </summary>
    public bool IsNoteSecondary
        => !IsNoteBright && (Stage == VendorStage.Fetching || (!IsBusy && !HasFiles && !IsEmptyNoteMuted));

    /// <summary>
    /// Does the sentence stand bright? When it is an instruction to the user: the one while the
    /// installer is open, and the blocked note — "bright, not red, the same colour as the
    /// instruction at installer open" (design 65, delivery §3.5).
    /// </summary>
    public bool IsNoteBright
        => Stage == VendorStage.InstallerOpen || (!IsBusy && !HasFiles && IsBlocked);

    /// <summary>
    /// The label of the action button; empty means no button (Spec S3.5b §3.7): none while
    /// obtaining; if a vendor build is missing, <c>Get &lt;display name&gt;</c> for the first in
    /// catalog order; otherwise <c>Use &lt;first file&gt;</c>, as long as nothing is chosen.
    /// </summary>
    public string ActionText
        => IsBusy ? string.Empty
            : MissingVendor is { } offer ? GetTextFor(offer)
            : FirstFile is { } first && SelectedChoice.IsNone ? UseTextFor(first.Name)
            : string.Empty;

    /// <summary>
    /// Can the action run? Not while it is <c>Get</c> and the vendor builds are blocked — the
    /// button then stands disabled (design 65), and the blocked note says why.
    /// </summary>
    public bool IsActionEnabled => !(IsGetAction && IsBlocked);

    /// <summary>The files in the folder, the chosen one marked as <c>in use</c>.</summary>
    public IReadOnlyList<PickFileViewModel> Files
        => Choices
            .Where(choice => !choice.IsNone && !choice.IsVendor)
            .Select(choice => new PickFileViewModel(
                choice.Name,
                !SelectedChoice.IsNone
                    && string.Equals(choice.Name, SelectedChoice.Name, StringComparison.Ordinal)))
            .ToList();

    /// <summary>The toggle: <c>Show 1 file</c> / <c>Hide 3 files</c>.</summary>
    public string FilesToggleText => FilesToggleTextFor(IsFilesOpen, Files.Count);

    /// <summary>The sentence for an empty folder (Spec S3.5b §3.7).</summary>
    public static string EmptyFolderNoteFor(string folder)
        => $"Nothing in {folder} yet. Get a build from the vendor, or put one there yourself — Anvil lists what it finds.";

    /// <summary>The sentence when files lie there but none is chosen (Spec S3.5b §3.7).</summary>
    public static string PickNoteFor(string folder)
        => $"Pick the build you want to attach. Anvil lists what lies in {folder}.";

    /// <summary>The action that chooses the first file.</summary>
    public static string UseTextFor(string fileName) => "Use " + fileName;

    /// <summary>The toggle's text.</summary>
    public static string FilesToggleTextFor(bool isOpen, int count)
        => $"{(isOpen ? "Hide" : "Show")} {count} {(count == 1 ? "file" : "files")}";

    private PackageChoice? FirstFile
        => Choices.FirstOrDefault(choice => !choice.IsNone && !choice.IsVendor);

    /// <summary>The first vendor build that does not lie in the folder, in catalog order.</summary>
    private VendorOffer? MissingVendor
        => Choices.FirstOrDefault(choice => choice.IsVendor)?.Vendor;

    private VendorStage Stage => _vendor?.Stage ?? VendorStage.Idle;

    /// <summary>Is the action <c>Get …</c> right now?</summary>
    private bool IsGetAction => !IsBusy && MissingVendor is not null;

    /// <summary>Does the machine lack something obtaining needs (TD-15)?</summary>
    private bool IsBlocked => _vendor is { BlockedNote.Text.Length: > 0 };

    /// <summary>
    /// The sentence below the box (Spec S3.5b §3.7, s9.3): while obtaining the stage's own; with
    /// nothing listed the blocked note if there is one, else the empty sentence; then nothing
    /// chosen, chosen with a missing vendor build; otherwise none. The last two exclude each
    /// other.
    /// </summary>
    private EmphasizedText CurrentNote
        => IsBusy ? _vendor!.StageNote
            : !HasFiles ? (IsBlocked ? _vendor!.BlockedNote : new EmphasizedText(_emptyNote))
            : SelectedChoice.IsNone ? new EmphasizedText(PickNoteFor(_folderText))
            : MissingVendor is not null ? new EmphasizedText(VendorNoteFor(_folderText))
            : EmphasizedText.Empty;

    /// <summary>
    /// Re-reads what lies in the store — on expanding the list and on showing the page (Spec
    /// S3.5b §3.7).
    ///
    /// <para>
    /// <b>Why it re-reads at all:</b> there is no watcher on the folder (S3.3, deliberately), and
    /// the user drops packages into it in the file manager <i>while</i> Anvil is running.
    /// </para>
    ///
    /// <para>
    /// <b>Only when something really changed.</b> Swapping the list at runtime is the delicate
    /// part: the ComboBox briefly loses its selection and reports <c>null</c>. The normal case —
    /// nothing changed — therefore touches nothing, and the swap itself runs under
    /// <see cref="_syncing"/>, so that neither a write nor a check happens. <b>Only folder files
    /// are compared:</b> the vendor entries follow from them and the fixed catalog — if they
    /// counted, every expanding would swap the list.
    /// </para>
    /// </summary>
    public void Reload()
    {
        // Always, before the early return: what blocks Get (a missing tool) can change while the
        // store does not.
        _vendor?.Refresh();

        var names = _packages.Names();
        if (names.SequenceEqual(
                Choices.Where(choice => !choice.IsNone && !choice.IsVendor).Select(choice => choice.Name),
                StringComparer.Ordinal))
        {
            return;
        }

        _syncing = true;
        try
        {
            Choices = Build(names);
            SelectedChoice = ChoiceFor(_packages.Selected);
        }
        finally
        {
            _syncing = false;
        }

        _logger?.LogDebug("package list reloaded: {Label} ({Count} files)", Label, names.Count);
    }

    /// <summary>
    /// The box's action, per <see cref="ActionText"/>: <c>Get …</c> obtains the first missing
    /// vendor build and chooses it after success — <b>the same path as a click on it in the
    /// field</b>; <c>Use …</c> chooses the first file — the same path as a choice in the field
    /// (<see cref="OnSelectedChoiceChanged(PackageChoice, PackageChoice)"/>). Without an action
    /// the command does nothing; the view then shows no button.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsActionEnabled))]
    private async Task ActionAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (MissingVendor is { } offer)
        {
            await ObtainAndSelectAsync(offer);
        }
        else if (FirstFile is { } first && SelectedChoice.IsNone)
        {
            SelectedChoice = first;
        }
    }

    /// <summary>
    /// Obtains a vendor build the module's way and chooses it <b>after success</b> (Spec S3.5b
    /// §3.7, s9.3 E-11): re-read, write, set the field and <i>one</i> check — like a choice in the
    /// field. On failure or cancel it only re-reads; the old choice stays, the red sentence is
    /// shown by <see cref="Fetch"/>. <b>Blocked or already busy, it does nothing</b> — no second
    /// run beside the first.
    /// </summary>
    private async Task ObtainAndSelectAsync(VendorOffer offer)
    {
        // A vendor entry only exists in the list when the row has vendor builds (Build).
        if (_vendor is not { } vendor || IsBlocked || IsBusy)
        {
            return;
        }

        _logger?.LogInformation("vendor build requested: {Label} → {Name}", Label, offer.Name);

        var isObtained = await vendor.ObtainAsync(offer);
        Reload();

        if (!isObtained)
        {
            return;
        }

        _packages.Select(offer.Name);
        _syncing = true;
        try
        {
            SelectedChoice = ChoiceFor(offer.Name);
        }
        finally
        {
            _syncing = false;
        }

        _requestCheck();
        _logger?.LogInformation("package choice: {Label} → {Name}", Label, offer.Name);
    }

    /// <summary>Reports everything a new stage or a new sentence of the vendor builds changes.</summary>
    private void NotifyVendorStateChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(ShowsStageCancel));
        OnPropertyChanged(nameof(IsStateActive));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsStateWarning));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(NoteEmphasis));
        OnPropertyChanged(nameof(IsNoteSecondary));
        OnPropertyChanged(nameof(IsNoteBright));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(IsActionEnabled));
        ActionCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsListOpenChanged(bool value)
    {
        if (value)
        {
            Reload();
        }
    }

    /// <summary>
    /// A choice writes the selection at once (<see cref="IPackageChoices.Select"/>, S3.3) and
    /// triggers <i>exactly one</i> check; via <c>ReadinessChanged</c> the new finding then reaches
    /// the Modules row and the library (S3.4 §3.7).
    ///
    /// <para>
    /// <b>A vendor build is not chosen but obtained</b> (Spec S3.5b §3.7): right after the
    /// assignment the field jumps back to the previous choice and keeps it until success; while
    /// obtaining runs or while it is blocked, it stays with the jump back.
    /// </para>
    /// </summary>
    partial void OnSelectedChoiceChanged(PackageChoice? oldValue, PackageChoice newValue)
    {
        var value = newValue;
        if (value is null)
        {
            // The ComboBox reports null when its selection drops away — on swapping the list in
            // Reload. That means "no choice", so the topmost entry. This check comes before the
            // one on _syncing, because otherwise the reset would not happen and the row would
            // stand empty.
            SelectedChoice = Choices[0];
            return;
        }

        if (_syncing)
        {
            return;
        }

        if (value.Vendor is { } offer)
        {
            // The jump back happens AFTER the assignment, via the UI thread: a change that
            // arrives while the ComboBox's assignment is still running is ignored by its binding
            // — the closed field would keep showing the vendor build although the row held the old
            // choice (headless probe, S3.5b task 11). Only if nothing else has been chosen yet: a
            // fast obtain must not lose its choice.
            var restore = oldValue is { IsVendor: false } ? oldValue : Choices[0];
            _post(() =>
            {
                if (!ReferenceEquals(SelectedChoice, value))
                {
                    return;
                }

                _syncing = true;
                try
                {
                    SelectedChoice = restore;
                }
                finally
                {
                    _syncing = false;
                }
            });

            _ = ObtainAndSelectAsync(offer);
            return;
        }

        // The topmost entry is not a file: it clears the selection.
        _packages.Select(value.IsNone ? null : value.Name);
        _requestCheck();

        _logger?.LogInformation(
            "package choice: {Label} → {Name}", Label, value.IsNone ? "none" : value.Name);
    }

    /// <summary>
    /// The topmost entry, then the files in the order of the store — already sorted there (S3.3).
    ///
    /// <para>
    /// Then the vendor offers whose name does <b>not</b> lie in the store — if it does, it stands
    /// only once, at the top among the folder files (Spec S3.5b §3.7). Another release in the
    /// store does not count: <c>Get CE 7.7</c> stands beside a <c>7.6</c> (TD-6).
    /// </para>
    /// </summary>
    private IReadOnlyList<PackageChoice> Build(IReadOnlyList<string> names)
    {
        var missing = (_vendor?.Offers ?? [])
            .Where(offer => !names.Contains(offer.Name, StringComparer.Ordinal))
            .ToList();

        var choices = new List<PackageChoice>(names.Count + missing.Count + 1)
        {
            // The topmost entry carries the empty sentence when there is nothing to choose and
            // nothing to fetch — then it is the only entry, and the open list itself says what is
            // missing.
            new(NoneLabel, IsNone: true, StoreIsEmpty: names.Count == 0 && missing.Count == 0),
        };

        foreach (var name in names)
        {
            choices.Add(new PackageChoice(name, IsNone: false));
        }

        foreach (var offer in missing)
        {
            choices.Add(new PackageChoice(offer.Name, IsNone: false, Vendor: offer));
        }

        return choices;
    }

    /// <summary>
    /// The entry for a file name; without a name and without a match the topmost entry. No
    /// substitute entry as in the Proton list: <c>Selected</c> already checks against the store
    /// (S3.3, E-4).
    /// </summary>
    private PackageChoice ChoiceFor(string? name)
        => name is null
            ? Choices[0]
            : Choices.FirstOrDefault(
                choice => !choice.IsNone && !choice.IsVendor
                    && string.Equals(choice.Name, name, StringComparison.Ordinal))
                ?? Choices[0];
}

/// <summary>
/// A file of the expandable list below the selection box (Spec S3.5b §3.7): mono name on the
/// left, on the right whether it is the chosen one.
/// </summary>
/// <param name="Name">The file name in the store.</param>
/// <param name="InUse">Is it the chosen file?</param>
public sealed record PickFileViewModel(string Name, bool InUse)
{
    public string Status => InUse ? "in use" : "in the folder";
}
