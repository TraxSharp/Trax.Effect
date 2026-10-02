using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Trax.Core.Decisions;

namespace Trax.Effect.Services.Decisions;

/// <summary>
/// One of the decision observers the composite tells, registered under its own key.
/// </summary>
/// <param name="Key">The key the observer is registered under.</param>
/// <param name="Tag">Identifies a Trax registration, so registering it again adds nothing.</param>
internal sealed record DecisionObserverPart(object Key, Type? Tag);

/// <summary>
/// The <see cref="IDecisionObserver"/> Trax.Core finds in the container when Trax registers one of
/// its own: it tells every registered observer, so decision recording, junction events and a
/// host's own observer each hear every decision, rather than the one registered first or last
/// silently replacing the others.
/// </summary>
/// <remarks>
/// <para>The required observers are told first, in the order they were registered, and the first
/// failure among them propagates, which fails the step as it would for that observer alone: a
/// decision that could not be recorded is not acted on, and is not reported as made either. Then the
/// best-effort observers are told, each failure logged and swallowed.</para>
///
/// <para>An observer registered before Trax's own is folded in when Trax's is added. One registered
/// after it, as <see cref="IDecisionObserver"/>, is what the container returns instead, so while a
/// required observer is a part, <see cref="DecisionObserverCheck"/> refuses the host's start and every
/// run that records its decisions; register it before <c>AddTrax</c> to have both.</para>
/// </remarks>
internal sealed class CompositeDecisionObserver : IDecisionObserver
{
    private readonly IReadOnlyList<IDecisionObserver> _required;
    private readonly IReadOnlyList<IDecisionObserver> _bestEffort;
    private readonly ILogger<CompositeDecisionObserver>? _logger;

    public CompositeDecisionObserver(
        IServiceProvider services,
        IEnumerable<DecisionObserverPart> parts,
        ILogger<CompositeDecisionObserver>? logger = null
    )
    {
        var observers = parts
            .Select(part => services.GetRequiredKeyedService<IDecisionObserver>(part.Key))
            .ToList();

        _required = observers.Where(o => o.Required).ToList();
        _bestEffort = observers.Where(o => !o.Required).ToList();
        _logger = logger;
    }

    /// <summary>
    /// True when any part is required. Only a required part's failure leaves the composite, so a
    /// failure that reaches Trax.Core always fails the step.
    /// </summary>
    public bool Required => _required.Count > 0;

    /// <inheritdoc />
    public Task Decided(DecisionMade decision, CancellationToken cancellationToken) =>
        TellAll((o, ct) => o.Decided(decision, ct), cancellationToken);

    /// <inheritdoc />
    public Task Routed(TrackRouted routing, CancellationToken cancellationToken) =>
        TellAll((o, ct) => o.Routed(routing, ct), cancellationToken);

    /// <inheritdoc />
    public Task Refused(DecisionRefused refusal, CancellationToken cancellationToken) =>
        TellAll((o, ct) => o.Refused(refusal, ct), cancellationToken);

    private async Task TellAll(
        Func<IDecisionObserver, CancellationToken, Task> tell,
        CancellationToken cancellationToken
    )
    {
        foreach (var observer in _required)
            await tell(observer, cancellationToken).ConfigureAwait(false);

        foreach (var observer in _bestEffort)
        {
            try
            {
                await tell(observer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger?.LogWarning(
                    e,
                    "Decision observer ({Observer}) threw; it is best effort, so the decision stands.",
                    observer.GetType().Name
                );
            }
        }
    }
}

/// <summary>
/// Registers decision observers so that every one of them is told, through
/// <see cref="CompositeDecisionObserver"/>.
/// </summary>
internal static class DecisionObservers
{
    /// <summary>
    /// Adds an observer made by <paramref name="factory"/> beside every other one, once per
    /// <paramref name="tag"/>. Any <see cref="IDecisionObserver"/> registered directly before this
    /// call is folded into the composite with its own lifetime.
    /// </summary>
    public static void Add(
        IServiceCollection services,
        Type tag,
        Func<IServiceProvider, IDecisionObserver> factory,
        ServiceLifetime lifetime
    )
    {
        FoldExisting(services);

        if (
            services.Any(d =>
                d.ImplementationInstance is DecisionObserverPart { } p && p.Tag == tag
            )
        )
            return;

        var key = new object();
        services.Add(
            new ServiceDescriptor(typeof(IDecisionObserver), key, (sp, _) => factory(sp), lifetime)
        );
        services.AddSingleton(new DecisionObserverPart(key, tag));
    }

    private static void FoldExisting(IServiceCollection services)
    {
        foreach (
            var descriptor in services
                .Where(d =>
                    d.ServiceType == typeof(IDecisionObserver)
                    && !d.IsKeyedService
                    && d.ImplementationType != typeof(CompositeDecisionObserver)
                )
                .ToList()
        )
        {
            var key = new object();
            services.Remove(descriptor);
            services.Add(Keyed(descriptor, key));
            services.AddSingleton(new DecisionObserverPart(key, null));
        }

        if (
            !services.Any(d =>
                d.ServiceType == typeof(IDecisionObserver)
                && !d.IsKeyedService
                && d.ImplementationType == typeof(CompositeDecisionObserver)
            )
        )
        {
            services.AddTransient<IDecisionObserver, CompositeDecisionObserver>();
            services.AddSingleton<DecisionObserverCheck>();
            services.AddHostedService(sp => sp.GetRequiredService<DecisionObserverCheck>());
        }
    }

    private static ServiceDescriptor Keyed(ServiceDescriptor descriptor, object key) =>
        descriptor switch
        {
            { ImplementationInstance: { } instance } => new ServiceDescriptor(
                typeof(IDecisionObserver),
                key,
                instance
            ),
            { ImplementationFactory: { } factory } => new ServiceDescriptor(
                typeof(IDecisionObserver),
                key,
                (sp, _) => factory(sp),
                descriptor.Lifetime
            ),
            _ => new ServiceDescriptor(
                typeof(IDecisionObserver),
                key,
                descriptor.ImplementationType!,
                descriptor.Lifetime
            ),
        };
}
