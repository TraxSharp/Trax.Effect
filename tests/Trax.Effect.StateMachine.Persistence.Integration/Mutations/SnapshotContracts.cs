namespace Trax.Effect.StateMachine.Persistence.Integration.Mutations;

// Distinct GraphQL input/output types per mutation. A Trax ServiceTrain registers its input/output as
// GraphQL types, so two trains cannot share one CLR type without a schema-type-name collision — which is
// why this fan is mechanical, per-machine duplication that a generator emits (doc 09).

public record SaveSnapshotInput
{
    /// <summary>The draft id, scoped to the authenticated user.</summary>
    public required Guid Id { get; init; }

    /// <summary>The whole client-computed snapshot as canonical JSON. The server validates it before storing.</summary>
    public required string Snapshot { get; init; }
}

public record SaveSnapshotOutput
{
    /// <summary>The stored snapshot (canonical JSON) on success.</summary>
    public string? Snapshot { get; init; }

    /// <summary>Set when the client snapshot was rejected; the draft was NOT stored.</summary>
    public SnapshotProblem? Problem { get; init; }
}

public record AdvanceSnapshotInput
{
    /// <summary>The draft id to advance.</summary>
    public required Guid Id { get; init; }

    /// <summary>The trigger to fire (a machine trigger name).</summary>
    public required string Trigger { get; init; }

    /// <summary>Optional trigger input as JSON.</summary>
    public string? Input { get; init; }

    /// <summary>Optional idempotency key — a stable value so a retry replays instead of re-firing.</summary>
    public string? RequestId { get; init; }
}

public record AdvanceSnapshotOutput
{
    /// <summary>The new stored snapshot (canonical JSON) after a successful server-computed transition.</summary>
    public string? Snapshot { get; init; }

    /// <summary>Set when the trigger was declined, or the draft was missing/stale (typed, not a crash).</summary>
    public SnapshotProblem? Problem { get; init; }
}
