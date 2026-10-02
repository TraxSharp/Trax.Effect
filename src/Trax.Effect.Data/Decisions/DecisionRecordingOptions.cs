namespace Trax.Effect.Data.Decisions;

/// <summary>
/// How <c>AddDecisionRecording</c> records and replays decisions. Configure it with
/// <c>AddDecisionRecording(o =&gt; ...)</c>.
/// </summary>
public sealed class DecisionRecordingOptions
{
    /// <summary>The default for <see cref="ReplayAnswersFor"/>: 24 hours.</summary>
    public static readonly TimeSpan DefaultMaxReplayAge = TimeSpan.FromHours(24);

    internal DecisionRecordingOptions() { }

    /// <summary>How long after a decider gave an answer it is still replayed.</summary>
    public TimeSpan MaxReplayAge { get; private set; } = DefaultMaxReplayAge;

    /// <summary>
    /// Replays a recorded answer into a repeated run (a requeue, a manifest's retry) only while it
    /// is younger than <paramref name="maxAge"/>, measured from when a decider gave it, not from a
    /// later run that replayed it. An older one is asked afresh and the decision logged. Defaults to
    /// <see cref="DefaultMaxReplayAge"/>.
    /// </summary>
    /// <param name="maxAge">
    /// At least one second. <see cref="TimeSpan.MaxValue"/>, or any span longer than the calendar
    /// goes back, replays answers of any age.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxAge"/> is under a second.</exception>
    public DecisionRecordingOptions ReplayAnswersFor(TimeSpan maxAge)
    {
        if (maxAge < TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(
                nameof(maxAge),
                maxAge,
                "ReplayAnswersFor() requires at least one second. Omit the call to replay answers "
                    + $"for the default of {DefaultMaxReplayAge}."
            );

        MaxReplayAge = maxAge;
        return this;
    }
}
