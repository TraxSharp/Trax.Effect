namespace Trax.Effect.StateMachine.Persistence.Integration.Mutations;

// Marker interfaces give each train a clean GraphQL response type name (Trax derives the name from the
// train's marker interface; without one it falls back to the generic ServiceTrain<...> base name, which
// is not a valid GraphQL name).
public interface ISaveTurnstileSnapshot { }

/// <inheritdoc cref="ISaveTurnstileSnapshot" />
public interface IAdvanceTurnstileSnapshot { }
