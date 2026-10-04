using System.Collections.ObjectModel;
using System.Globalization;
using Scantron.Core.Models;
using Scantron.Core.Merge;
using Scantron.Desktop.Mvvm;
using Scantron.Desktop.Services;

namespace Scantron.Desktop.ViewModels;

/// <summary>
/// The application shell: owns the document under edit, the sync workflow and all commands.
/// </summary>
/// <remarks>
/// <para>
/// This is the only stateful piece. Everything it depends on - the file service, the workspace
/// store, the conflict resolver - is either stateless or trivially replaceable, and the merge
/// itself is delegated to <see cref="InventoryMerger"/>. The intent is that the sync rules stay
/// testable without WPF.
/// </para>
/// <para>
/// No dialogs or file pickers appear here either. Commands are invoked with a path and report
/// failures through <see cref="StatusMessage"/>, so the whole class is drivable from a test by
/// handing it paths directly.
/// </para>
/// </remarks>
public sealed class MainViewModel : ObservableObject
{
    private readonly InventoryFileService _files = new();
    private readonly WorkspaceStore _workspace;

    private InventoryDocument? _document;
    private InventoryDocument? _mergeCandidate;
    private MergeResult? _pendingMerge;
    private ContainerViewModel? _selectedContainer;
    private ConflictViewModel? _selectedConflict;
    private string _statusMessage = "Ready.";
    private string _syncState = "Not synced";
    private string? _lastExportPath;
    private bool _isBusy;

    public MainViewModel(WorkspaceStore? workspace = null)
    {
        _workspace = workspace ?? new WorkspaceStore();

        NewCommand = new RelayCommand(NewDocument);
        OpenDialogCommand = new RelayCommand(OpenWithDialog);
        SaveDialogCommand = new RelayCommand(SaveWithDialog, () => CanExport);
        ImportDialogCommand = new RelayCommand(ImportWithDialog, () => CanMerge);
        AddContainerCommand = new RelayCommand(AddContainer);
        RemoveContainerCommand = new RelayCommand(RemoveContainer, () => SelectedContainer is not null);
        AcceptMergeCommand = new RelayCommand(AcceptMerge, () => CanAcceptMerge);
        DiscardMergeCommand = new RelayCommand(DiscardMerge, () => _mergeCandidate is not null);
        TakeAllLocalCommand = new RelayCommand(
            () => ResolveAll(ConflictResolution.KeepLocal), () => CanBulkResolve);
        TakeAllRemoteCommand = new RelayCommand(
            () => ResolveAll(ConflictResolution.TakeRemote), () => CanBulkResolve);
        KeepSelectedConflictCommand = new RelayCommand(
            () => SelectedConflict?.KeepLocal(), () => SelectedConflict is { IsResolved: false });
        TakeSelectedConflictRemoteCommand = new RelayCommand(
            () => SelectedConflict?.TakeRemote(), () => SelectedConflict is { IsResolved: false });

        LoadWorkspace();
    }

    // ---- collections and selection ------------------------------------------------------------------

    public ObservableCollection<ContainerViewModel> Containers { get; } = [];

    public ObservableCollection<ConflictViewModel> Conflicts { get; } = [];

    public ObservableCollection<string> ActivityLog { get; } = [];

    public ContainerViewModel? SelectedContainer
    {
        get => _selectedContainer;
        set
        {
            if (SetProperty(ref _selectedContainer, value))
            {
                OnPropertyChanged(nameof(SelectedContainerItems));
                RemoveContainerCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Rows for the grid. Empty rather than null when no container is selected.</summary>
    public IReadOnlyList<ItemViewModel> SelectedContainerItems =>
        SelectedContainer?.Items ?? (IReadOnlyList<ItemViewModel>)[];

    // ---- status -------------------------------------------------------------------------------------

    /// <summary>
    /// Current status line.
    /// </summary>
    /// <remarks>
    /// Settable because the item grid reports a validation problem from the view - a quantity
    /// box holding text never reaches the model, so the view is the only place that knows.
    /// </remarks>
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Short sync label, e.g. "3 conflicts" or "Synced 14:02".</summary>
    public string SyncState
    {
        get => _syncState;
        private set => SetProperty(ref _syncState, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>
    /// Row and container counts for the status bar.
    /// </summary>
    /// <remarks>
    /// Reports on what is loaded rather than on whether a file has ever been opened: a
    /// deliberately empty document is a real document, and calling it "no document" would read
    /// as a fault.
    /// </remarks>
    public string DocumentSummary => $"{Containers.Count} container(s), {Containers.Sum(c => c.Items.Count)} item(s)";

    /// <summary>Path of the last export, reused as the default for the next one.</summary>
    public string? LastExportPath
    {
        get => _lastExportPath;
        private set => SetProperty(ref _lastExportPath, value);
    }

    // ---- commands -----------------------------------------------------------------------------------

    public RelayCommand NewCommand { get; }
    public RelayCommand OpenDialogCommand { get; }
    public RelayCommand SaveDialogCommand { get; }
    public RelayCommand ImportDialogCommand { get; }
    public RelayCommand AddContainerCommand { get; }
    public RelayCommand RemoveContainerCommand { get; }
    public RelayCommand AcceptMergeCommand { get; }
    public RelayCommand DiscardMergeCommand { get; }
    public RelayCommand TakeAllLocalCommand { get; }
    public RelayCommand TakeAllRemoteCommand { get; }
    public RelayCommand KeepSelectedConflictCommand { get; }
    public RelayCommand TakeSelectedConflictRemoteCommand { get; }

    /// <summary>
    /// Hook for the file pickers, injected so the view model stays free of WPF dialog types.
    /// </summary>
    /// <remarks>
    /// Defaults to a picker set up in the window; a test substitutes a stub and drives the same
    /// command with a path, which is why the real handlers take a <see cref="Uri"/> rather than
    /// returning one.
    /// </remarks>
    public IFilePicker? Picker { get; set; }

    /// <summary>Conflict currently highlighted in the review grid.</summary>
    public ConflictViewModel? SelectedConflict
    {
        get => _selectedConflict;
        set
        {
            if (SetProperty(ref _selectedConflict, value))
            {
                KeepSelectedConflictCommand.RaiseCanExecuteChanged();
                TakeSelectedConflictRemoteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private void OpenWithDialog()
    {
        if (Picker?.PickOpen() is { } path)
        {
            LoadFrom(path);
        }
    }

    private void SaveWithDialog()
    {
        if (Picker?.PickSave(LastExportPath) is { } path)
        {
            SaveTo(path);
        }
    }

    private void ImportWithDialog()
    {
        if (Picker?.PickImport() is { } path)
        {
            MergeFrom(path);
        }
    }

    // ---- guards -------------------------------------------------------------------------------------

    private bool CanExport => _document is not null && !IsBusy;

    private bool CanMerge => _document is not null && !IsBusy;

    private bool CanAcceptMerge =>
        _pendingMerge is not null && !Conflicts.Any(c => !c.IsResolved) && !IsBusy;

    /// <summary>
    /// True while there is at least one open conflict to decide.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="CanAcceptMerge"/>. The bulk buttons are how an operator
    /// settles the *last* open conflict, so guarding them on "nothing left to resolve" would
    /// make them unreachable - the state they are needed in is the state that disables them.
    /// </remarks>
    private bool CanBulkResolve => _pendingMerge is not null && Conflicts.Any(c => !c.IsResolved) && !IsBusy;

    /// <summary>
    /// Runs the startup path: restores the workspace, or opens an empty document.
    /// </summary>
    /// <remarks>
    /// A workspace that fails to load is not fatal. The operator is told, and starts from empty
    /// with their file still on disk - blocking the app would be a worse answer than a warning.
    /// </remarks>
    private void LoadWorkspace()
    {
        if (_workspace.TryLoad(out var error) && _workspace.Current is not null)
        {
            Adopt(_workspace.Current);
            StatusMessage = error ?? $"Restored {_workspace.Current.Containers.Count} container(s) from the last session.";
            SyncState = _workspace.Base is null ? "No sync baseline" : "Workspace restored";
            AppendActivity($"Restored workspace from {Path.GetFileName(_workspace.CurrentPath)}.");
        }
        else
        {
            AdoptEmpty();
            if (error is not null)
            {
                StatusMessage = error;
                AppendActivity(error);
            }
        }

        RaiseGuards();
    }

    // ---- document lifecycle -------------------------------------------------------------------------

    private void NewDocument()
    {
        AdoptEmpty();
        StatusMessage = "Started a new document.";
        AppendActivity("New document created.");
        RaiseGuards();
    }

    /// <summary>Replaces the working document with the contents of an export file.</summary>
    public void LoadFrom(Uri path)
    {
        var loadError = _files.TryLoad(path.LocalPath, out var document);
        if (loadError is not null || document is null)
        {
            StatusMessage = loadError ?? $"Could not open {Path.GetFileName(path.LocalPath)}.";
            AppendActivity(StatusMessage);
            return;
        }

        // Opening an export replaces the base snapshot too: the file just read *is* the last
        // state agreed with the handheld, which is exactly what a merge needs as its third input.
        // Keeping the previous base would make every row in the newly-opened file look changed.
        _workspace.SetDocuments(document, document);
        Adopt(document);
        StatusMessage = $"Opened {Path.GetFileName(path.LocalPath)}.";
        AppendActivity($"Opened {Path.GetFileName(path.LocalPath)} and set it as the sync baseline.");
        RaiseGuards();
    }

    /// <summary>Validates and writes the working document as an export file.</summary>
    public void SaveTo(Uri path)
    {
        // Rebuilt first: the grid is what the operator is looking at, so it - not the last
        // structural change - decides what gets written.
        var document = BuildDocument();
        _document = document;

        var error = _files.TrySave(path.LocalPath, document);
        if (error is not null)
        {
            StatusMessage = error;
            AppendActivity(error);
            return;
        }

        LastExportPath = path.LocalPath;
        StatusMessage = $"Exported to {Path.GetFileName(path.LocalPath)}. Load this onto the handheld.";
        AppendActivity($"Exported {Containers.Count} container(s) to {Path.GetFileName(path.LocalPath)}.");
    }

    // ---- containers ---------------------------------------------------------------------------------

    private void AddContainer()
    {
        var container = new ContainerViewModel();
        Containers.Add(container);
        SelectedContainer = container;
        AppendActivity("Container added.");
        RaiseDocumentChanged();
    }

    private void RemoveContainer()
    {
        if (SelectedContainer is not { } container)
        {
            return;
        }

        Containers.Remove(container);
        SelectedContainer = Containers.FirstOrDefault();
        AppendActivity($"Container \"{container.Id}\" removed.");
        RaiseDocumentChanged();
    }

    // ---- merge --------------------------------------------------------------------------------------

    /// <summary>
    /// Merges a handheld export into the working document, holding the result until the
    /// operator has dealt with every conflict.
    /// </summary>
    /// <remarks>
    /// The merge is run immediately but not applied. Conflicts are collected into
    /// <see cref="Conflicts"/> for review, and the merged document is only swapped in by
    /// <see cref="AcceptMerge"/>. Applying first and hoping is what turns a judgement call into
    /// data loss.
    /// </remarks>
    public void MergeFrom(Uri path)
    {
        // Merges what is on screen, not the last saved snapshot, or a sync would silently
        // discard every edit made since the last structural change.
        var local = BuildDocument();
        _document = local;

        var loadError = _files.TryLoad(path.LocalPath, out var remote);
        if (loadError is not null || remote is null)
        {
            StatusMessage = loadError ?? $"Could not read {Path.GetFileName(path.LocalPath)}.";
            AppendActivity(StatusMessage);
            return;
        }

        var result = InventoryMerger.Merge(_workspace.Base, local, remote, DateTimeOffset.Now);

        _pendingMerge = result;
        _mergeCandidate = result.Document;
        RebuildConflicts(result.Conflicts);

        SyncState = Conflicts.Count == 0
            ? "No conflicts"
            : $"{Conflicts.Count(c => !c.IsResolved)} unresolved of {Conflicts.Count}";
        SelectedConflict = null;

        StatusMessage = result.Conflicts.Count == 0
            ? $"Merged cleanly from {Path.GetFileName(path.LocalPath)}. Review, then accept."
            : $"{Conflicts.Count} field(s) need a decision before this merge can be accepted.";

        if (result.ClockSkewDetected)
        {
            // Loud, and unmissable: with a wrong clock on either device, "most recent wins"
            // stops meaning anything, and the operator needs to know the ranking they are reading.
            StatusMessage += " WARNING: a timestamp is far from this machine's clock, so recency is unreliable.";
            AppendActivity("Clock skew detected between the desktop and the handheld export.");
        }

        AppendActivity($"Merge preview from {Path.GetFileName(path.LocalPath)}: " +
                       $"{result.Conflicts.Count} conflict(s), {result.ItemOutcomes.Count} row(s) examined.");
        RaiseGuards();
    }

    /// <summary>Applies every recorded decision and promotes the result to the working document.</summary>
    public void AcceptMerge()
    {
        if (_pendingMerge is not { } pending || _mergeCandidate is null)
        {
            StatusMessage = "No merge is pending.";
            return;
        }

        if (Conflicts.Any(c => !c.IsResolved))
        {
            StatusMessage = "Resolve every conflict before accepting the merge.";
            return;
        }

        var document = _mergeCandidate;

        // Decisions are applied one at a time, each threaded forward into the next. The domain
        // model is immutable, so the document has to be reassigned as it is rewritten - and a
        // conflict that cannot be mapped aborts the whole accept rather than leaving a
        // half-applied document that looks complete in the grid.
        var failures = new List<string>();
        foreach (var conflict in Conflicts)
        {
            var outcome = ConflictResolver.Apply(document, conflict.Source, conflict.Resolution!.Value);
            if (!outcome.Succeeded)
            {
                failures.Add(outcome.Message ?? "Unknown conflict resolution failure");
                break;
            }

            document = outcome.Document!;
        }

        if (failures.Count > 0)
        {
            StatusMessage = "Merge not applied:\n" + string.Join("\n", failures);
            AppendActivity(StatusMessage);
            Log.Warning($"Merge acceptance blocked by {failures.Count} unresolvable conflict(s)");
            return;
        }

        _workspace.PromoteBase(document);
        Adopt(document);
        FinishMerge("Merged and applied. Export the file for the handheld.");
    }

    /// <summary>Throws the preview away and keeps editing the pre-merge document.</summary>
    public void DiscardMerge()
    {
        FinishMerge("Merge discarded. The document is unchanged.");
        AppendActivity("Pending merge discarded.");
    }

    private void FinishMerge(string message)
    {
        _pendingMerge = null;
        _mergeCandidate = null;
        Conflicts.Clear();
        SyncState = "Not synced";
        StatusMessage = message;

        // Deliberately not RaiseDocumentChanged: that rebuilds the document from the grid, and
        // the grid still shows the pre-merge state at the moment a merge is accepted. Doing it
        // here would overwrite the just-resolved document and quietly restore every value the
        // operator had just settled. The grid has been reloaded from the merged document by
        // Adopt, so there is nothing left to pull back in.
        _document = BuildDocument();
        _workspace.SetDocuments(_document, _workspace.Base);

        var saveError = _workspace.Save();
        if (saveError is not null)
        {
            StatusMessage = saveError;
            AppendActivity(saveError);
        }

        OnPropertyChanged(nameof(DocumentSummary));
        OnPropertyChanged(nameof(SelectedContainerItems));
        RaiseGuards();
    }

    /// <summary>Applies one decision to every open conflict at once.</summary>
    public void ResolveAll(ConflictResolution resolution)
    {
        foreach (var conflict in Conflicts.Where(c => !c.IsResolved))
        {
            if (resolution == ConflictResolution.KeepLocal)
            {
                conflict.KeepLocal();
            }
            else
            {
                conflict.TakeRemote();
            }
        }

        SyncState = Conflicts.Count == 0 ? "Not synced" : $"{Conflicts.Count} resolved";

        // Accept becomes available and the bulk buttons retire; the guards are stale otherwise.
        RaiseGuards();
    }

    private void RebuildConflicts(IReadOnlyList<ItemConflict> conflicts)
    {
        Conflicts.Clear();

        foreach (var conflict in conflicts)
        {
            var view = new ConflictViewModel(conflict);

            // Resolving a single row by hand has to re-evaluate the guards: it is what usually
            // enables Accept, and it is what retires the bulk buttons once nothing is open.
            view.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(ConflictViewModel.IsResolved))
                {
                    SyncState = $"{Conflicts.Count(c => !c.IsResolved)} unresolved of {Conflicts.Count}";
                    RaiseGuards();
                }
            };

            Conflicts.Add(view);
        }
    }

    // ---- document plumbing --------------------------------------------------------------------------

    /// <summary>Rebuilds the editable view from an immutable document.</summary>
    private void Adopt(InventoryDocument document)
    {
        _document = document;
        RebuildContainers(document);
        RaiseDocumentChanged();
    }

    private void AdoptEmpty() => Adopt(new InventoryDocument { Containers = [] });

    private void RebuildContainers(InventoryDocument document)
    {
        var selectedId = SelectedContainer?.Id;
        Containers.Clear();

        foreach (var container in document.Containers)
        {
            Containers.Add(new ContainerViewModel(container));
        }

        // Selection survives a reload when the same container is still present, so accepting a
        // merge does not throw the operator back to the top of the list.
        SelectedContainer = selectedId is null
            ? Containers.FirstOrDefault()
            : Containers.FirstOrDefault(c => string.Equals(c.Id, selectedId, StringComparison.Ordinal))
              ?? Containers.FirstOrDefault();
    }

    /// <summary>
    /// Pulls the edited view models back into the document.
    /// </summary>
    /// <remarks>
    /// The grid is the authority on what the document currently holds, so it is rebuilt at the
    /// moment it is read - export, merge, save - rather than on every keystroke. Rebuilding per
    /// keystroke would also mean writing the workspace file on every character typed.
    /// </remarks>
    private InventoryDocument BuildDocument() =>
        (_document ?? new InventoryDocument { Containers = [] }) with
        {
            ExportedAt = InventoryFormat.FormatExportedAt(DateTimeOffset.Now),
            Containers = Containers.Select(c => c.ToContainer()).ToList(),
        };

    /// <summary>
    /// Rebuilds the document from the grid and persists the workspace.
    /// </summary>
    /// <remarks>
    /// Called after every structural change: adding or removing a container or row, loading a
    /// document, accepting a merge.
    /// </remarks>
    private void RaiseDocumentChanged()
    {
        _document = BuildDocument();

        _workspace.SetDocuments(_document, _workspace.Base);
        var error = _workspace.Save();
        if (error is not null)
        {
            StatusMessage = error;
            AppendActivity(error);
        }

        OnPropertyChanged(nameof(DocumentSummary));
        OnPropertyChanged(nameof(SelectedContainerItems));
        RaiseGuards();
    }

    private void RaiseGuards()
    {
        SaveDialogCommand.RaiseCanExecuteChanged();
        ImportDialogCommand.RaiseCanExecuteChanged();
        AcceptMergeCommand.RaiseCanExecuteChanged();
        DiscardMergeCommand.RaiseCanExecuteChanged();
        TakeAllLocalCommand.RaiseCanExecuteChanged();
        TakeAllRemoteCommand.RaiseCanExecuteChanged();
        KeepSelectedConflictCommand.RaiseCanExecuteChanged();
        TakeSelectedConflictRemoteCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(DocumentSummary));
        OnPropertyChanged(nameof(SyncState));
    }

    private void AppendActivity(string message)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        ActivityLog.Add($"{stamp}  {message}");

        // Bounded so a long session cannot grow this without limit; the file log is the
        // durable record.
        while (ActivityLog.Count > 200)
        {
            ActivityLog.RemoveAt(0);
        }

        Log.Info(message);
    }
}
