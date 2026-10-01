using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Trax.Core.Decisions;
using Trax.Effect.Models.RecordedDecision;

namespace Trax.Effect.Data.Decisions;

/// <summary>
/// Collects each run's decisions while it runs, and serves a requeued run the answers its original
/// gave. Registered by <c>AddDecisionRecording</c> as both the <see cref="IDecisionObserver"/> and
/// the <see cref="IDecisionReplay"/> Trax.Core looks for.
/// </summary>
/// <remarks>
/// Runs are told apart by their external id. A run's decisions are held here until the run
/// finishes, when <see cref="DecisionRecordingHook"/> writes them against the run's metadata and
/// forgets them; the hook is registered with this journal and cannot be switched off, so nothing
/// is held past its run. Every decision is also logged as it is made, so a host that reads its
/// logs sees them before the run ends.
/// </remarks>
public sealed class DecisionJournal(ILogger<DecisionJournal>? logger = null)
    : IDecisionObserver,
        IDecisionReplay
{
    // Optional, so a host that registers no logging still records decisions instead of failing
    // every run that makes one.
    private readonly ILogger _logger = logger ?? NullLogger<DecisionJournal>.Instance;

    private readonly ConcurrentDictionary<string, RunDecisions> _runs = new();

    private readonly ConcurrentDictionary<string, ReplaySet> _replays = new();

    /// <inheritdoc />
    public void Decided(DecisionMade decision)
    {
        var run = _runs.GetOrAdd(decision.RunId, _ => new RunDecisions());

        lock (run)
        {
            var occurrence = run.Records.Count(r => r.QuestionKey == decision.Question.Key);

            run.Records.Add(
                new RecordedDecision
                {
                    QuestionKey = decision.Question.Key,
                    Occurrence = occurrence,
                    Kind = DecisionJson.Kind(decision.Question),
                    Question = DecisionJson.Write(decision.Question),
                    Answer = DecisionJson.Write(decision.Answer),
                    Model = decision.Answer.Model,
                    Decider = decision.Decider?.FullName,
                    Replayed = decision.Replayed,
                    Shadows = DecisionJson.Write(decision.Shadows),
                    DecidedAt = DateTime.UtcNow,
                }
            );
        }

        _logger.LogInformation(
            "Train {Train} (run {RunId}) decided {Question}: {Answer} by {Decider}{Replayed}{Shadows}",
            decision.Train,
            decision.RunId,
            decision.Question.Key,
            DecisionJson.Write(decision.Answer),
            decision.Decider?.Name ?? "replay",
            decision.Replayed ? " (replayed)" : "",
            decision.Shadows.Count == 0
                ? ""
                : $"; shadows agreeing: {decision.Shadows.Count(s => s.Agrees)}/{decision.Shadows.Count}"
        );
    }

    /// <inheritdoc />
    public void Routed(TrackRouted routing)
    {
        if (_runs.TryGetValue(routing.RunId, out var run))
            lock (run)
                if (run.Records.LastOrDefault(r => r.QuestionKey == routing.On.Name) is { } record)
                {
                    record.Track = routing.Track;
                    record.FallbackReason = routing.FallbackReason;
                }

        _logger.LogInformation(
            "Train {Train} (run {RunId}) took track {Track} on {On}{Fallback}",
            routing.Train,
            routing.RunId,
            routing.Track,
            routing.On.Name,
            routing.FallbackReason is null ? "" : $" because {routing.FallbackReason}"
        );
    }

    /// <inheritdoc />
    public Answer? Replay(string train, string runId, string key, int occurrence)
    {
        if (!_replays.TryGetValue(runId, out var replay))
            return null;

        // A replay that could not be loaded fails the run rather than letting it ask afresh: the
        // run was requeued to repeat the original, and a fresh answer could take another track.
        if (replay.Failure is { } failure)
            throw new InvalidOperationException(
                $"Run {runId} replays the decisions of an earlier run, which could not be read: "
                    + failure
            );

        return replay.Answers.GetValueOrDefault((key, occurrence));
    }

    /// <summary>Takes the decisions held for a run, which then stops holding them.</summary>
    internal IReadOnlyList<RecordedDecision> Take(string runId)
    {
        _replays.TryRemove(runId, out _);

        return _runs.TryRemove(runId, out var run) ? run.Records : [];
    }

    /// <summary>Makes the answers a run should replay available for the run's duration.</summary>
    internal void SeedReplay(string runId, IReadOnlyDictionary<(string, int), Answer> answers) =>
        _replays[runId] = new ReplaySet(answers, null);

    /// <summary>Notes that a run's replay could not be loaded, so its first decision fails.</summary>
    internal void ReplayUnavailable(string runId, string reason) =>
        _replays[runId] = new ReplaySet(new Dictionary<(string, int), Answer>(), reason);

    private sealed class RunDecisions
    {
        public List<RecordedDecision> Records { get; } = [];
    }

    private sealed record ReplaySet(
        IReadOnlyDictionary<(string Key, int Occurrence), Answer> Answers,
        string? Failure
    );
}
