using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Trax.Effect.Extensions;
using Trax.Effect.StateMachine.Persistence.Mutations;

namespace Trax.Effect.StateMachine.Persistence;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// <b>Obsolete.</b> Prefer <c>trax.AddStateMachines(...)</c> inside <c>AddTrax</c>: the builder form also
    /// auto-registers the <see cref="SnapshotDbContext"/> and contributes the generic mutations to the mediator
    /// scan, so the host writes one call. This <see cref="IServiceCollection"/> form has no builder access, so a
    /// host using it must still register <c>AddDbContext&lt;SnapshotDbContext&gt;</c> and add
    /// <see cref="Mutations.StateMachineMutations.Assembly"/> to its <c>AddMediator(...)</c> scan.
    /// </summary>
    [Obsolete(
        "Prefer trax.AddStateMachines(...) inside AddTrax; the IServiceCollection form cannot auto-wire the "
            + "mediator scan or the SnapshotDbContext."
    )]
    public static IServiceCollection AddTraxStateMachines(
        this IServiceCollection services,
        params Assembly[] assemblies
    )
    {
        RegisterMachinesAndStores(services, null, assemblies);
        return services;
    }

    /// <summary>
    /// <b>Obsolete.</b> As <see cref="AddTraxStateMachines(IServiceCollection, Assembly[])"/>, with host-level
    /// options (see <see cref="StateMachineOptions"/>). Prefer <c>trax.AddStateMachines(...)</c>.
    /// </summary>
    [Obsolete(
        "Prefer trax.AddStateMachines(...) inside AddTrax; the IServiceCollection form cannot auto-wire the "
            + "mediator scan or the SnapshotDbContext."
    )]
    public static IServiceCollection AddTraxStateMachines(
        this IServiceCollection services,
        Action<StateMachineOptions> configure,
        params Assembly[] assemblies
    )
    {
        RegisterMachinesAndStores(services, configure, assemblies);
        return services;
    }

    /// <summary>
    /// Shared registration for both the obsolete <see cref="IServiceCollection"/> form and the
    /// <c>trax.AddStateMachines(...)</c> builder form: the options, machine discovery, the store, the
    /// effect-claim ledger, the exactly-once runner, the machine registry, and the four generic
    /// <c>stateMachine</c> mutation routes. It does NOT register the <see cref="SnapshotDbContext"/> or touch
    /// the mediator scan; the builder form layers those on.
    /// </summary>
    internal static void RegisterMachinesAndStores(
        IServiceCollection services,
        Action<StateMachineOptions>? configure,
        Assembly[] assemblies
    )
    {
        var options = new StateMachineOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        var machineTypes = assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type =>
                type is { IsAbstract: false, IsClass: true }
                && typeof(IMachine).IsAssignableFrom(type)
            )
            .Distinct()
            .ToList();

        if (machineTypes.Count == 0)
            throw new InvalidOperationException(
                "No state machines were found. Pass the assemblies that contain your "
                    + "Machine<TState, TTrigger> subclasses, e.g. trax.AddStateMachines(typeof(Program).Assembly)."
            );

        foreach (var type in machineTypes)
            services.AddSingleton(typeof(IMachine), type);

        services.AddScoped<ISnapshotStore, EfSnapshotStore>();
        services.AddScoped<IEffectClaimStore, EfEffectClaimStore>();
        services.AddScoped<IdempotentEffect>();
        services.AddScoped<ISnapshotMachineRegistry, SnapshotMachineRegistry>();

        services.AddScopedTraxRoute<ISaveSnapshot, SaveSnapshot>();
        services.AddScopedTraxRoute<IAdvanceSnapshot, AdvanceSnapshot>();
        services.AddScopedTraxRoute<ILoadSnapshot, LoadSnapshot>();
        services.AddScopedTraxRoute<ISendSnapshot, SendSnapshot>();
    }
}
