using Trax.Effect.Broadcaster.SignalR.Models;
using Trax.Effect.Services.TrainEventBroadcaster;

namespace Trax.Effect.Broadcaster.SignalR.Services;

internal static class DefaultTraxJunctionClientEventProjection
{
    /// <summary>
    /// The client payload for a junction event. Only the step's own fields are copied: the message's
    /// output and failure fields, which a junction event leaves empty anyway, never are, and neither
    /// is the decider's name. A question's answer and confidence are copied only when
    /// <paramref name="answers"/> is set, and never when the step withheld them.
    /// </summary>
    public static TraxJunctionClientEvent Project(TrainLifecycleEventMessage message, bool answers)
    {
        var step = message.Junction!;
        var withheld = !answers || step.AnswerWithheld;

        // A junction on a decision's track names the track, so it is shown only where answers are.
        var nameWithheld = step.NameWithheld || (!answers && step.TrackPosition is not null);

        return new(
            MetadataId: message.MetadataId,
            ExternalId: message.ExternalId,
            TrainName: message.TrainName,
            EventType: message.EventType,
            Timestamp: message.Timestamp,
            Position: step.Position,
            Kind: step.Kind.ToString(),
            Name: nameWithheld ? JunctionEventPayload.WithheldName : step.Name,
            State: step.State.ToString(),
            StartedAt: step.StartedAt,
            EndedAt: step.EndedAt,
            DurationMs: step.DurationMs,
            FailureClass: step.FailureClass?.ToString(),
            FailureException: step.FailureException,
            QuestionKey: step.QuestionKey,
            Answer: withheld ? null : step.Answer,
            Confidence: withheld ? null : step.Confidence,
            Replayed: step.Replayed,
            AnswerWithheld: step.AnswerWithheld,
            NameWithheld: nameWithheld,
            TrackPosition: step.TrackPosition
        );
    }
}
