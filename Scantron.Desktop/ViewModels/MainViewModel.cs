using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Scantron.Core.Models;
using Scantron.Core.Merge;
using Scantron.Core.Sync;
using Scantron.Desktop.Mvvm;
using Scantron.Desktop.Services;
using Scantron.Desktop.Services.Sync;
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
            private readonly HandheldClient _handheld;
            private readonly DiscoveryResponder _discovery;
            private readonly PairedPeerStore _paired;
            private readonly SyncCoordinator _sync;
            private readonly ChangeEmitter _emitter;
            private bool _disposed;
            private bool _served;
            private long _syncSeq;

            /// <summary>
            /// Set while a peer's batch is being adopted.
            /// </summary>
            /// <remarks>
            /// Adopting the merged result rebuilds the grid, and a rebuild looks exactly like an operator
            /// edit to the change emitter. Publishing during an apply would send the peer its own change
            /// straight back - the classic sync loop, and precisely what the base diff exists to prevent.
            /// </remarks>
            private bool _suppressPublish;

            /// <summary>True while the pane is showing a live-sync batch rather than a file merge.</summary>
            private bool _liveMerge;

        private InventoryDocument? _document;
        private InventoryDocument? _mergeCandidate;
        private MergeResult? _pendingMerge;
        private ContainerViewModel? _selectedContainer;
        private ConflictViewModel? _selectedConflict;
        private string _statusMessage = "Ready.";
        private string _syncState = "Not synced";
                private string _handheldAddress = string.Empty;
                private string? _lastExportPath;
        private bool _isBusy;
    private bool _hasWorkingDocument;

        public MainViewModel(WorkspaceStore? workspace = null, InboxStore? inbox = null, TransferHub? hub = null, HandheldClient? handheld = null, DiscoveryResponder? discovery = null, PairedPeerStore? paired = null, SyncCoordinator? coordinator = null)
        {
            _workspace = workspace ?? new WorkspaceStore();
            _inbox = inbox ?? new InboxStore();
            _hub = hub ?? new TransferHub();
                    _handheld = handheld ?? new HandheldClient();
                    _discovery = discovery ?? new DiscoveryResponder();
            _paired = paired ?? new PairedPeerStore();
            _sync = coordinator ?? new SyncCoordinator(_paired);

            // The live-sync coordinator borrows the view model's documents rather than holding
            // copies, so what it publishes is what the operator is actually looking at - the same
            // reason the merge path rebuilds from the grid instead of using the last saved file.
            _sync.Document = LiveDocument;
            _sync.Base = () => _workspace.Base;
            _sync.Applied = OnSyncApplied;
            _sync.NextSeq = () => Interlocked.Increment(ref _syncSeq);
            _sync.StateChanged += (_, _) => OnSyncStateChanged();

            _emitter = new ChangeEmitter(
                () => (LiveDocument(), _workspace.Base, Conflicts.Select(c => c.Source).ToList()),
                ops => _sync.PublishAsync(ops, CancellationToken.None));

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
        CommitMergeCommand = new RelayCommand(
            () =>
            {
                if (_liveMerge)
                {
                    ApplyLiveResolutions();
                }
                else
                {
                    AcceptMerge();
                }
            },
            () => CanAcceptMerge);
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
                                // Async because it waits on a socket. The previous implementation was synchronous
                                // and had nothing to wait for, which is exactly why it could not actually send.
                                PushToHandheldCommand = new AsyncRelayCommand(PushToHandheld, () => CanPushToHandheld);
                                ReceiveFromHandheldCommand = new RelayCommand(() => ShowHandheldTransfer(), () => CanReceiveFromHandheld);

                                StartSyncingCommand = new RelayCommand(StartSyncing, () => CanSync);
                                StopSyncingCommand = new RelayCommand(StopSyncing, () => _hub.State.Listening);
                                UnpairCommand = new RelayCommand(Unpair, () => _paired.Peer.IsPaired);

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
        /// This is the most important line on the toolbar. Discovery can fill the address in, but
        /// only when the AP passes client-to-client broadcast, so this line has to stand on its own:
        /// if it is wrong or vague the transfer simply cannot happen and there is nothing on screen
        /// to explain why.
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
        /// Address of the handheld to push to, as typed by the operator.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The reverse of <see cref="HubStatus"/>, and needed for the same reason. There is no
        /// discovery for the device-initiated direction - the scanner cannot be relied on to hold a
        /// broadcast responder while docked - so the operator reads the address off the CK65 and types
        /// it here, exactly as they type the desktop's address into the scanner for the other
        /// direction.
        /// </para>
        /// <para>
        /// Kept as typed rather than normalised on the way in, because the operator needs to see what
        /// they typed when a transfer fails. The value is sanitised at the point of use, by
        /// <see cref="HandheldClient"/>, which is the same place and the same rule the device applies
        /// to a desktop address - so a pasted URL works in both directions.
        /// </para>
        /// </remarks>
        public string HandheldAddress
        {
            get => _handheldAddress;
            set
            {
                if (SetProperty(ref _handheldAddress, value))
                {
                    // The push guard does not read this, but the button's tooltip and the status line
                    // both depend on whether an address is set, and a stale enabled button is the
                    // exact failure being fixed here.
                    RaiseGuards();
                }
            }
        }

        /// <summary>
            /// True when a document has actually reached the handheld this session.
        /// </summary>
        /// <remarks>
            /// Both directions feed this: a pull the hub served, and a push this desktop initiated
            /// and that came back acknowledged. Only a delivery that was actually confirmed sets it -
            /// the previous implementation set it unconditionally, which is how a button that sent
            /// nothing still reported a successful transfer.
        /// </remarks>
        public bool HasServedToHandheld
            {
                get => _served;
                private set => SetProperty(ref _served, value);
            }

            // ---- live sync ----------------------------------------------------------------------------------

            /// <summary>
            /// True when the document on screen came from live sync rather than a file.
            /// </summary>
            /// <remarks>
            /// The two merge paths differ only in how a conflict is settled. A file merge holds the
            /// whole result for one Accept click, because the operator asked for a deliberate act. A
            /// live batch cannot work that way - the ordinary case has no conflicts at all and has
            /// already been applied - so its conflicts are settled in place instead, and this flag is
            /// what tells the buttons which of the two they are looking at. The pane itself is
            /// transitional and expected to be removed, so nothing here should grow beyond a switch.
            /// </remarks>
            public bool IsLiveMerge => _liveMerge;

            /// <summary>Count of live-sync conflicts not yet decided. Drives the badge.</summary>
            public int OpenConflictCount => Conflicts.Count(c => !c.IsResolved);

            /// <summary>
            /// Human-readable live-sync status, e.g. "In sync 14:02" or "Offline - retrying".
            /// </summary>
            /// <remarks>
            /// Derived from the coordinator's connection state rather than stored, so the status line
            /// can never disagree with the connection the buttons are acting on.
            /// </remarks>
            public string SyncStatus => _sync.State switch
            {
                SyncConnectionState.InSync => $"In sync {DateTime.Now:HH:mm} - {_sync.PeerName}",
                SyncConnectionState.Syncing => $"Syncing with {_sync.PeerName}...",
                SyncConnectionState.AwaitingPair => "Waiting for the pairing code.",
                SyncConnectionState.Greeting => "A handheld is connecting...",
                SyncConnectionState.Offline => $"Paired with {_sync.PeerName} - offline, retrying.",
                _ => "Not paired.",
            };

            /// <summary>True while a paired handheld is connected and exchanging changes.</summary>
            public bool IsSyncing => _sync.State is SyncConnectionState.Syncing or SyncConnectionState.InSync;

            /// <summary>True when a handheld is paired, whether or not it is connected right now.</summary>
            public bool IsPaired => _paired.Peer.IsPaired;

            /// <summary>
            /// The six digits the operator reads off this screen and types into the handheld.
            /// </summary>
            /// <remarks>
            /// Shown whenever a handheld is already paired, so re-pairing a replacement unit does not
            /// require a restart. It is deliberately not persisted - a code that outlives its pairing
            /// is a standing key to the stock count.
            /// </remarks>
            public string PairingCode => _paired.Code;

            /// <summary>Label for the conflict badge, empty when there is nothing to show.</summary>
            public string ConflictBadge => OpenConflictCount == 0 ? "" : $"{OpenConflictCount}";

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
    /// The pane's committing action. Accepts a file merge, or applies a live batch's resolutions.
    /// </summary>
    /// <remarks>
    /// One command with two meanings because the operator is looking at one pane and one button. The
    /// two paths really are different - a file merge holds the whole result, a live batch has already
    /// been applied and only its decided fields remain - but that distinction belongs here, not in a
    /// second button whose purpose has to be explained.
    /// </remarks>
    public RelayCommand CommitMergeCommand { get; }
        public RelayCommand StartSharingCommand { get; }
        public RelayCommand StopSharingCommand { get; }
                public AsyncRelayCommand PushToHandheldCommand { get; }
        public RelayCommand ReceiveFromHandheldCommand { get; }

        /// <summary>Starts live sync: opens the hub and begins accepting a paired handheld.</summary>
        public RelayCommand StartSyncingCommand { get; }

        public RelayCommand StopSyncingCommand { get; }

        /// <summary>Forgets the paired handheld, so a different unit can pair.</summary>
        public RelayCommand UnpairCommand { get; }

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

    /// <summary>
    /// True when the pane's commit action can run.
    /// </summary>
    /// <remarks>
    /// The two merge paths have different preconditions, and that is the whole difference between
    /// them: a file merge is commit-able only while a preview is pending, whereas a live batch has
    /// already been applied and is commit-able once its open conflicts have decisions (or there are
    /// none at all).
    /// </remarks>
    private bool CanAcceptMerge => _liveMerge
        ? !IsBusy && Conflicts.Count > 0 && !Conflicts.Any(c => !c.IsResolved)
        : _pendingMerge is not null && !Conflicts.Any(c => !c.IsResolved) && !IsBusy;

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

        /// <summary>
                /// Everything a <c>/pull</c> serves has to survive the export gate first.
                /// </summary>
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

            // Entering a manual merge makes the manual mode current, because only one of the two
            // paths can be the one the pane is showing. Leaving the flag set from an earlier live
            // batch is what made a clean manual merge uncommittable: the live guard wants conflicts
            // to settle, and a clean merge has none, so the button stayed disabled with no way back.
            _liveMerge = false;
            OnPropertyChanged(nameof(IsLiveMerge));

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

        // Published before the pane is cleared, because the decisions are read from it. The base was
        // advanced first, so the emitter sees nothing outstanding and keeps the publish to exactly
        // these decisions - the peer would otherwise stay frozen on the field for ever.
        PublishResolutions();

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

    /// <summary>
    /// Sends the operator's decisions on the current conflicts to the handheld.
    /// </summary>
    /// <remarks>
    /// A decision is not an edit, it is the settlement of one, so it travels as a
    /// <see cref="ResolveOp"/> rather than as the row. That distinction is what stops the peer
    /// re-reporting the same conflict: a row would look like a fresh simultaneous change, while a
    /// resolution is understood as "this field is now this value, stop arguing about it".
    /// </remarks>
    private void PublishResolutions()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ops = new List<SyncOp>();

        foreach (var conflict in Conflicts.Where(c => c.Resolution is not null))
        {
            var source = conflict.Source;
            var value = conflict.Resolution == ConflictResolution.KeepLocal
                ? source.LocalValue
                : source.RemoteValue;

            ops.Add(new ResolveOp(source.ContainerId, source.ItemUuid, source.Field, value, now));
        }

        if (ops.Count > 0)
        {
            _ = _sync.PublishAsync(ops, CancellationToken.None);
        }
    }
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

        // A bulk decision on a live-sync conflict has to reach the handheld too, or the field stays
        // frozen there for ever.
        PublishResolutions();
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
                    OnPropertyChanged(nameof(OpenConflictCount));
                    OnPropertyChanged(nameof(ConflictBadge));

                    // Rows are now unblocked (or blocked), so what is outstanding has to be
                    // re-evaluated rather than waiting for the next unrelated edit.
                    _emitter.PublishSoon();
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
        _hub.Start(LiveDocument, ReceiveFromNetwork, _sync);

        if (_hub.State.Error is { } error)
        {
            StatusMessage = $"Could not start sharing: {error}";
            AppendActivity(StatusMessage);
            return;
        }

        // Started only after the hub is listening, so the address the reply advertises is one that
        // actually works. Discovery is a convenience: a bind failure is logged and sharing goes on.
        if (_discovery.Start() is { } discoveryError)
        {
            Log.Warning("Discovery is unavailable, so the address must be typed into the handheld: " + discoveryError);
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
        private void StopSharingAsync()
        {
            _discovery.Stop();
            _ = _hub.StopAsync();
        }

    /// <summary>
        /// Sends the live document to a handheld that is listening for it.
    /// </summary>
    /// <remarks>
        /// <para>
        /// This is the button that used to do nothing. It validated the document, built a
        /// <see cref="HubResponse"/> that described a transfer that had already happened, and returned
        /// it to a caller that discarded it - so no bytes ever left the machine, while the status line
        /// claimed the handheld had pulled the document. The response was a fiction.
        /// </para>
        /// <para>
        /// It now dials the handheld, and only reports what actually came back. The two directions
        /// are both real: this one is desktop-initiated and goes out through
        /// <see cref="HandheldClient"/>, while <c>/pull</c> remains the path the device takes when the
        /// operator works from the scanner. Neither replaces the other, because neither device can be
        /// relied on to be the one holding the button.
        /// </para>
        /// <para>
        /// Runs through the same export gate as a file, because it is the same promise: whatever
        /// leaves this machine is a document the handheld is entitled to import without losing what it
        /// already holds. The gate runs before the socket is opened, so an invalid document never
        /// costs the operator a trip across the warehouse.
        /// </para>
        /// </remarks>
        private async Task PushToHandheld()
        {
            var document = BuildDocument();
            _document = document;

            if (InventoryFileService.Validate(document) is { } invalid)
            {
                var message = $"Not sent - this document would be rejected by the handheld:{Environment.NewLine}{invalid}";
                Log.Warning($"Refused a push to the handheld: {invalid}");
                StatusMessage = message;
                AppendActivity(message);
                return;
            }

            // The exact bytes an export to file would write, so the two routes stay interchangeable.
            var payload = HubEndpoints.ToExportJson(document);

            IsBusy = true;
            StatusMessage = $"Sending to {HandheldAddress}...";

            try
            {
                var result = await _handheld.SendAsync(HandheldAddress, payload).ConfigureAwait(true);

                StatusMessage = result.Message;
                AppendActivity(result.Message);

                if (result.Success)
                {
                    // Only a transfer that was actually delivered counts as served. The flag is what
                    // the status bar reports, so setting it unconditionally would reintroduce the
                    // original lie in a second place.
                    HasServedToHandheld = true;
                }
            }
            finally
            {
                IsBusy = false;
            }
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

    // ---- live sync ------------------------------------------------------------------------------------

    private bool CanSync => !IsBusy && !_hub.State.Listening;

    /// <summary>
    /// Opens the hub and offers live sync, showing the pairing code.
    /// </summary>
    /// <remarks>
    /// Live sync and the manual transfer routes share one hub on one port, so "sync" is the same
    /// switch as "share" plus the pairing code. That is deliberate: an operator should not have to
    /// reason about which of two listening modes is on.
    /// </remarks>
    private void StartSyncing()
    {
        StartSharing();

        if (_hub.State.Error is { } error)
        {
            StatusMessage = $"Could not start live sync: {error}";
            AppendActivity(StatusMessage);
            return;
        }

        StatusMessage = _paired.Peer.IsPaired
            ? $"Live sync open. {_paired.Peer.Name} reconnects on its own; no code needed."
            : $"Live sync open. Type the pairing code {_paired.Code} into the handheld.";

        AppendActivity(StatusMessage);
        OnPropertyChanged(nameof(PairingCode));
        RaiseGuards();
    }

    private void StopSyncing()
    {
        StopSharingAsync();
        AppendActivity("Live sync closed.");
    }

    private void Unpair()
    {
        var name = _paired.Peer.Name;
        _sync.Unpair();

        StatusMessage = $"Unpaired from {name}. A new handheld can pair with the code shown.";
        AppendActivity($"Unpaired from {name}.");
        RaiseGuards();
    }

    private void OnSyncStateChanged()
    {
        OnPropertyChanged(nameof(SyncStatus));
        OnPropertyChanged(nameof(IsSyncing));
        OnPropertyChanged(nameof(IsPaired));
        OnPropertyChanged(nameof(PairingCode));
        RaiseGuards();
    }

    /// <summary>
    /// Applies a batch of live-sync changes to the grid and persists the new base.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is where live sync stops being "review and accept" and becomes actual sync. A batch with
    /// no conflicts is adopted immediately - the operator is not asked to approve a change that
    /// cannot lose anyone's work - while a real conflict lands in the review pane and raises the
    /// badge. That pane is transitional and is expected to disappear; the conflict state itself is
    /// modelled in the documents, not in the pane, so removing it later is a UI-only change.
    /// </para>
    /// <para>
    /// The base is advanced for everything both devices agreed on and deliberately left alone for a
    /// conflicted field. Advancing past a conflict is what would silently pick a winner.
    /// </para>
    /// </remarks>
    private void OnSyncApplied(SyncApplyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // The grid is the authority, so what it holds now is folded in before the peer's view is
        // applied - exactly as the file-merge path rebuilds before merging.
        var local = BuildDocument();
        _document = local;
        _hasWorkingDocument = true;

        _workspace.SetDocuments(result.Document, result.Base);

        _suppressPublish = true;
        try
        {
            Adopt(result.Document);
        }
        finally
        {
            _suppressPublish = false;
        }

        _document = result.Document;

        // A manual merge preview was computed against the state *before* this batch, so keeping it
        // would let the operator later accept a document that has already been superseded - silently
        // reverting the very change that just arrived. It is dropped rather than kept, and the
        // operator is told below, because a review pane that empties itself without a word is its
        // own small data loss: they cannot tell whether they missed something or nothing happened.
        var supersededMerge = _pendingMerge is not null;

        _pendingMerge = null;
        _mergeCandidate = null;
        _liveMerge = true;
        Conflicts.Clear();
        RebuildConflicts(result.Conflicts);

        SyncState = result.Conflicts.Count == 0
            ? "In sync"
            : $"{result.Conflicts.Count} field(s) need a decision";

        if (result.Conflicts.Count > 0)
        {
            StatusMessage = $"{result.Conflicts.Count} field(s) changed on both devices. Choose a value to settle each one.";
            AppendActivity(StatusMessage);
        }

        if (supersededMerge)
        {
            const string superseded =
                "The merge you had open was set aside: the handheld's change was applied on top of it, " +
                "so reviewing it would have reverted that change. Import the file again if you still need it.";

            StatusMessage = result.Conflicts.Count > 0
                ? StatusMessage + " " + superseded
                : superseded;

            AppendActivity("A pending merge preview was superseded by a live-sync batch and discarded.");
        }

        if (result.ClockSkewDetected)
        {
            AppendActivity("Clock skew detected between the desktop and the handheld; recency is unreliable.");
        }

        // Persisted only after the documents are in place: a base written before the document it
        // belongs to would be a snapshot of a state the operator never saw.
        var saveError = _workspace.Save();
        if (saveError is not null)
        {
            StatusMessage = saveError;
            AppendActivity(saveError);
        }

        OnPropertyChanged(nameof(DocumentSummary));
        OnPropertyChanged(nameof(SelectedContainerItems));
        OnPropertyChanged(nameof(SyncStatus));
        OnPropertyChanged(nameof(IsLiveMerge));
        RaiseGuards();

        // Only a live batch needs an explicit action: a clean one has already been applied, and a
        // conflicted one is settled with "Apply resolutions" rather than being held for Accept.
        if (result.Conflicts.Count > 0)
        {
            StatusMessage = $"{result.Conflicts.Count} field(s) changed on both devices. " +
                            "Choose a value for each, then apply the resolutions.";
        }

        // Deliberately not RaiseDocumentChanged: that would rebuild the document from the grid and
        // push the freshly applied values straight back out as local edits, which is the echo the
        // base diff exists to prevent.
    }

    /// <summary>
    /// Settles the live-sync conflicts the operator has decided, in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterpart of <see cref="AcceptMerge"/> for the live path. A file merge can hold the
    /// whole result for one Accept click because the operator asked for a deliberate act; a live
    /// batch cannot, because the ordinary case has no conflicts at all and has already been applied.
    /// So there is nothing to promote here - only the decided fields have to be written and the new
    /// agreed base advanced past them, which is what unfreezes the rows.
    /// </para>
    /// <para>
    /// Only the decided fields move. A conflict the operator has not answered keeps its base entry
    /// exactly where it was, so it stays reported rather than being quietly settled by default.
    /// </para>
    /// </remarks>
    public void ApplyLiveResolutions()
    {
        if (!_liveMerge)
        {
            // A file merge is committed by AcceptMerge; sending it here would skip that step.
            StatusMessage = "Use Accept to apply a merge from a file.";
            return;
        }

        if (Conflicts.Count == 0)
        {
            StatusMessage = "There is nothing to resolve.";
            return;
        }

        if (Conflicts.Any(c => !c.IsResolved))
        {
            StatusMessage = "Resolve every conflict before applying.";
            return;
        }

        var document = _document ?? BuildDocument();
        var nextBase = _workspace.Base ?? document;

        foreach (var conflict in Conflicts)
        {
            var source = conflict.Source;
            var value = conflict.Resolution == ConflictResolution.KeepLocal
                ? source.LocalValue
                : source.RemoteValue;

            document = ApplyFieldToDocument(document, source, value);
            nextBase = ApplyFieldToDocument(nextBase, source, value);
        }

        _workspace.SetDocuments(document, nextBase);

        // The decisions go to the handheld before the pane is cleared, because the peer needs to
        // learn the field is settled - otherwise it would stay frozen there for ever.
        PublishResolutions();

        _suppressPublish = true;
        try
        {
            Adopt(document);
        }
        finally
        {
            _suppressPublish = false;
        }

        _document = document;
        _liveMerge = false;
        Conflicts.Clear();

        var saveError = _workspace.Save();
        if (saveError is not null)
        {
            StatusMessage = saveError;
            AppendActivity(saveError);
        }
        else
        {
            StatusMessage = "Resolutions applied and synced to the handheld.";
            AppendActivity(StatusMessage);
        }

        OnPropertyChanged(nameof(IsLiveMerge));
        OnPropertyChanged(nameof(DocumentSummary));
        OnPropertyChanged(nameof(SelectedContainerItems));
        RaiseGuards();
    }

    /// <summary>
    /// Applies one decided field to a document, by uuid for a row and by tag for a container.
    /// </summary>
    /// <remarks>
    /// Deliberately mirrors what the peer does with a <see cref="ResolveOp"/>. Both sides reaching
    /// the same value by the same rule is what makes the two devices agree without a round trip.
    /// </remarks>
    private static InventoryDocument ApplyFieldToDocument(
        InventoryDocument document,
        ItemConflict conflict,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(conflict.ItemUuid))
        {
            return SyncDocuments.SetContainerField(document, conflict.ContainerId, conflict.Field, value);
        }

        var container = document.Containers.FirstOrDefault(
            c => string.Equals(c.Id.Trim(), conflict.ContainerId.Trim(), StringComparison.Ordinal));

        var row = container?.Items.FirstOrDefault(
            i => string.Equals(i.Uuid.Trim(), conflict.ItemUuid.Trim(), StringComparison.Ordinal));

        if (row is null)
        {
            return document;
        }

        return SyncDocuments.SetItemField(
            document,
            conflict.ContainerId,
            Scantron.Core.Identity.ItemKeyResolver.Resolve(conflict.ContainerId, row),
            conflict.Field,
            value);
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

                // The push client owns no socket of its own between sends, so this is about releasing the
                // handler rather than tearing anything down that could fail.
                _handheld.Dispose();
                _discovery.Dispose();
                _emitter.Dispose();
                await _sync.DisposeAsync().ConfigureAwait(true);
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

        /// <summary>Publishes the live document to a listening handheld. Internal for the same reason.</summary>
                internal Task SendToHandheld() => PushToHandheld();

    /// <summary>
    /// Starts listening to a container and its rows, so an edit reaches the handheld.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The document is only rebuilt from the grid when it is read, so nothing else in the app needs
    /// to know the moment a field changes. Live sync is the exception: without this, typing a
    /// quantity would not be noticed until something else happened to trigger a read, and the
    /// handheld would sit on a stale value until the next structural change.
    /// </para>
    /// <para>
    /// This is not a per-change publish - it only asks the emitter to look once the typing stops,
    /// which is why hooking every property is cheap enough to do unconditionally.
    /// </para>
    /// </remarks>
    private void Watch(ContainerViewModel container)
    {
        container.PropertyChanged += OnEditorChanged;
        container.Items.CollectionChanged += OnRowsChanged;

        foreach (var item in container.Items)
        {
            item.PropertyChanged += OnEditorChanged;
        }
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Rows are hooked as they arrive and released as they leave, so a removed row cannot keep
        // the emitter alive or report edits it no longer has.
        foreach (var item in e.OldItems?.Cast<ItemViewModel>() ?? [])
        {
            item.PropertyChanged -= OnEditorChanged;
        }

        foreach (var item in e.NewItems?.Cast<ItemViewModel>() ?? [])
        {
            item.PropertyChanged += OnEditorChanged;
        }

        _emitter.PublishSoon();
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        // An apply rebuilds the grid, and a rebuild looks like an edit. Publishing then would send
        // the peer its own change straight back.
        if (!_suppressPublish)
        {
            _emitter.PublishSoon();
        }
    }

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
            var view = new ContainerViewModel(container);
            Watch(view);
            Containers.Add(view);
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

        // Asking the emitter is cheap and safe on every structural change; it only does work once
        // the operator has stopped typing. A structural change is also the moment the operator is
        // most likely to look away, so it must not be the one that never gets sent.
        if (!_suppressPublish)
        {
            _emitter.PublishSoon();
        }
    }

    private void RaiseGuards()
    {
        SaveDialogCommand.RaiseCanExecuteChanged();
        ImportDialogCommand.RaiseCanExecuteChanged();
        AcceptMergeCommand.RaiseCanExecuteChanged();
        CommitMergeCommand.RaiseCanExecuteChanged();
        DiscardMergeCommand.RaiseCanExecuteChanged();
        TakeAllLocalCommand.RaiseCanExecuteChanged();
        TakeAllRemoteCommand.RaiseCanExecuteChanged();
        KeepSelectedConflictCommand.RaiseCanExecuteChanged();
        TakeSelectedConflictRemoteCommand.RaiseCanExecuteChanged();
                StartSharingCommand.RaiseCanExecuteChanged();
                StopSharingCommand.RaiseCanExecuteChanged();
                PushToHandheldCommand.RaiseCanExecuteChanged();
                ReceiveFromHandheldCommand.RaiseCanExecuteChanged();
        StartSyncingCommand.RaiseCanExecuteChanged();
        StopSyncingCommand.RaiseCanExecuteChanged();
        UnpairCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(DocumentSummary));
                OnPropertyChanged(nameof(SyncState));
        OnPropertyChanged(nameof(OpenConflictCount));
        OnPropertyChanged(nameof(ConflictBadge));
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
