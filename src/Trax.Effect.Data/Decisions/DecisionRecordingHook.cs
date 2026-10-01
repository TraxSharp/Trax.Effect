using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Trax.Core.Decisions;
using Trax.Effect.Data.Services.DataContext;
using Trax.Effect.Models.Metadata;
using Trax.Effect.Services.TrainLifecycleHook;

namespace Trax.Effect.Data.Decisions;

/// <summary>
/// Loads the decisions a requeued run replays when it starts, and writes a run's decisions against
/// its metadata when it finishes, however it finishes.
/// </summary>
public sealed class DecisionRecordingHook(
    IDataContext dataContext,
    DecisionJournal journal,
    ILogger<DecisionRecordingHook>? logger = null
) : ITrainLifecycleHook
{
    private readonly ILogger _logger = logger ?? NullLogger<DecisionRecordingHook>.Instance;

    /// <inheritdoc />
    public async Task OnStarted(Metadata metadata, CancellationToken ct)
    {
        if (metadata.ReplayDecisionsOf is not { } source)
            return;

        try
        {
            var recorded = await dataContext
                .RecordedDecisions.AsNoTracking()
                .Where(d => d.MetadataId == source)
                .Select(d => new
                {
                    d.QuestionKey,
                    d.Occurrence,
                    d.Answer,
                })
                .ToListAsync(ct);

            journal.SeedReplay(
                metadata.ExternalId,
                recorded.ToDictionary(
                    d => (d.QuestionKey, d.Occurrence),
                    d => DecisionJson.ReadAnswer(d.Answer)
                )
            );
        }
        catch (Exception e)
        {
            journal.ReplayUnavailable(metadata.ExternalId, e.Message);
            _logger.LogError(
                e,
                "Could not load the decisions of run {Source} for run {RunId} to replay.",
                source,
                metadata.ExternalId
            );
        }
    }

    /// <inheritdoc />
    public Task OnCompleted(Metadata metadata, CancellationToken ct) => Record(metadata, ct);

    /// <inheritdoc />
    public Task OnFailed(Metadata metadata, Exception exception, CancellationToken ct) =>
        Record(metadata, ct);

    /// <inheritdoc />
    public Task OnCancelled(Metadata metadata, CancellationToken ct) => Record(metadata, ct);

    private async Task Record(Metadata metadata, CancellationToken ct)
    {
        var decisions = journal.Take(metadata.ExternalId);

        // A run that was never persisted has no row to record against; its decisions were logged.
        if (decisions.Count == 0 || metadata.Id <= 0)
            return;

        foreach (var decision in decisions)
            decision.MetadataId = metadata.Id;

        try
        {
            await dataContext.RecordedDecisions.AddRangeAsync(decisions, ct);
            await dataContext.SaveChanges(ct);
        }
        catch (Exception e)
        {
            // Recording is a record of what happened, not part of it: the run's outcome stands.
            _logger.LogError(
                e,
                "Could not record {Count} decisions of run {RunId}; they remain in the log.",
                decisions.Count,
                metadata.ExternalId
            );
        }
    }
}
