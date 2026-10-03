using System.Globalization;
using Trax.Core.Decisions;
using Trax.Effect.Enums;
using Trax.Effect.Services.TrainEventBroadcaster;

namespace Trax.Effect.Services.JunctionEvents;

/// <summary>
/// Turns the decisions a run makes into junction events: each question answered (<c>Decided</c>),
/// each answer refused (<c>DecisionRefused</c>), and each track taken (<c>Routed</c>). Registered by
/// <c>AddJunctionEvents</c> beside any other decision observer, never in place of one.
/// </summary>
/// <remarks>
/// Best effort: it is not <see cref="IDecisionObserver.Required"/>, and it reports only after every
/// required observer has recorded the decision. A decision of a run that has no junction events on
/// its flow, such as a plain train run inside a junction, is not reported. The state the question
/// was about, its instructions and criteria, a shadow's answers and a refusal's reason are never
/// published; an answer to a question about a type marked <c>[TraxSensitive]</c> is withheld.
/// </remarks>
internal sealed class JunctionEventDecisionObserver : IDecisionObserver
{
    /// <inheritdoc />
    public async Task Decided(DecisionMade decision, CancellationToken cancellationToken)
    {
        if (
            JunctionEventRun.ForDecision(decision.Train, decision.RunId) is not { } run
            || KindOf(decision.Question) is not { } kind
        )
            return;

        var key = decision.Question.Key;
        var withheld = SensitiveQuestions.IsSensitive(decision.QuestionType, key);
        var (answer, confidence) = withheld ? (null, null) : Summarise(decision.Answer);
        var now = DateTime.UtcNow;

        await run.Publish(
            TrainLifecycleEventMessage.DecidedEventType,
            new JunctionEventPayload(
                run.NextPosition(),
                kind,
                key,
                JunctionRunState.Completed,
                now,
                now,
                0,
                QuestionKey: key,
                Answer: answer,
                Confidence: confidence,
                Replayed: decision.Replayed,
                Decider: decision.Decider?.FullName,
                AnswerWithheld: withheld
            )
        );
    }

    /// <inheritdoc />
    public async Task Refused(DecisionRefused refusal, CancellationToken cancellationToken)
    {
        if (
            JunctionEventRun.ForDecision(refusal.Train, refusal.RunId) is not { } run
            || KindOf(refusal.Question) is not { } kind
        )
            return;

        var key = refusal.Question.Key;
        var now = DateTime.UtcNow;

        // The refused answer and the reason are left out: the answer is one the run would not act
        // on, and the reason quotes it.
        await run.Publish(
            TrainLifecycleEventMessage.DecisionRefusedEventType,
            new JunctionEventPayload(
                run.NextPosition(),
                kind,
                key,
                JunctionRunState.Failed,
                now,
                now,
                0,
                QuestionKey: key,
                Decider: refusal.Decider.FullName,
                AnswerWithheld: SensitiveQuestions.IsSensitive(refusal.QuestionType, key)
            )
        );
    }

    /// <inheritdoc />
    public async Task Routed(TrackRouted routing, CancellationToken cancellationToken)
    {
        if (JunctionEventRun.ForDecision(routing.Train, routing.RunId) is not { } run)
            return;

        var key = QuestionKey.For(routing.On);
        var withheld = SensitiveQuestions.IsSensitive(routing.On);
        var now = DateTime.UtcNow;

        var position = run.NextPosition();

        // Junctions from here on are on this track; when its answer is withheld, so are their
        // names, which would give the track away.
        run.Routed(position, withheld);

        // The track taken gives the answer away, so it is withheld with it.
        await run.Publish(
            TrainLifecycleEventMessage.RoutedEventType,
            new JunctionEventPayload(
                position,
                JunctionRunKind.Route,
                key,
                JunctionRunState.Completed,
                now,
                now,
                0,
                QuestionKey: key,
                Answer: withheld ? null : routing.Track,
                AnswerWithheld: withheld
            )
        );
    }

    private static JunctionRunKind? KindOf(Question question) =>
        question switch
        {
            ChoiceQuestion => JunctionRunKind.Choice,
            ScoreQuestion => JunctionRunKind.Score,
            YesNoQuestion => JunctionRunKind.YesNo,
            _ => null,
        };

    private static (string? Answer, double? Confidence) Summarise(Answer answer) =>
        answer switch
        {
            ChoiceAnswer choice => (choice.Choice, Finite(choice.Confidence)),
            ScoreAnswer score => (Number(score.Score), Finite(score.Confidence)),
            YesNoAnswer yesNo => (Number(yesNo.Probability), null),
            _ => (null, null),
        };

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    // JSON has no form for NaN or an infinity, and a confidence outside 0..1 means nothing.
    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
}
