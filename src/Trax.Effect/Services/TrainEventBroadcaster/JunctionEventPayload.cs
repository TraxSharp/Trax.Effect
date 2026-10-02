using System.Text.Json.Serialization;
using Trax.Core.Exceptions;
using Trax.Effect.Enums;

namespace Trax.Effect.Services.TrainEventBroadcaster;

/// <summary>
/// The junction-level part of a <see cref="TrainLifecycleEventMessage"/> whose
/// <see cref="TrainLifecycleEventMessage.EventType"/> is one of the junction event types
/// (<see cref="TrainLifecycleEventMessage.IsJunctionEvent"/>): one step of a run, a junction that
/// started or ended, a question a routing step asked, or the track it took.
/// </summary>
/// <remarks>
/// <para>It carries names, times, states and how a failure is classified, and nothing a run was
/// given or produced: never a junction's input or output, the train's input or output, a failure's
/// message, or the state a decider was shown. A question's answer is summarised as the option, score
/// or probability the run acted on, and is left out, with <see cref="AnswerWithheld"/> set, when the
/// question is about a type marked <c>[TraxSensitive]</c>.</para>
///
/// <para>Published only by a host that called <c>AddJunctionEvents()</c>. The same values are stored
/// in <c>trax.junction_run</c>, one row per <see cref="Position"/>.</para>
/// </remarks>
/// <param name="Position">
/// Where the step falls in the run, from 0. A junction's start and end carry the same position, and
/// ordering by it gives the run's timeline.
/// </param>
/// <param name="Kind">What the step is.</param>
/// <param name="Name">
/// The junction's class name without its namespace, or for a question or a track, the question's key.
/// </param>
/// <param name="State">Where the step stands after this event.</param>
/// <param name="StartedAt">When the junction started, or when the question was answered or the track taken (UTC).</param>
/// <param name="EndedAt">When the junction returned (UTC), or null for a start.</param>
/// <param name="DurationMs">
/// Milliseconds from <paramref name="StartedAt"/> to <paramref name="EndedAt"/>, or null for a start.
/// </param>
/// <param name="FailureClass">
/// How a failed junction's failure is classified, decided as the run's own failure would be; null
/// unless the junction failed.
/// </param>
/// <param name="FailureException">
/// The type name of the exception a junction failed or was cancelled with, never its message.
/// </param>
/// <param name="QuestionKey">The question's key, for a question or a track.</param>
/// <param name="Answer">
/// The answer the run acted on: an option's name, a score or a probability of yes (invariant
/// culture, round-trip format), or for a track, the track's name. Null for a junction, a refused
/// answer, and a withheld one.
/// </param>
/// <param name="Confidence">How sure the decider was, for a choice or a score.</param>
/// <param name="Replayed">True when the answer came from an earlier run rather than a decider.</param>
/// <param name="Decider">The full name of the decider's type, or null when the answer was replayed.</param>
/// <param name="AnswerWithheld">
/// True when the question is about a type marked <c>[TraxSensitive]</c>, so its answer, confidence
/// and track are left out.
/// </param>
/// <param name="Attempt">
/// Which attempt of a manifest's run this is. Not yet filled in: always null.
/// </param>
public sealed record JunctionEventPayload(
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("kind")]
    [property: JsonConverter(typeof(JsonStringEnumConverter<JunctionRunKind>))]
        JunctionRunKind Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("state")]
    [property: JsonConverter(typeof(JsonStringEnumConverter<JunctionRunState>))]
        JunctionRunState State,
    [property: JsonPropertyName("startedAt")] DateTime StartedAt,
    [property: JsonPropertyName("endedAt")] DateTime? EndedAt = null,
    [property: JsonPropertyName("durationMs")] double? DurationMs = null,
    [property: JsonPropertyName("failureClass")]
    [property: JsonConverter(typeof(JsonStringEnumConverter<FailureClass>))]
        FailureClass? FailureClass = null,
    [property: JsonPropertyName("failureException")] string? FailureException = null,
    [property: JsonPropertyName("questionKey")] string? QuestionKey = null,
    [property: JsonPropertyName("answer")] string? Answer = null,
    [property: JsonPropertyName("confidence")] double? Confidence = null,
    [property: JsonPropertyName("replayed")] bool Replayed = false,
    [property: JsonPropertyName("decider")] string? Decider = null,
    [property: JsonPropertyName("answerWithheld")] bool AnswerWithheld = false,
    [property: JsonPropertyName("attempt")] int? Attempt = null
);
