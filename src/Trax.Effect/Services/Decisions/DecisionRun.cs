using System.Collections.Concurrent;
using Trax.Core.Decisions;
using Trax.Core.Exceptions;
using Trax.Effect.Models.Metadata;

namespace Trax.Effect.Services.Decisions;

/// <summary>
/// Prepares the recording of one run's decisions before its junctions run. Implemented by
/// <c>Trax.Effect.Data</c>'s decision journal, which <c>AddDecisionRecording</c> registers.
/// </summary>
internal interface IDecisionRunRecorder
{
    /// <summary>
    /// Binds the run to its row and loads the answers it replays. Called once per run, after the
    /// row is saved and the start hooks have run, and before the first junction. Whatever it
    /// throws fails the run.
    /// </summary>
    Task<DecisionRun> Begin(Metadata metadata, CancellationToken cancellationToken);
}

/// <summary>
/// What one run needs to record and replay its decisions: the row they are written against and the
/// answers it replays.
/// </summary>
/// <remarks>
/// It lives on the run's own async flow (<see cref="Current"/>), set by <c>ServiceTrain.Run</c>, not
/// in the journal, which is a singleton. Two runs that share an external id, as a retried
/// dispatch's rows can, each see their own, and nothing outlives the run whichever way it ends.
/// </remarks>
internal sealed class DecisionRun(
    string runId,
    long? metadataId,
    IReadOnlyDictionary<(string Key, int Occurrence), RecordedAnswer> replay
)
{
    private static readonly AsyncLocal<DecisionRun?> CurrentRun = new();

    /// <summary>The run on this async flow, or null outside a service train's run.</summary>
    public static DecisionRun? Current
    {
        get => CurrentRun.Value;
        set => CurrentRun.Value = value;
    }

    /// <summary>The run's external id, which Trax.Core reports its decisions under.</summary>
    public string RunId { get; } = runId;

    /// <summary>The run's row, or null when it was never persisted and its decisions are only logged.</summary>
    public long? MetadataId { get; } = metadataId;

    /// <summary>
    /// The answers of the run this one repeats, each with the fingerprint it was recorded under,
    /// keyed as Trax.Core asks for them.
    /// </summary>
    public IReadOnlyDictionary<(string Key, int Occurrence), RecordedAnswer> Replay { get; } =
        replay;

    /// <summary>The id of the row written for each question's latest asking, for its routing.</summary>
    public ConcurrentDictionary<string, long> Latest { get; } = new();

    /// <summary>
    /// The failure for a run that names a run to replay that cannot be replayed. It is classified
    /// permanent: running it again on the same host hits the same wall, and asking afresh would
    /// break the promise the requeue made.
    /// </summary>
    public static TrainException Unreplayable(Metadata metadata, string why)
    {
        var message =
            $"Run {metadata.ExternalId} of train '{metadata.Name}' was queued to replay the "
            + $"decisions of run {metadata.ReplayDecisionsOf}, but {why}. It is failed rather "
            + "than asked afresh, because a fresh answer could take a different track from the "
            + "run it repeats.";

        return Classified(
            new TrainException(message),
            metadata.Name,
            metadata.ExternalId,
            FailureClass.Permanent
        );
    }

    /// <summary>
    /// Attaches a failure class to an exception that carries none yet, so the run records it
    /// whether it fails before any junction or in a decision step.
    /// </summary>
    public static TException Classified<TException>(
        TException exception,
        string trainName,
        string runId,
        FailureClass failureClass
    )
        where TException : Exception
    {
        if (exception.Data["TrainExceptionData"] is TrainExceptionData data)
            data.FailureClass ??= failureClass;
        else
            exception.Data["TrainExceptionData"] = new TrainExceptionData
            {
                TrainName = trainName,
                TrainExternalId = runId,
                Type = exception.GetType().Name,
                Junction = "DecisionReplay",
                Message = exception.Message,
                StackTrace = exception.StackTrace,
                FailureClass = failureClass,
            };

        return exception;
    }
}
