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
    /// must be listed too. The payload carries no input, output or failure message; a failed
    /// junction is described by its exception's type and failure class.
    /// </remarks>
    public SignalRSinkOptions WithJunctionEvents()
    {
        _junctionEvents = true;
        return this;
    }
}
