using Microsoft.EntityFrameworkCore;
using Trax.Effect.Data.Services.IDataContextFactory;
using Trax.Effect.Enums;
using Trax.Effect.Models.Metadata;
using Trax.Effect.Services.JunctionEvents;

namespace Trax.Effect.Data.JunctionEvents;

/// <summary>
/// Works out which attempt of its manifest a run is, from the manifest's earlier runs, through a
/// short-lived data context of its own. Registered by <c>AddJunctionEvents</c>.
/// </summary>
/// <remarks>
/// <para>The runs are picked as Trax.Scheduler picks them when it counts a manifest's failures: a
/// run that completed or was cancelled ends the streak, and a dispatch attempt the scheduler failed
/// and requeued (<c>FailureException</c> <see cref="RequeuedDispatch"/>) is not a run of the job
/// and is skipped, as is a run still pending or in progress. A dead letter's resolution does not
/// restart the count here; the scheduler's failure window and dead letters decide retries, this
/// only numbers them for a timeline.</para>
///
/// <para>It is two queries through <c>ix_metadata_manifest_id_id</c>: the id of the manifest's
/// latest completed or cancelled run before this one, then a count of its failed runs between
/// that one and this one. Neither reads a row's content, and neither sorts the manifest's
/// history.</para>
/// </remarks>
internal sealed class RunAttempts(IDataContextProviderFactory contexts) : IRunAttempts
{
    /// <summary>
    /// The failure exception Trax.Scheduler records on a dispatch attempt it requeued
    /// (<c>DispatchFailure.Requeued</c>, internal there). Keep the two equal.
    /// </summary>
    internal const string RequeuedDispatch = "DispatchRequeued";

    /// <inheritdoc />
    public async Task<int?> AttemptOf(Metadata metadata, CancellationToken cancellationToken)
    {
        if (metadata.ManifestId is not { } manifestId)
            return null;

        using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var id = metadata.Id;
        var runs = context.Metadatas.AsNoTracking().Where(m => m.ManifestId == manifestId);

        // Where the streak starts: the latest run before this one that completed or was cancelled.
        var since =
            await runs.Where(m =>
                    m.Id < id
                    && (
                        m.TrainState == TrainState.Completed || m.TrainState == TrainState.Cancelled
                    )
                )
                .MaxAsync(m => (long?)m.Id, cancellationToken)
            ?? 0;

        var failed = await runs.CountAsync(
            m =>
                m.Id > since
                && m.Id < id
                && m.TrainState == TrainState.Failed
                && (m.FailureException == null || m.FailureException != RequeuedDispatch),
            cancellationToken
        );

        return failed + 1;
    }
}
