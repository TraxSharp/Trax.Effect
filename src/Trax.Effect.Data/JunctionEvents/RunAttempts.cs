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
/// The runs are picked as Trax.Scheduler picks them when it counts a manifest's failures: a run
/// that completed or was cancelled ends the streak, and a dispatch attempt the scheduler failed
/// and requeued (<c>FailureException</c> <see cref="RequeuedDispatch"/>) is not a run of the job
/// and is skipped. A dead letter's resolution does not restart the count here; the scheduler's
/// failure window and dead letters decide retries, this only numbers them for a timeline.
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
        var before = context
            .Metadatas.AsNoTracking()
            .Where(m => m.ManifestId == manifestId && m.Id < id);

        var lastResolved = await before
            .Where(m =>
                m.TrainState == TrainState.Completed || m.TrainState == TrainState.Cancelled
            )
            .OrderByDescending(m => m.Id)
            .Select(m => (long?)m.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var failed = await before
            .Where(m =>
                m.TrainState == TrainState.Failed
                && m.FailureException != RequeuedDispatch
                && (lastResolved == null || m.Id > lastResolved)
            )
            .CountAsync(cancellationToken);

        return failed + 1;
    }
}
