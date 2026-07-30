using System.Text.Json;
using System.Text.Json.Nodes;
using Trax.Core.Junction;
using Trax.Effect.StateMachine.Persistence.Integration.Fakes;

namespace Trax.Effect.StateMachine.Persistence.Integration.Mutations;

/// <summary>
/// The autosave resolver (soft path): read the principal, validate + store the client snapshot, and map
/// the total service result to a typed output. Constructor-injected, so it is directly unit-testable —
/// <c>new SaveTurnstileSnapshotJunction(service, principal).Run(input)</c> — with no GraphQL server, the
/// way Trax's own tests exercise junctions.
/// </summary>
public class SaveTurnstileSnapshotJunction(
    SnapshotDraftService<TurnstileState, TurnstileTrigger> service,
    ISnapshotPrincipal principal
) : Junction<SaveSnapshotInput, SaveSnapshotOutput>
{
    public override async Task<SaveSnapshotOutput> Run(SaveSnapshotInput input)
    {
        if (principal.CurrentUserKey is not { } userKey)
            return Problem(
                "unauthenticated",
                "No authenticated user is associated with this request."
            );

        return await service.Autosave(userKey, input.Id, input.Snapshot, CancellationToken) switch
        {
            AutosaveResult.Saved saved => new SaveSnapshotOutput
            {
                Snapshot = service.Serialize(saved.Snapshot),
            },
            AutosaveResult.Rejected rejected => Problem(rejected.Code, rejected.Message),
            AutosaveResult.Conflict => Problem(
                "conflict",
                "The draft changed elsewhere; reload and retry."
            ),
            _ => Problem("internal-error", "Unknown autosave result."),
        };
    }

    private static SaveSnapshotOutput Problem(string code, string message) =>
        new()
        {
            Problem = new SnapshotProblem { Code = code, Message = message },
        };
}

/// <summary>
/// The authoritative advance resolver: re-drive the stored draft by one trigger server-side, never
/// trusting a client-computed state. A declined trigger, a stale view, or a missing draft all come back
/// as typed problems (data), never a thrown error across the API boundary.
/// </summary>
public class AdvanceTurnstileSnapshotJunction(
    SnapshotDraftService<TurnstileState, TurnstileTrigger> service,
    ISnapshotPrincipal principal
) : Junction<AdvanceSnapshotInput, AdvanceSnapshotOutput>
{
    public override async Task<AdvanceSnapshotOutput> Run(AdvanceSnapshotInput input)
    {
        if (principal.CurrentUserKey is not { } userKey)
            return Problem(
                "unauthenticated",
                "No authenticated user is associated with this request."
            );

        JsonNode? triggerInput;
        try
        {
            triggerInput = string.IsNullOrEmpty(input.Input) ? null : JsonNode.Parse(input.Input);
        }
        catch (JsonException)
        {
            return Problem("malformed", "The trigger input is not valid JSON.");
        }

        return await service.Advance(
            userKey,
            input.Id,
            input.Trigger,
            triggerInput,
            input.RequestId,
            CancellationToken
        ) switch
        {
            AdvanceOutcome.Advanced advanced => new AdvanceSnapshotOutput
            {
                Snapshot = service.Serialize(advanced.Snapshot),
            },
            AdvanceOutcome.Rejected rejected => Problem(
                rejected.Reason,
                rejected.Detail ?? rejected.Reason
            ),
            AdvanceOutcome.NotFound => Problem("not-found", "No draft with that id."),
            AdvanceOutcome.LoadError loadError => Problem(loadError.Code, loadError.Message),
            AdvanceOutcome.Conflict => Problem(
                "conflict",
                "The draft changed elsewhere; reload and retry."
            ),
            _ => Problem("internal-error", "Unknown advance outcome."),
        };
    }

    private static AdvanceSnapshotOutput Problem(string code, string message) =>
        new()
        {
            Problem = new SnapshotProblem { Code = code, Message = message },
        };
}
