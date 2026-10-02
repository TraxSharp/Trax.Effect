using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Trax.Effect.Enums;
using Trax.Effect.Extensions;
using Trax.Effect.Models.Metadata;
using Trax.Effect.Services.TrainEventBroadcaster;

namespace Trax.Effect.Services.JunctionEvents;

/// <summary>
/// Publishes each step of a run: stores it through the <see cref="IJunctionRunSink"/>, sends it over
/// the <see cref="ITrainEventBroadcaster"/> when the host has one, and hands it to the run's local
/// <see cref="IJunctionEventHandler"/>s. Registered by <c>AddJunctionEvents</c>; its presence is what
/// turns junction events on for a run.
/// </summary>
/// <remarks>
/// Nothing it does can fail a run or change a junction's result. The store only queues; the
/// broadcaster, as shipped, only queues; and every failure, of the store, the transport or a
/// handler, is logged and swallowed.
/// </remarks>
internal sealed class JunctionEventPublisher
{
    private static readonly string? LocalExecutor = Assembly
        .GetEntryAssembly()
        ?.GetAssemblyProject();

    private readonly IServiceProvider _root;
    private readonly ILogger<JunctionEventPublisher>? _logger;
    private readonly Lazy<ITrainEventBroadcaster?> _broadcaster;
    private readonly Lazy<IJunctionRunSink?> _sink;
    private readonly Lazy<string> _instanceId;

    public JunctionEventPublisher(
        IServiceProvider root,
        ILogger<JunctionEventPublisher>? logger = null
    )
    {
        _root = root;
        _logger = logger;
        _broadcaster = new(() => _root.GetService<ITrainEventBroadcaster>());
        _sink = new(() => _root.GetService<IJunctionRunSink>());
        _instanceId = new(() =>
            (_root.GetService<BroadcastInstance>() ?? BroadcastInstance.Unregistered).Id
        );
    }

    /// <summary>Begins the junction events of the run <paramref name="metadata"/> records.</summary>
    public JunctionEventRun Begin(Metadata metadata, Type train, IServiceProvider services) =>
        new(this, metadata, train, services);

    /// <summary>Stores, broadcasts and hands out one step. Never throws.</summary>
    public async Task Publish(JunctionEventRun run, string eventType, JunctionEventPayload step)
    {
        var metadata = run.Metadata;

        // A run that was never persisted has no row to write against.
        if (metadata.Id > 0)
        {
            try
            {
                _sink.Value?.Write(metadata.Id, step);
            }
            catch (Exception e)
            {
                _logger?.LogWarning(
                    e,
                    "Could not store step {Position} ({Name}) of run {ExternalId}; the run carries on.",
                    step.Position,
                    step.Name,
                    run.ExternalId
                );
            }
        }

        var message = new TrainLifecycleEventMessage(
            MetadataId: metadata.Id,
            ExternalId: metadata.ExternalId,
            TrainName: metadata.Name,
            TrainState: TrainState.InProgress.ToString(),
            Timestamp: DateTime.UtcNow,
            FailureJunction: null,
            FailureReason: null,
            EventType: eventType,
            Executor: LocalExecutor,
            Output: null,
            HostName: metadata.HostName,
            HostEnvironment: metadata.HostEnvironment
        )
        {
            InstanceId = _instanceId.Value,
            Junction = step,
        };

        try
        {
            if (_broadcaster.Value is { } broadcaster)
                await broadcaster.PublishAsync(message, CancellationToken.None);
        }
        catch (Exception e)
        {
            _logger?.LogWarning(
                e,
                "Could not broadcast {EventType} for step {Position} ({Name}) of run {ExternalId}; the run carries on.",
                eventType,
                step.Position,
                step.Name,
                run.ExternalId
            );
        }

        IEnumerable<IJunctionEventHandler> handlers;
        try
        {
            handlers = run.Services.GetServices<IJunctionEventHandler>().ToList();
        }
        catch (Exception e)
        {
            _logger?.LogWarning(
                e,
                "Could not resolve the junction event handlers for run {ExternalId}; the run carries on.",
                run.ExternalId
            );
            return;
        }

        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(message, CancellationToken.None);
            }
            catch (Exception e)
            {
                _logger?.LogWarning(
                    e,
                    "JunctionEventHandler ({HandlerType}) threw while handling {EventType} for run {ExternalId}; the run carries on.",
                    handler.GetType().Name,
                    eventType,
                    run.ExternalId
                );
            }
        }
    }
}
