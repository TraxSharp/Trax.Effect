namespace Trax.Effect.Data.Services.WorkQueuePromotion;

/// <summary>
/// Promotes deferred work queue entries — the second phase of a two-phase enqueue.
/// </summary>
/// <remarks>
/// An entry created with <c>CreateWorkQueue.DeferPromotion</c> is committed unconfirmed and is not
/// dispatchable. The enqueue promotes it once its <c>OnQueue</c> hook has returned. If the process
/// dies in between, the entry is left unconfirmed rather than the hook's side-effect being left
/// with nothing to consume it, and <see cref="PromoteStaleAsync"/> recovers it.
/// </remarks>
public interface IWorkQueuePromotion
{
    /// <summary>
    /// Marks one entry dispatchable. Returns false when the entry does not exist or was already
    /// promoted, so callers can distinguish a recovery from a no-op.
    /// </summary>
    Task<bool> PromoteAsync(long workQueueId, CancellationToken cancellationToken);

    /// <summary>
    /// Promotes every entry left unconfirmed for longer than <paramref name="olderThan"/> and
    /// returns how many were promoted.
    /// </summary>
    /// <remarks>
    /// Promotion — rather than cancellation — is the recovery, because the deferred run re-executes
    /// the whole <c>Junctions()</c> chain: a hook's side-effect that did land is re-applied by the
    /// chain, and one that did not is performed for the first time. That assumes the hook is
    /// idempotent, which its own contract already requires.
    /// </remarks>
    Task<int> PromoteStaleAsync(TimeSpan olderThan, CancellationToken cancellationToken);
}
