using Scantron.Core.Models;
using Scantron.Core.Sync;

namespace Scantron.Desktop.Services.Sync;

/// <summary>
/// Decides when to tell the handheld about a local edit, and coalesces the burst.
/// </summary>
/// <remarks>
/// <para>
/// The desktop already has the answer to "what changed": it is whatever differs from the last
/// agreed base. So this type does not track edits at all - it only decides <em>when</em> to ask.
/// Debouncing is the whole of its job.
/// </para>
/// <para>
/// It debounces because the alternative is a change frame per keystroke. Typing a category name
/// would be a dozen frames, each carrying the same row, and the handheld would write the row a
/// dozen times. Waiting a fraction of a second after the last edit collapses that into one frame
/// with the final value, at a latency nobody can perceive.
/// </para>
/// <para>
/// Publishing is not required to succeed. If nothing is connected the ops are simply not sent, and
/// because they are still ahead of the base they go out in the catch-up when the handheld returns.
/// </para>
/// </remarks>
public sealed class ChangeEmitter : IDisposable
{
    /// <summary>
    /// How long the emitter waits for the typing to stop.
    /// </summary>
    /// <remarks>
    /// Long enough to swallow a real burst of keystrokes, short enough that a save-and-glance
    /// round trip does not feel laggy. Below this the frame count climbs sharply; above it the
    /// operator starts to notice.
    /// </remarks>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(400);

    private readonly Timer _timer;
    private readonly Func<(InventoryDocument? Current, InventoryDocument? Base, IReadOnlyList<Scantron.Core.Merge.ItemConflict> Conflicts)> _snapshot;
    private readonly Func<IReadOnlyList<SyncOp>, Task> _publish;
    private readonly object _gate = new();

    private bool _disposed;
    private bool _pending;

    public ChangeEmitter(
        Func<(InventoryDocument? Current, InventoryDocument? Base, IReadOnlyList<Scantron.Core.Merge.ItemConflict> Conflicts)> snapshot,
        Func<IReadOnlyList<SyncOp>, Task> publish)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(publish);

        _snapshot = snapshot;
        _publish = publish;

        // One-shot: re-armed on every edit, which is what makes it a debounce rather than a poll.
        _timer = new Timer(_ => _ = PublishPendingAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Number of times a batch has actually been published. For status and for tests.</summary>
    public int PublishCount { get; private set; }

    /// <summary>
    /// Requests a publish after the quiet period.
    /// </summary>
    /// <remarks>
    /// Cheap and safe to call on every property change, which is exactly how it is used. It does no
    /// work until the timer fires.
    /// </remarks>
    public void PublishSoon()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = true;
        }

        _timer.Change(QuietPeriod, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Publishes immediately, for the cases where waiting would be wrong.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="PublishSoon"/> this does not need a preceding request: it means "work out
    /// the changes now", which is what a Sync button wants. It still publishes nothing when the
    /// document already matches the base, so an impatient click cannot produce an empty frame.
    /// </remarks>
    public Task FlushAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            _pending = true;
        }

        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        return PublishPendingAsync();
    }

    private async Task PublishPendingAsync()
    {
        lock (_gate)
        {
            if (_disposed || !_pending)
            {
                return;
            }

            _pending = false;
        }

        IReadOnlyList<SyncOp> ops;
        try
        {
            var (current, @base, conflicts) = _snapshot();

            // A row with an open conflict is frozen: sending it would either re-report the conflict
            // forever or settle it in favour of whoever synced last.
            ops = ChangeCursors.Diff(@base, current ?? SyncDocuments.Empty, ChangeCursors.ConflictRows(conflicts));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Log.Error("Could not work out what to send to the handheld", ex);
            return;
        }

        if (ops.Count == 0)
        {
            return;
        }

        PublishCount++;

        try
        {
            await _publish(ops).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Swallowed rather than surfaced: the ops are still ahead of the base, so nothing is
            // lost, and a transient network failure is not something an operator can act on.
            Log.Info($"A live-sync publish did not complete: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _timer.Dispose();
    }
}
