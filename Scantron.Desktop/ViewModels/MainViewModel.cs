using System.Collections.ObjectModel;
using System.Globalization;
using Scantron.Core.Models;
using Scantron.Core.Merge;
using Scantron.Desktop.Mvvm;
using Scantron.Desktop.Services;
using Scantron.Desktop.Services.Transfer;

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
/// <para>
/// The one thing owned here that does talk to the outside world is the transfer hub, and it is
/// injected rather than constructed so a test can supply one on a spare port. Shutdown closes it
/// before anything else is torn down - see <see cref="Dispose"/>.
/// </para>
/// </remarks>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly InventoryFileService _files = new();
    private readonly WorkspaceStore _workspace;
        private readonly InboxStore _inbox;
        private readonly TransferHub _hub;
        private bool _disposed;
        private bool _served;

        private InventoryDocument? _document;
        private InventoryDocument? _mergeCandidate;
        private MergeResult? _pendingMerge;
        private ContainerViewModel? _selectedContainer;
        private ConflictViewModel? _selectedConflict;
        private string _statusMessage = "Ready.";
        private string _syncState = "Not synced";
        private string? _lastExportPath;
        private bool _isBusy;
    private bool _hasWorkingDocument;

        public MainViewModel(WorkspaceStore? workspace = null, InboxStore? inbox = null, TransferHub? hub = null)
        {
            _workspace = workspace ?? new WorkspaceStore();
            _inbox = inbox ?? new InboxStore();
            _hub = hub ?? new TransferHub();

            // The hub reports from its serve loop. Reflected onto the view model rather than bound
            // directly, because the toolbar has to be able to change the buttons the state implies -
            // and only the view model knows what else is enabled.
            _hub.StateChanged += (_, state) => OnHubStateChanged(state);

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
                StartSharingCommand = new RelayCommand(StartSharing);
                StopSharingCommand = new RelayCommand(StopSharingAsync, () => _hub.State.Listening);
                PushToHandheldCommand = new RelayCommand(() => PushToHandheld(), () => CanPushToHandheld);
                ReceiveFromHandheldCommand = new RelayCommand(() => ShowHandheldTransfer(), () => CanReceiveFromHandheld);

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

        // ---- LAN transfer ---------------------------------------------------------------------------------

        /// <summary>
        /// What the hub is doing, including the address to type into the handheld.
        /// </summary>
        /// <remarks>
        /// This is the most important line on the toolbar. There is no discovery - the operator
        /// reads the IP off the PC and enters it on the CK65 - so if this is wrong or vague, the
        /// transfer simply cannot happen and there is nothing on screen to explain why.
        /// </remarks>
        public string HubStatus => _hub.State.Status;

        /// <summary>
        /// True when the hub is listening and reachable.
        /// </summary>
        /// <remarks>
        /// Drives the toolbar buttons as well as the status text. Receiving is only offered while
        /// something can actually be received, so the button cannot sit there implying a pull will
        /// work when nothing is listening.
        /// </remarks>
        public bool IsSharing => _hub.State.Listening;

    /// <summary>
    /// The document a pull would serve, or null when there is nothing to serve.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null and empty are different answers here, and the difference is destructive. The
    /// handheld imports by clearing and replacing, so handing it an empty
    /// <c>containers</c> array instructs it to delete everything it holds. Returning null makes
    /// <c>/pull</c> refuse outright instead.
    /// </para>
    /// <para>
    /// That distinction cannot be read off <c>_document</c>, because a cold start with no
    /// workspace still adopts an empty one - it has to, or the grid would have nothing to bind
    /// to. So this is a separate flag rather than a null check: true once the operator has opened,
    /// created or received something, and false only for the app that has just been launched
    /// onto an empty desk.
    /// </para>
    /// </remarks>
    public InventoryDocument? LiveDocument() => _hasWorkingDocument ? BuildDocument() : null;

        /// <summary>
        /// True when a pull from the desktop has already been answered this session.
        /// </summary>
        /// <remarks>
        /// Pulling is destructive on the handheld - it clears and replaces its whole database - so
        /// the app never pushes the document anywhere on its own. The file is put on the desk, the
        /// operator carries it over, and the pull button exists to stage what arrived. Flagging that
        /// it has happened is the only part of the exchange the desktop can observe.
        /// </remarks>
        public bool HasServedToHandheld
        {
            get => _served;
            private set => SetProperty(ref _served, value);
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
        public RelayCommand StartSharingCommand { get; }
        public RelayCommand StopSharingCommand { get; }
        public RelayCommand PushToHandheldCommand { get; }
        public RelayCommand ReceiveFromHandheldCommand { get; }

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
        /// Sharing can always be started, even with nothing loaded.
        /// </summary>
        /// <remarks>
        /// Deliberately unguarded. An operator who has just opened the app and wants to start
        /// sharing should not have to invent a document first, and refusing to listen would leave no
        /// way to diagnose why. <c>/pull</c> reports "nothing loaded" for that case; it does not
        /// need a second opinion from the toolbar.
        /// </remarks>
        private bool CanShare => !IsBusy && !_hub.State.Listening;

        private bool CanStopShare => !IsBusy && _hub.State.Listening;

        /// <summary>Everything a <c>/pull</c> serves has to survive the export gate first.</summary>
        /// <remarks>
        /// Keyed off <c>LiveDocument</c> rather than <c>_document</c>, which is never null: a cold
        /// start adopts an empty one so the grid has something to bind to. Offering to send that
        /// would be offering the empty document whose delivery the whole 503 rule exists to prevent.
        /// </remarks>
        private bool CanPushToHandheld => _hasWorkingDocument && !IsBusy;

        private bool CanReceiveFromHandheld => _hasWorkingDocument && !IsBusy && _hub.State.Listening;

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

            // A deliberate "New" is an operator saying an empty document is what they want. It is
            // still a real document, and refusing to serve it over the wire would be the confusing
            // answer rather than the safe one.
            _hasWorkingDocument = true;
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
                _hasWorkingDocument = true;
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

            MergeDocument(remote, Path.GetFileName(path.LocalPath));
        }

        /// <summary>
        /// The one merge path. Both the file import and an inbound LAN push land here.
        /// </summary>
        /// <remarks>
        /// <para>
        /// There is exactly one of these. A second implementation - however identical it looked when
        /// written - would be free to drift the first time a merge rule changed, and the drift would
        /// show up as transfers behaving differently from USB imports, which is precisely the thing
        /// the feature promises will not happen. So the rules live here once and both callers supply
        /// only a document and the name to show the operator.
        /// </para>
        /// <para>
        /// <paramref name="local"/> is passed in rather than rebuilt here because
        /// <see cref="MergeFrom"/> has to build and persist it before it knows whether the file can
        /// even be read. Building twice would stamp a second <c>exportedAt</c> on the same merge and
        /// make the desktop look a moment newer than it is.
        /// </para>
        /// </remarks>
        private void MergeDocument(InventoryDocument remote, string sourceLabel, InventoryDocument? local = null)
        {
            ArgumentNullException.ThrowIfNull(sourceLabel);

            local ??= BuildDocument();
            _document = local;

            // Reaching here means something arrived from outside, so the operator is working on a
            // real document from now on - including the case where the first thing that ever
            // happened was a push from a scanner.
            _hasWorkingDocument = true;

            var result = InventoryMerger.Merge(_workspace.Base, local, remote, DateTimeOffset.Now);

            _pendingMerge = result;
            _mergeCandidate = result.Document;
            RebuildConflicts(result.Conflicts);

            SyncState = Conflicts.Count == 0
                ? "No conflicts"
                : $"{Conflicts.Count(c => !c.IsResolved)} unresolved of {Conflicts.Count}";
            SelectedConflict = null;

            StatusMessage = result.Conflicts.Count == 0
                ? $"Merged cleanly from {sourceLabel}. Review, then accept."
                : $"{result.Conflicts.Count} field(s) need a decision before this merge can be accepted.";

            if (result.ClockSkewDetected)
            {
                // Loud, and unmissable: with a wrong clock on either device, "most recent wins"
                // stops meaning anything, and the operator needs to know the ranking they are reading.
                StatusMessage += " WARNING: a timestamp is far from this machine's clock, so recency is unreliable.";
                AppendActivity("Clock skew detected between the desktop and the handheld export.");
            }

            AppendActivity($"Merge preview from {sourceLabel}: " +
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

    // ---- LAN transfer ----------------------------------------------------------------------------------

    /// <summary>
    /// Opens the hub and leaves it open until the operator closes it.
    /// </summary>
    /// <remarks>
    /// Synchronous on purpose. Binding is a local operation, and <see cref="TransferHub.Start"/>
    /// never blocks on the network - the serve loop runs on its own task. Making this
    /// <c>async</c> would only buy a dispatcher hop before a button click, and would leave the
    /// view model deciding <c>await</c>s on a path that must be straightforward to reason about.
    /// </remarks>
    private void StartSharing()
    {
        _hub.Start(LiveDocument, ReceiveFromNetwork);

        if (_hub.State.Error is { } error)
        {
            StatusMessage = $"Could not start sharing: {error}";
            AppendActivity(StatusMessage);
            return;
        }

        AppendActivity("Sharing with the handheld. Type the address above into the CK65's Transfer screen.");
    }

    /// <summary>
    /// Closes the hub.
    /// </summary>
    /// <remarks>
        /// Fire-and-forget by design. Stopping means refusing the next connection, and the listener
        /// closes before anything is awaited, so the button has already done its job by the time this
        /// returns. The one case that can take real time - a push mid-merge - is waited for by
        /// <see cref="DisposeAsync"/>, which runs at shutdown while the view model is still whole.
        /// </remarks>
        private void StopSharingAsync() => _ = _hub.StopAsync();

    /// <summary>
    /// Offers the live document to a handheld that asks for it.
    /// </summary>
    /// <remarks>
    /// Runs through the same gate as a file export, because it is the same promise: whatever
    /// leaves this machine over the network is a document the handheld is entitled to import
    /// without losing what it already holds. <c>/pull</c> refuses outright when there is nothing
    /// loaded - see <c>HubEndpoints</c> - so this only ever sees a real document.
    /// </remarks>
    private HubResponse PushToHandheld()
    {
        var document = BuildDocument();
        _document = document;

        if (InventoryFileService.Validate(document) is { } invalid)
        {
            var message = $"Not sent - this document would be rejected by the handheld:{Environment.NewLine}{invalid}";
            Log.Warning($"Refused a /pull: {invalid}");
            StatusMessage = message;
            AppendActivity(message);
            return new HubResponse(400, HubResponse.Text, message);
        }

        HasServedToHandheld = true;
        StatusMessage = $"Handheld pulled {Containers.Count} container(s). Confirm the import on the device.";
        AppendActivity($"Handheld pulled {Containers.Count} container(s) over the network.");
        return new HubResponse(200, HubResponse.Json, HubEndpoints.Acknowledgement(document));
    }

    /// <summary>
        /// Explains that the pull half of a transfer is driven from the handheld.
    /// </summary>
        /// <remarks>
        /// There is genuinely nothing for this to do, and that is worth saying out loud rather than
        /// leaving a button that looks broken. Pulling is destructive on the device - it clears and
        /// replaces its whole database - so the desktop never initiates it, and an operator watching
        /// the screen after sending something to a scanner needs to be told where the merge will
        /// appear.
        /// </remarks>
        private void ShowHandheldTransfer()
        {
            StatusMessage = "Nothing to do here - on the handheld choose Get from desktop. " +
                            "Anything it sends appears in Sync review.";
            AppendActivity("Pull direction explained: transfers are started from the handheld.");
        }

        /// <summary>
        /// Stages an inbound push and sends it into the one merge path.
        /// </summary>
    /// <remarks>
    /// Runs on the UI thread - <c>TransferHub</c> marshals here deliberately - because it
    /// rebuilds the document from the grid and then replaces it with the merge result.
    /// </remarks>
    private void ReceiveFromHandheld(InventoryDocument remote)
    {
        if (_inbox.Stage(remote, out var path) is { } stageError)
        {
            // Not fatal - the merge still runs - but the operator has to know the desktop is
            // currently the only copy of what the handheld sent.
            StatusMessage = stageError;
            AppendActivity(stageError);
        }

        MergeDocument(remote, $"the handheld over the network ({Path.GetFileName(path)})");
    }

    /// <summary>Routes a validated inbound document, from the hub's serve loop.</summary>
    private HubResponse ReceiveFromNetwork(InventoryDocument remote)
    {
        ReceiveFromHandheld(remote);

        var conflicts = Conflicts.Count(c => !c.IsResolved);

            // The handheld has a ten second timeout and nothing to do but wait, so this answers the
            // moment the merge is staged - not when the operator accepts it, which could be an hour
            // later. The push is on disk and in the review pane by the time this is written.
            return new HubResponse(
                200,
                HubResponse.Json,
                conflicts == 0
                    ? HubEndpoints.Acknowledgement(remote)
                    : $$"""{"ok":true,"conflicts":{{conflicts}},"message":"{{PlainText(Conflicts.Count + " field(s) need a decision in Sync review on the desktop.")}}"}""");
        }

        /// <summary>
        /// Flattens a sentence so it is safe to place inside a JSON string literal.
        /// </summary>
        /// <remarks>
        /// Hand-escaped rather than serialized, because this is the one place a sentence built from
        /// user data crosses into JSON. The handheld must never be handed a body that fails to parse
        /// just because an operator named a container something with a quote in it.
        /// </remarks>
        private static string PlainText(string message) =>
            message.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private void OnHubStateChanged(HubState state)
    {
        OnPropertyChanged(nameof(HubStatus));
        OnPropertyChanged(nameof(IsSharing));
        RaiseGuards();
    }

    // ---- document plumbing --------------------------------------------------------------------------

    /// <summary>
    /// Closes the transfer hub.
    /// </summary>
    /// <remarks>
    /// Called from the window's <c>Closed</c> event, because the view model's own disposal is
    /// reached from the dispatcher, and the serve loop is already marshalling onto that same
    /// dispatcher. Waiting here rather than from a synchronous Dispose is what makes shutdown
    /// deterministic: an inbound push mid-merge completes against a live view model, or is
    /// abandoned cleanly - it is never left holding one that is half torn down.
    /// </remarks>
    public void Dispose() => _ = DisposeAsync();

    internal async Task DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            await _hub.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Nothing useful can be shown to an operator during shutdown, so this is the log's
            // job rather than the status line's.
            Log.Error("The transfer hub did not close cleanly", ex);
        }
    }

        /// <summary>
        /// Routes a validated inbound document from the hub's serve loop, on the UI thread.
        /// </summary>
        /// <remarks>
        /// Internal so the end-to-end test can drive the exact path a real push takes, rather than a
        /// stand-in that could diverge from it. That is the only reason this is not private, and it
        /// is why the test asserts against <see cref="ActivityLog"/> rather than against this
        /// method's name.
        /// </remarks>
        internal HubResponse MergeFromNetwork(InventoryDocument remote)
        {
            ArgumentNullException.ThrowIfNull(remote);
            return ReceiveFromNetwork(remote);
        }

        /// <summary>Publishes the live document to a handheld that pulls it.</summary>
        /// <remarks>Internal for the same reason as <see cref="MergeFromNetwork"/>.</remarks>
        internal HubResponse SendToHandheld() => PushToHandheld();

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
                StartSharingCommand.RaiseCanExecuteChanged();
                StopSharingCommand.RaiseCanExecuteChanged();
                PushToHandheldCommand.RaiseCanExecuteChanged();
                ReceiveFromHandheldCommand.RaiseCanExecuteChanged();
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
