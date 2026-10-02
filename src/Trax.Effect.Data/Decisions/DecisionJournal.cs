using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Trax.Core.Decisions;
using Trax.Core.Exceptions;
using Trax.Effect.Data.Services.IDataContextFactory;
using Trax.Effect.Exceptions;
using Trax.Effect.Models.Metadata;
using Trax.Effect.Models.RecordedDecision;
using Trax.Effect.Services.Decisions;

namespace Trax.Effect.Data.Decisions;

/// <summary>
/// Writes each decision a run makes to <c>trax.decision</c> as it is made, and serves a requeued
/// run the answers its original gave. Registered by <c>AddDecisionRecording</c> as both the
/// <see cref="IDecisionObserver"/> and the <see cref="IDecisionReplay"/> Trax.Core looks for.
/// </summary>
/// <remarks>
/// <para>A decision is written before the train acts on it, through a data context of its own,
/// so a run that dies mid-way (an OOM kill, a timeout, a deploy) leaves every decision it acted on
/// behind for its requeue to replay, and the write never flushes or inherits what the run's own
/// junctions have tracked. The journal is <see cref="Required"/>: a decision that cannot be written
/// fails its step before any track is taken, classified transient, because a database that is down
/// now may not be on a retry. A value the store refuses is classified permanent.</para>
///
/// <para>The journal itself holds nothing between runs. Each run's row and replayed answers live
/// on the run's own async flow, set by <c>ServiceTrain.Run</c> when it starts and dropped when its
/// junctions finish, so two runs that share an external id each write under their own row and
/// replay their own answers, and nothing is left behind however a run ends. A decision made
/// outside a service train's run, or in a run that was never persisted, is logged only.</para>
/// </remarks>
public sealed class DecisionJournal(
    IDataContextProviderFactory contextFactory,
    ILogger<DecisionJournal>? logger = null
) : IDecisionObserver, IDecisionReplay, IDecisionRunRecorder
{
    // Optional, so a host that registers no logging still records decisions instead of failing
    // every run that makes one.
    private readonly ILogger _logger = logger ?? NullLogger<DecisionJournal>.Instance;

    private static readonly IReadOnlyDictionary<(string, int), Answer> NothingToReplay =
        new Dictionary<(string, int), Answer>();

    /// <summary>
    /// True: a requeue replays what was written, so a decision that could not be written must not
    /// be acted on.
    /// </summary>
    public bool Required => true;

    /// <inheritdoc />
    public async Task Decided(DecisionMade decision, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Train {Train} (run {RunId}) decided {Question}: {Answer} by {Decider}{Replayed}{Shadows}{Refused}",
            decision.Train,
            decision.RunId,
            decision.Question.Key,
            DecisionJson.Write(decision.Answer),
            decision.Decider?.Name ?? "replay",
            decision.Replayed ? " (replayed)" : "",
            decision.Shadows.Count == 0
                ? ""
                : $"; shadows agreeing: {decision.Shadows.Count(s => s.Agrees)}/{decision.Shadows.Count}",
            decision.ReplayRefused is null ? "" : $"; not replayed: {decision.ReplayRefused}"
        );

        if (Bound(decision.RunId) is not { MetadataId: { } metadataId } run)
            return;

        var record = new RecordedDecision
        {
            MetadataId = metadataId,
            QuestionKey = decision.Question.Key,
            Occurrence = decision.Occurrence,
            Kind = DecisionJson.Kind(decision.Question),
            Question = DecisionJson.Write(decision.Question),
            Answer = DecisionJson.Write(decision.Answer, decision.ReplayRefused),
            Model = decision.Answer.Model,
            Decider = decision.Decider?.FullName,
            Replayed = decision.Replayed,
            Shadows = DecisionJson.Write(decision.Shadows),
            DecidedAt = DateTime.UtcNow,
        };

        await Write(
            decision.Train,
            decision.RunId,
            async context =>
            {
                context.RecordedDecisions.Add(record);
                await context.SaveChanges(cancellationToken);
            },
            cancellationToken
        );

        run.Latest[record.QuestionKey] = record.Id;
    }

    /// <inheritdoc />
    public async Task Routed(TrackRouted routing, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Train {Train} (run {RunId}) took track {Track} on {On}{Fallback}",
            routing.Train,
            routing.RunId,
            routing.Track,
            QuestionKey.For(routing.On),
            routing.FallbackReason is null ? "" : $" because {routing.FallbackReason}"
        );

        // The routing is written onto the row of the latest decision it routes on.
        if (
            Bound(routing.RunId) is not { MetadataId: not null } run
            || !run.Latest.TryGetValue(QuestionKey.For(routing.On), out var recordId)
        )
            return;

        await Write(
            routing.Train,
            routing.RunId,
            async context =>
            {
                var record = await context.RecordedDecisions.FirstOrDefaultAsync(
                    d => d.Id == recordId,
                    cancellationToken
                );

                // Gone only if its run was deleted meanwhile, which leaves nothing to route.
                if (record is null)
                    return;

                record.Track = routing.Track;
                record.FallbackReason = routing.FallbackReason;
                await context.SaveChanges(cancellationToken);
            },
            cancellationToken
        );
    }

    /// <inheritdoc />
    public Answer? Replay(string train, string runId, string key, int occurrence) =>
        Bound(runId)?.Replay.GetValueOrDefault((key, occurrence));

    /// <summary>
    /// Binds the run to its row and loads the answers of the run it replays. A run that names one
    /// that does not exist, or whose answers cannot be read, fails here, classified permanent; one
    /// whose answers cannot be loaded because the database failed fails classified transient.
    /// </summary>
    async Task<DecisionRun> IDecisionRunRecorder.Begin(
        Metadata metadata,
        CancellationToken cancellationToken
    )
    {
        // A run that was never persisted has no row to write against; its decisions are logged.
        long? metadataId = metadata.Id > 0 ? metadata.Id : null;

        var replay = metadata.ReplayDecisionsOf is { } source
            ? await LoadReplay(metadata, source, cancellationToken)
            : NothingToReplay;

        return new DecisionRun(metadata.ExternalId, metadataId, replay);
    }

    private async Task<IReadOnlyDictionary<(string, int), Answer>> LoadReplay(
        Metadata metadata,
        long source,
        CancellationToken cancellationToken
    )
    {
        bool exists;
        List<(string QuestionKey, int Occurrence, string Answer)> recorded;

        try
        {
            using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

            exists = await context
                .Metadatas.AsNoTracking()
                .AnyAsync(m => m.Id == source, cancellationToken);

            recorded = exists ? (
                    await context
                        .RecordedDecisions.AsNoTracking()
                        .Where(d => d.MetadataId == source)
                        .Select(d => new
                        {
                            d.QuestionKey,
                            d.Occurrence,
                            d.Answer,
                        })
                        .ToListAsync(cancellationToken)
                ).Select(d => (d.QuestionKey, d.Occurrence, d.Answer)).ToList() : [];
        }
        catch (Exception e)
            when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                e,
                "Could not load the decisions of run {Source} for run {RunId} to replay.",
                source,
                metadata.ExternalId
            );

            DecisionRun.Classified(e, metadata.Name, metadata.ExternalId, FailureClass.Transient);
            throw;
        }

        if (!exists)
            throw DecisionRun.Unreplayable(metadata, $"no run {source} exists");

        var answers = new Dictionary<(string, int), Answer>();

        foreach (var (key, occurrence, answer) in recorded)
        {
            try
            {
                answers[(key, occurrence)] = DecisionJson.ReadAnswer(answer);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException)
            {
                throw DecisionRun.Unreplayable(
                    metadata,
                    $"its recorded answer to '{key}' cannot be read: {e.Message}"
                );
            }
        }

        return answers;
    }

    /// <summary>
    /// Writes through a short-lived context of its own, so the write neither flushes what the
    /// run's junctions have tracked nor leaves a failed entity tracked in the run's context.
    /// </summary>
    private async Task Write(
        string train,
        string runId,
        Func<Services.DataContext.IDataContext, Task> write,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await write(context);
        }
        catch (StoreRefusedContentException e)
        {
            // The same decision is refused the same way on every retry.
            DecisionRun.Classified(e, train, runId, FailureClass.Permanent);
            throw;
        }
    }

    /// <summary>The run on this flow, when it is the run Trax.Core reported.</summary>
    private static DecisionRun? Bound(string runId) =>
        DecisionRun.Current is { } run && run.RunId == runId ? run : null;
}
