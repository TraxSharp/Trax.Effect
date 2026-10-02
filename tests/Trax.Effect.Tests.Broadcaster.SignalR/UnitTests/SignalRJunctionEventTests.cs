using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Trax.Core.Exceptions;
using Trax.Effect.Broadcaster.SignalR.Configuration;
using Trax.Effect.Broadcaster.SignalR.Configuration.SignalRSinkOptions;
using Trax.Effect.Broadcaster.SignalR.Extensions;
using Trax.Effect.Broadcaster.SignalR.Models;
using Trax.Effect.Broadcaster.SignalR.Services;
using Trax.Effect.Configuration.TraxBuilder;
using Trax.Effect.Enums;
using Trax.Effect.Extensions;
using Trax.Effect.Services.EffectRegistry;
using Trax.Effect.Services.TrainEventBroadcaster;
using Trax.Effect.Tests.Broadcaster.SignalR.Fakes.Trains;

namespace Trax.Effect.Tests.Broadcaster.SignalR.UnitTests;

/// <summary>
/// Junction events reach SignalR clients only from a sink that asked for them, only for the trains
/// its filters already send, and through their own client method, carrying no failure message.
///
/// <para>Enforces docs/adr/0019-junction-events-are-opt-in-and-carry-no-run-data.md.</para>
/// </summary>
[TestFixture]
[Property("adr", "docs/adr/0019-junction-events-are-opt-in-and-carry-no-run-data.md")]
public class SignalRJunctionEventTests
{
    private const string Adr = "docs/adr/0019-junction-events-are-opt-in-and-carry-no-run-data.md";

    private IHubContext<TraxTrainEventHub, ITraxTrainEventClient> _hub = null!;
    private ITraxTrainEventClient _client = null!;
    private readonly List<SignalRTrainEventDispatcher> _created = [];

    [SetUp]
    public void SetUp()
    {
        _hub = Substitute.For<IHubContext<TraxTrainEventHub, ITraxTrainEventClient>>();
        _client = Substitute.For<ITraxTrainEventClient>();
        var clients = Substitute.For<IHubClients<ITraxTrainEventClient>>();
        clients.All.Returns(_client);
        _hub.Clients.Returns(clients);
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (var dispatcher in _created)
            await dispatcher.DisposeAsync();
        _created.Clear();
    }

    private static SignalRSinkOptions NewOptions() =>
        (SignalRSinkOptions)Activator.CreateInstance(typeof(SignalRSinkOptions), nonPublic: true)!;

    private SignalRTrainEventDispatcher Create(SignalRSinkConfiguration config)
    {
        var dispatcher = new SignalRTrainEventDispatcher(_hub, config);
        _created.Add(dispatcher);
        return dispatcher;
    }

    private static TrainLifecycleEventMessage Step(
        string trainName = "Some.Other.IFoo",
        string eventType = "JunctionFailed"
    ) =>
        new(
            MetadataId: 9,
            ExternalId: "run-9",
            TrainName: trainName,
            TrainState: "InProgress",
            Timestamp: new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc),
            FailureJunction: null,
            FailureReason: null,
            EventType: eventType,
            Executor: null,
            Output: null
        )
        {
            Junction = new JunctionEventPayload(
                2,
                JunctionRunKind.Junction,
                "Ship",
                JunctionRunState.Failed,
                new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 10, 2, 0, 0, 2, DateTimeKind.Utc),
                2000,
                FailureClass.Transient,
                "TimeoutException"
            ),
        };

    [Test]
    public async Task A_sink_that_did_not_ask_for_junction_events_sends_none()
    {
        var d = Create(NewOptions().Build());

        await d.HandleAsync(Step(), CancellationToken.None);
        await d.StopAsync(CancellationToken.None);

        _client
            .ReceivedCalls()
            .Should()
            .BeEmpty($"the SignalR sink sends junction events only when asked. See {Adr}.");
    }

    [Test]
    public async Task A_sink_that_asked_sends_each_step_through_JunctionEvent_without_a_failure_message()
    {
        var d = Create(NewOptions().WithJunctionEvents().Build());
        object? sent = null;
        await _client.JunctionEvent(Arg.Do<object>(p => sent = p));

        await d.HandleAsync(Step(), CancellationToken.None);
        await d.StopAsync(CancellationToken.None);

        await _client.DidNotReceive().TrainEvent(Arg.Any<object>());
        var step = sent.Should().BeOfType<TraxJunctionClientEvent>().Subject;
        step.Name.Should().Be("Ship");
        step.State.Should().Be("Failed");
        step.FailureClass.Should().Be("Transient");
        step.FailureException.Should().Be("TimeoutException");
        JsonSerializer.Serialize(step).Should().NotContain("failureReason");
    }

    [Test]
    public async Task The_train_filter_applies_to_junction_events()
    {
        var d = Create(
            NewOptions().WithJunctionEvents().OnlyForTrains<ICheckGeocodeDriftTrain>().Build()
        );

        await d.HandleAsync(Step(), CancellationToken.None);
        await d.HandleAsync(
            Step(typeof(ICheckGeocodeDriftTrain).FullName!),
            CancellationToken.None
        );
        await d.StopAsync(CancellationToken.None);

        await _client
            .Received(1)
            .JunctionEvent(
                Arg.Is<object>(p =>
                    ((TraxJunctionClientEvent)p).TrainName
                    == typeof(ICheckGeocodeDriftTrain).FullName
                )
            );
    }

    [Test]
    public async Task An_event_type_filter_must_list_the_junction_event_types_it_sends()
    {
        var d = Create(NewOptions().WithJunctionEvents().OnlyForEvents("Completed").Build());

        await d.HandleAsync(Step(), CancellationToken.None);
        await d.StopAsync(CancellationToken.None);

        _client.ReceivedCalls().Should().BeEmpty();
    }

    [Test]
    public async Task A_withheld_answer_is_not_sent()
    {
        var d = Create(NewOptions().WithJunctionEvents().Build());
        object? sent = null;
        await _client.JunctionEvent(Arg.Do<object>(p => sent = p));
        var message = Step(eventType: "Decided");
        message = message with
        {
            Junction = message.Junction! with
            {
                Kind = JunctionRunKind.Choice,
                State = JunctionRunState.Completed,
                Answer = "Red",
                Confidence = 0.9,
                AnswerWithheld = true,
            },
        };

        await d.HandleAsync(message, CancellationToken.None);
        await d.StopAsync(CancellationToken.None);

        var step = sent.Should().BeOfType<TraxJunctionClientEvent>().Subject;
        step.AnswerWithheld.Should().BeTrue();
        step.Answer.Should().BeNull();
        step.Confidence.Should().BeNull();
    }

    [Test]
    public void Only_a_sink_that_asked_registers_as_a_junction_event_handler()
    {
        using var off = Provider(o => { });
        using var on = Provider(o => o.WithJunctionEvents());

        off.GetServices<IJunctionEventHandler>().Should().BeEmpty();
        on.GetServices<IJunctionEventHandler>()
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeSameAs(on.GetRequiredService<SignalRTrainEventDispatcher>());
    }

    private static ServiceProvider Provider(Action<SignalRSinkOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        new TraxBuilder(services, new EffectRegistry()).AddEffects(effects =>
            effects.UseBroadcaster(b => b.UseSignalRHub(configure))
        );
        return services.BuildServiceProvider();
    }
}
