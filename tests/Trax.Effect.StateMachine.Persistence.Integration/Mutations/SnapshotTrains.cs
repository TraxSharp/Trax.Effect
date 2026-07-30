using LanguageExt;
using Trax.Effect.Attributes;
using Trax.Effect.Services.ServiceTrain;

namespace Trax.Effect.StateMachine.Persistence.Integration.Mutations;

/// <summary>
/// Autosave a turnstile draft (soft path). This is the shape a machine's mutation fan takes — a thin
/// <see cref="ServiceTrain{TIn,TOut}"/> whose body is a one-line junction chain, gated by
/// <c>[TraxAuthorize]</c>, exposed as a <c>[TraxMutation]</c>. The generator emits one of these per
/// machine (doc 09); it is verbatim modulo the state/trigger type parameters and the namespace.
/// </summary>
[TraxAuthorize]
[TraxMutation(
    GraphQLOperation.Run,
    Namespace = "turnstileDrafts",
    Description = "Autosave: validate a client-provided snapshot and persist it (soft path)."
)]
public class SaveTurnstileSnapshot
    : ServiceTrain<SaveSnapshotInput, SaveSnapshotOutput>,
        ISaveTurnstileSnapshot
{
    protected override Task<Either<Exception, SaveSnapshotOutput>> Junctions() =>
        Chain<SaveTurnstileSnapshotJunction>().Resolve();
}

/// <summary>Advance a stored turnstile draft by one trigger, server-side (authoritative path).</summary>
[TraxAuthorize]
[TraxMutation(
    GraphQLOperation.Run,
    Namespace = "turnstileDrafts",
    Description = "Advance: re-drive a stored draft by one trigger, server-side (authoritative path)."
)]
public class AdvanceTurnstileSnapshot
    : ServiceTrain<AdvanceSnapshotInput, AdvanceSnapshotOutput>,
        IAdvanceTurnstileSnapshot
{
    protected override Task<Either<Exception, AdvanceSnapshotOutput>> Junctions() =>
        Chain<AdvanceTurnstileSnapshotJunction>().Resolve();
}
