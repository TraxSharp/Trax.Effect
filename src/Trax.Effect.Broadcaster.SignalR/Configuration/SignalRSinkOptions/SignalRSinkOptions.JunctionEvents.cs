using Trax.Effect.Broadcaster.SignalR.Services;
using Trax.Effect.Services.TrainEventBroadcaster;

namespace Trax.Effect.Broadcaster.SignalR.Configuration.SignalRSinkOptions;

public partial class SignalRSinkOptions
{
    /// <summary>
    /// Sends junction events (each step of a run, from a host that called <c>AddJunctionEvents()</c>)
    /// to clients through the <c>"JunctionEvent"</c> client method, as a <c>TraxJunctionClientEvent</c>.
    /// Off by default: without this call no junction event reaches a client.
    /// </summary>
    /// <remarks>
    /// A junction event goes to the same clients as its train's own events and passes the same
    /// filters: <see cref="OnlyForTrains(Type[])"/> applies to it, and so does
    /// <see cref="OnlyForEvents"/> when it was called, in which case the junction event types to
    /// send (<c>"JunctionStarted"</c>, <c>"JunctionCompleted"</c>, <c>"JunctionFailed"</c>,
    /// <c>"JunctionCancelled"</c>, <c>"Decided"</c>, <c>"DecisionRefused"</c>, <c>"Routed"</c>)
    /// must be listed too. The default payload carries no input, output or failure message, and no
    /// question's answer or confidence: a failed junction is described by its exception's type and
    /// failure class, and a question by its key. <see cref="WithJunctionAnswers"/> adds the answers;
    /// <see cref="WithJunctionProjection{TClient}"/> replaces the payload. <see cref="WithProjection{TClient}"/>
    /// shapes train events only.
    /// </remarks>
    public SignalRSinkOptions WithJunctionEvents()
    {
        _junctionEvents = true;
        return this;
    }

    /// <summary>
    /// Sends junction events, as <see cref="WithJunctionEvents"/> does, and includes in each
    /// question's payload the answer the run acted on and the decider's confidence.
    /// </summary>
    /// <remarks>
    /// Every client connected to the hub receives every train's events, so this puts every
    /// decision's answer in front of all of them. Answers to questions about a type marked
    /// <c>[TraxSensitive]</c> stay withheld. Replaced by <see cref="WithJunctionProjection{TClient}"/>
    /// when both are called.
    /// </remarks>
    public SignalRSinkOptions WithJunctionAnswers()
    {
        _junctionEvents = true;
        _junctionProjection = message =>
            DefaultTraxJunctionClientEventProjection.Project(message, answers: true);
        return this;
    }

    /// <summary>
    /// Sends junction events, as <see cref="WithJunctionEvents"/> does, shaped by
    /// <paramref name="projection"/> instead of the default <c>TraxJunctionClientEvent</c>. The
    /// message's <see cref="TrainLifecycleEventMessage.Junction"/> is never null here.
    /// </summary>
    /// <typeparam name="TClient">The shape sent to SignalR clients. Must be JSON-serializable.</typeparam>
    public SignalRSinkOptions WithJunctionProjection<TClient>(
        Func<TrainLifecycleEventMessage, TClient> projection
    )
        where TClient : notnull
    {
        if (projection is null)
        {
            throw new ArgumentNullException(
                nameof(projection),
                "WithJunctionProjection() requires a non-null projection. "
                    + "Omit the call to use the default TraxJunctionClientEvent projection."
            );
        }

        _junctionEvents = true;
        _junctionProjection = message => projection(message);
        return this;
    }
}
