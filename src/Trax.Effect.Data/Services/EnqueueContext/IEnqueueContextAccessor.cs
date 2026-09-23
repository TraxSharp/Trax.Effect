using Trax.Effect.Data.Services.DataContext;

namespace Trax.Effect.Data.Services.EnqueueContext;

/// <summary>
/// Exposes the data context that the enqueue path is currently committing on, so a
/// <c>ServiceTrain.OnQueue</c> hook can make its side-effect part of the same transaction as the
/// work queue row.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Current"/> is non-null only while <c>OnQueue</c> is running. Everywhere else it is
/// null, and a consumer must fall back to its own context.
/// </para>
/// <para>
/// Writes tracked on <see cref="Current"/> are saved and committed by the enqueue, so a hook that
/// throws — or a queue row that fails to insert — rolls the hook's work back with it. A hook that
/// writes through its own <c>DbContext</c> does NOT get that guarantee: EF can only share a
/// transaction between contexts that share a connection, and a separately-pooled context does not.
/// </para>
/// <para>
/// The hook must not call <c>SaveChanges</c> or commit on <see cref="Current"/>; the enqueue owns
/// the lifetime.
/// </para>
/// </remarks>
public interface IEnqueueContextAccessor
{
    /// <summary>
    /// The context the enqueue is committing on, or null when no enqueue is in progress.
    /// </summary>
    IDataContext? Current { get; }

    /// <summary>
    /// Makes <paramref name="context"/> the ambient enqueue context until the returned scope is
    /// disposed. Called by the framework's enqueue path; consumers read <see cref="Current"/>.
    /// </summary>
    IDisposable Enter(IDataContext context);
}
