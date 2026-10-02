using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Trax.Core.Decisions;

namespace Trax.Effect.Services.Decisions;

/// <summary>
/// Refuses a host whose container hands Trax.Core an <see cref="IDecisionObserver"/> other than the
/// composite while a required observer, such as decision recording, is registered as one of its
/// parts. That happens when an observer is registered after <c>AddTrax</c>: the container returns
/// the last registration, so the required observer would never be told, and a decision would be
/// acted on without being recorded.
/// </summary>
/// <remarks>
/// It checks once, from a scope of its own. The host's start refuses (in <see cref="StartingAsync"/>,
/// before any other hosted service starts, or in <see cref="StartAsync"/> for a harness that skips
/// the lifecycle steps), and so does every run that would record its decisions
/// (<see cref="ThrowIfReplaced"/>), so a host built without the generic host fails closed too.
/// </remarks>
internal sealed class DecisionObserverCheck(IServiceProvider services) : IHostedLifecycleService
{
    private readonly Lazy<string?> _problem = new(() => Find(services));
    private bool _checked;

    /// <summary>Throws when a required decision observer would not be told about decisions.</summary>
    /// <exception cref="InvalidOperationException">An observer replaced the composite.</exception>
    public void ThrowIfReplaced()
    {
        if (_problem.Value is { } problem)
            throw new InvalidOperationException(problem);
    }

    private static string? Find(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;

        if (provider.GetService<IDecisionObserver>() is CompositeDecisionObserver or null)
            return null;

        var required = provider
            .GetServices<DecisionObserverPart>()
            .Select(part => provider.GetRequiredKeyedService<IDecisionObserver>(part.Key))
            .Where(observer => observer.Required)
            .Select(observer => observer.GetType().Name)
            .ToList();

        if (required.Count == 0)
            return null;

        return "An IDecisionObserver ("
            + provider.GetService<IDecisionObserver>()!.GetType().FullName
            + ") was registered after AddTrax(...), so the container hands it to every train in "
            + "place of the observers Trax composes, and "
            + string.Join(", ", required)
            + " would never be told about a decision the run acts on. Register your observer "
            + "before AddTrax(...); Trax then tells it alongside its own.";
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        _checked = true;
        ThrowIfReplaced();
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _checked ? Task.CompletedTask : StartingAsync(cancellationToken);

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
