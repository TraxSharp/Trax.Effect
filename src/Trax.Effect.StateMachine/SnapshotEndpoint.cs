using System.Text.Json.Nodes;

namespace Trax.Effect.StateMachine;

/// <summary>
/// The total result of <see cref="SnapshotEndpoint{TState,TTrigger}.Advance"/>. Mirrors what a
/// GraphQL mutation would hand back: either the new serialized snapshot, a typed rejection (the
/// trigger was declined), or a typed load error (the stored JSON could not be rehydrated). Auth is
/// deliberately NOT modeled here — it is enforced one layer up (Trax's <c>[TraxAuthorize]</c>),
/// which is where an auth denial becomes a typed <c>TRAX_AUTHORIZATION</c> error.
/// </summary>
public abstract record SnapshotEndpointResult
{
    /// <summary>The trigger was applied; <see cref="SnapshotJson"/> is the serialized successor to persist.</summary>
    public sealed record Ok(string SnapshotJson) : SnapshotEndpointResult;

    /// <summary>The trigger was declined; <see cref="Reason"/> is one of <see cref="RejectionReasons"/>.</summary>
    public sealed record Rejected(string Reason, string? Detail) : SnapshotEndpointResult;

    /// <summary>The stored snapshot could not be loaded; <see cref="Code"/> is one of <see cref="RehydrationErrorCodes"/>.</summary>
    public sealed record LoadError(string Code, string Message) : SnapshotEndpointResult;

    private SnapshotEndpointResult() { }
}

/// <summary>
/// A thin, transport-agnostic boundary that a GraphQL resolver (or any caller) can sit behind:
/// load the stored snapshot, apply a trigger, hand back the serialized successor — all as a single
/// total operation that never throws. It composes <see cref="SnapshotMachine{TState,TTrigger}"/>'s
/// two total primitives; there is no HotChocolate/Trax dependency here, so it is unit-testable in
/// isolation and reusable by a real feature later.
/// </summary>
public sealed class SnapshotEndpoint<TState, TTrigger>
    where TState : struct, Enum
    where TTrigger : struct, Enum
{
    private readonly SnapshotMachine<TState, TTrigger> _machine;

    public SnapshotEndpoint(SnapshotMachine<TState, TTrigger> machine) => _machine = machine;

    /// <summary>
    /// Rehydrate <paramref name="storedJson"/>, apply <paramref name="trigger"/>, and return the
    /// serialized successor to persist — or a typed error/rejection. Pass <c>null</c> for
    /// <paramref name="storedJson"/> to start a fresh instance at the definition's initial snapshot.
    /// </summary>
    public SnapshotEndpointResult Advance(
        string? storedJson,
        string trigger,
        JsonNode? input = null
    )
    {
        Snapshot current;
        if (storedJson is null)
        {
            current = _machine.Definition.CreateInitialSnapshot();
        }
        else
        {
            switch (_machine.Rehydrate(storedJson))
            {
                case RehydrationResult.Ok ok:
                    current = ok.Snapshot;
                    break;
                case RehydrationResult.Error err:
                    return new SnapshotEndpointResult.LoadError(err.Code, err.Message);
                default:
                    return new SnapshotEndpointResult.LoadError(
                        RehydrationErrorCodes.Malformed,
                        "Unknown rehydration result."
                    );
            }
        }

        return _machine.Advance(current, trigger, input) switch
        {
            AdvanceResult.Transitioned t => new SnapshotEndpointResult.Ok(
                _machine.Serialize(t.Snapshot)
            ),
            AdvanceResult.Rejected r => new SnapshotEndpointResult.Rejected(r.Reason, r.Detail),
            _ => new SnapshotEndpointResult.Rejected(
                RejectionReasons.InternalError,
                "Unknown advance result."
            ),
        };
    }
}
