using Trax.Effect.Models.Metadata;
using Trax.Effect.Services.Decisions;
using Trax.Effect.Services.TrainEventBroadcaster;

namespace Trax.Effect.Services.JunctionEvents;

/// <summary>
/// One run's junction events: the run they are about, and the next position in its timeline.
/// </summary>
/// <remarks>
/// It lives on the run's own async flow (<see cref="Current"/>), set by <c>ServiceTrain.Run</c>
/// for a host that called <c>AddJunctionEvents</c> and cleared when its junctions finish, as
/// <see cref="DecisionRun"/> is. A train run inside a junction sets its own for its own flow, so
/// each run's steps are numbered and reported under its own row.
/// </remarks>
internal sealed class JunctionEventRun
{
    private static readonly AsyncLocal<JunctionEventRun?> CurrentRun = new();

    private readonly JunctionEventPublisher _publisher;
    private int _position = -1;

    public JunctionEventRun(
        JunctionEventPublisher publisher,
        Metadata metadata,
        Type train,
        IServiceProvider services,
        int? attempt = null
    )
    {
        Attempt = attempt;
        _publisher = publisher;
        Metadata = metadata;
        ExternalId = metadata.ExternalId;
        DecisionTrain = DecisionRun.NameOf(train);
        Services = services;
    }

    /// <summary>The run on this async flow, or null when junction events are off or no run is going.</summary>
    public static JunctionEventRun? Current
    {
        get => CurrentRun.Value;
        set => CurrentRun.Value = value;
    }

    /// <summary>The run on this flow when it is the run <paramref name="metadata"/> records.</summary>
    public static JunctionEventRun? For(Metadata? metadata) =>
        Current is { } run && metadata is not null && ReferenceEquals(run.Metadata, metadata)
            ? run
            : null;

    /// <summary>
    /// The run on this flow when Trax.Core reported a decision for it: the same train, under the
    /// run's external id. A decision of another train run on the same flow is not this run's.
    /// </summary>
    public static JunctionEventRun? ForDecision(string train, string runId) =>
        Current is { } run && run.DecisionTrain == train && run.ExternalId == runId ? run : null;

    /// <summary>The run's row.</summary>
    public Metadata Metadata { get; }

    /// <summary>The run's external id when it began.</summary>
    public string ExternalId { get; }

    /// <summary>The train as Trax.Core names it in its decisions.</summary>
    public string DecisionTrain { get; }

    /// <summary>
    /// Which attempt of its manifest the run is, worked out once when it began, or null for a run
    /// with no manifest or when it could not be worked out.
    /// </summary>
    public int? Attempt { get; }

    /// <summary>The run's scope, which local junction event handlers are resolved from.</summary>
    public IServiceProvider Services { get; }

    /// <summary>The next position in the run's timeline.</summary>
    public int NextPosition() => Interlocked.Increment(ref _position);

    /// <summary>Publishes one step. Never throws.</summary>
    public Task Publish(string eventType, JunctionEventPayload step) =>
        _publisher.Publish(this, eventType, step);
}
