using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using LanguageExt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trax.Core.Decisions;
using Trax.Core.Exceptions;
using Trax.Effect.Attributes;
using Trax.Effect.Data.Extensions;
using Trax.Effect.Data.InMemory.Extensions;
using Trax.Effect.Data.JunctionEvents;
using Trax.Effect.Data.Services.DataContext;
using Trax.Effect.Enums;
using Trax.Effect.Extensions;
using Trax.Effect.Models.JunctionRun;
using Trax.Effect.Services.EffectJunction;
using Trax.Effect.Services.FailureClassifier;
using Trax.Effect.Services.ServiceTrain;
using Trax.Effect.Services.TrainEventBroadcaster;

namespace Trax.Effect.Tests.Data.InMemory.Integration.IntegrationTests;

/// <summary>
/// With <c>AddJunctionEvents</c>, each step of a run (a junction starting and ending, a question
/// answered, a track taken) is published live and stored in <c>trax.junction_run</c>, carrying
/// names, times, states and failure classes, and never what the run was given or produced.
///
/// <para>Enforces docs/adr/0019-junction-events-are-opt-in-and-carry-no-run-data.md.</para>
/// </summary>
[Property("adr", "docs/adr/0019-junction-events-are-opt-in-and-carry-no-run-data.md")]
public class JunctionEventsTests
{
    private const string Adr = "docs/adr/0019-junction-events-are-opt-in-and-carry-no-run-data.md";

    private ServiceProvider _provider = null!;

    private static readonly CapturingHandler Handler = new();
    private static readonly CapturingBroadcaster Broadcaster = new();
    private static readonly SwitchableDecider Decider = new();

    [OneTimeSetUp]
    public void Build() =>
        _provider = JunctionEventTrains
            .Register(new ServiceCollection(), Decider)
            .AddSingleton<IJunctionEventHandler>(Handler)
            .AddSingleton<ITrainEventBroadcaster>(Broadcaster)
            .AddSingleton<IFailureClassifier, WarehouseClassifier>()
            .AddTrax(trax =>
                trax.AddEffects(effects =>
                    effects.UseInMemory().AddJunctionEvents().AddJunctionEvents()
                )
            )
            .BuildServiceProvider();

    [OneTimeTearDown]
    public async Task Dispose() => await _provider.DisposeAsync();

    [Test]
    public async Task A_run_publishes_each_junction_and_decision_in_order()
    {
        Decider.Use(new ScriptedDecider().Choose(Lane.Express, 0.9));

        var (metadataId, output) = await Run<ILaneTrain>();

        output.Should().Be("loaded|stamped");
        var events = Handler.For(metadataId);
        events
            .Select(e => (e.EventType, e.Junction!.Position, e.Junction.Name))
            .Should()
            .Equal(
                ("JunctionStarted", 0, nameof(Weigh)),
                ("JunctionCompleted", 0, nameof(Weigh)),
                ("Decided", 1, "Lane"),
                ("Routed", 2, "Lane"),
                ("JunctionStarted", 3, nameof(Load)),
                ("JunctionCompleted", 3, nameof(Load)),
                ("JunctionStarted", 4, nameof(Stamp)),
                ("JunctionCompleted", 4, nameof(Stamp))
            );

        events
            .Should()
            .OnlyContain(e =>
                e.TrainName == typeof(ILaneTrain).FullName
                && e.TrainState == "InProgress"
                && e.Output == null
                && e.FailureReason == null
            );

        var decided = events.Single(e => e.EventType == "Decided").Junction!;
        decided.Kind.Should().Be(JunctionRunKind.Choice);
        decided.State.Should().Be(JunctionRunState.Completed);
        decided.QuestionKey.Should().Be("Lane");
        decided.Answer.Should().Be("Express");
        decided.Confidence.Should().Be(0.9);
        decided.Replayed.Should().BeFalse();
        decided.Decider.Should().Be(typeof(SwitchableDecider).FullName);

        var routed = events.Single(e => e.EventType == "Routed").Junction!;
        routed.Kind.Should().Be(JunctionRunKind.Route);
        routed.Answer.Should().Be("Express");

        var load = events.Where(e => e.Junction!.Name == nameof(Load)).ToList();
        load[0].Junction!.State.Should().Be(JunctionRunState.InProgress);
        load[0].Junction!.EndedAt.Should().BeNull();
        load[1].Junction!.State.Should().Be(JunctionRunState.Completed);
        load[1].Junction!.EndedAt.Should().NotBeNull();
        load[1].Junction!.DurationMs.Should().BeGreaterThanOrEqualTo(0);
        load[1].Junction!.StartedAt.Should().Be(load[0].Junction!.StartedAt);

        Broadcaster
            .For(metadataId)
            .Select(e => e.EventType)
            .Should()
            .Equal(events.Select(e => e.EventType), "the same steps go over the transport");
    }

    [Test]
    public async Task A_failing_junction_publishes_its_class_and_exception_type_and_the_skipped_one_nothing()
    {
        Decider.Use(new ScriptedDecider().Choose(Lane.Ground, 0.8));

        var (metadataId, _) = await Run<ILaneThenFailTrain>(expectFailure: true);

        var events = Handler.For(metadataId);
        var failed = events.Should().ContainSingle(e => e.EventType == "JunctionFailed").Subject;
        failed.Junction!.Name.Should().Be(nameof(Explode));
        failed.Junction.State.Should().Be(JunctionRunState.Failed);
        failed.Junction.FailureClass.Should().Be(FailureClass.Permanent);
        failed.Junction.FailureException.Should().Be(nameof(InvalidOperationException));
        failed.Junction.EndedAt.Should().NotBeNull();
        events.Should().NotContain(e => e.Junction!.Name == nameof(NeverReached));
        events.Last().Should().BeSameAs(failed);
    }

    [Test]
    public async Task The_timeline_is_stored_and_read_back_in_order()
    {
        Decider.Use(new ScriptedDecider().Choose(Lane.Ground, 0.8));

        var (metadataId, _) = await Run<ILaneThenFailTrain>(expectFailure: true);

        var rows = await Rows(metadataId);
        rows.Select(r => (r.Position, r.Kind, r.Name, r.State))
            .Should()
            .Equal(
                (0, JunctionRunKind.Junction, nameof(Weigh), JunctionRunState.Completed),
                (1, JunctionRunKind.Choice, "Lane", JunctionRunState.Completed),
                (2, JunctionRunKind.Route, "Lane", JunctionRunState.Completed),
                (3, JunctionRunKind.Junction, nameof(LoadSecret), JunctionRunState.Completed),
                (4, JunctionRunKind.Junction, nameof(Stamp), JunctionRunState.Completed),
                (5, JunctionRunKind.Junction, nameof(Explode), JunctionRunState.Failed)
            );

        rows[1].Answer.Should().Be("Ground");
        rows[1].Confidence.Should().Be(0.8);
        rows[2].Answer.Should().Be("Ground");
        rows[5].FailureClass.Should().Be(FailureClass.Permanent);
        rows[5].FailureException.Should().Be(nameof(InvalidOperationException));
        rows.Where(r => r.Kind == JunctionRunKind.Junction)
            .Should()
            .OnlyContain(r => r.EndedAt != null && r.EndedAt >= r.StartedAt);
    }

    [Test]
    public async Task Nothing_the_run_was_given_or_produced_is_published_or_stored()
    {
        Decider.Use(new ScriptedDecider().Choose(Lane.Ground, 0.8));

        var (metadataId, _) = await Run<ILaneThenFailTrain>(expectFailure: true);

        var wire = Broadcaster.For(metadataId).Select(m => JsonSerializer.Serialize(m)).ToList();
        var local = Handler.For(metadataId).Select(m => JsonSerializer.Serialize(m)).ToList();
        var stored = (await Rows(metadataId)).Select(r => JsonSerializer.Serialize(r)).ToList();

        wire.Should().NotBeEmpty();
        foreach (var json in wire.Concat(local).Concat(stored))
        {
            json.Should()
                .NotContain(
                    JunctionEventTrains.InputSecret,
                    $"a train's input is never published. See {Adr}."
                );
            json.Should()
                .NotContain(
                    JunctionEventTrains.OutputSecret,
                    $"a junction's output is never published. See {Adr}."
                );
            json.Should()
                .NotContain(
                    JunctionEventTrains.FailureSecret,
                    $"a failure's message is never published. See {Adr}."
                );
            json.Should()
                .NotContain(
                    "Which lane",
                    $"a question's instructions are never published. See {Adr}."
                );
        }
    }

    [Test]
    public async Task The_answer_to_a_question_about_a_sensitive_type_is_withheld()
    {
        Decider.Use(new ScriptedDecider().Choose(CustomsTier.Red, 0.99));

        var (metadataId, _) = await Run<ICustomsTrain>();

        var events = Handler.For(metadataId);
        var decided = events.Single(e => e.EventType == "Decided").Junction!;
        decided.QuestionKey.Should().Be("CustomsTier");
        decided.AnswerWithheld.Should().BeTrue();
        decided.Answer.Should().BeNull();
        decided.Confidence.Should().BeNull();

        var routed = events.Single(e => e.EventType == "Routed").Junction!;
        routed.AnswerWithheld.Should().BeTrue();
        routed.Answer.Should().BeNull($"the track taken would give the answer away. See {Adr}.");

        foreach (var json in Broadcaster.For(metadataId).Select(m => JsonSerializer.Serialize(m)))
            json.Should().NotContain("\"Red\"");

        (await Rows(metadataId))
            .Where(r => r.Kind != JunctionRunKind.Junction)
            .Should()
            .OnlyContain(r => r.AnswerWithheld && r.Answer == null && r.Confidence == null);
    }

    [Test]
    public async Task Junction_events_are_off_unless_the_host_asks_for_them()
    {
        var handler = new CapturingHandler();
        await using var provider = JunctionEventTrains
            .Register(new ServiceCollection(), Decider)
            .AddSingleton<IJunctionEventHandler>(handler)
            .AddTrax(trax => trax.AddEffects(effects => effects.UseInMemory()))
            .BuildServiceProvider();
        Decider.Use(new ScriptedDecider().Choose(Lane.Express, 0.9));

        using var scope = provider.CreateScope();
        var train = scope.ServiceProvider.GetRequiredService<ILaneTrain>();
        await train.Run(JunctionEventTrains.Parcel());

        handler.All.Should().BeEmpty($"junction events are off unless the host asks. See {Adr}.");
        var context = scope.ServiceProvider.GetRequiredService<IDataContext>();
        (await context.JunctionRuns.CountAsync()).Should().Be(0);
    }

    [Test]
    public async Task A_handler_or_broadcaster_that_throws_does_not_change_the_run()
    {
        await using var provider = JunctionEventTrains
            .Register(new ServiceCollection(), Decider)
            .AddSingleton<IJunctionEventHandler, ThrowingHandler>()
            .AddSingleton<ITrainEventBroadcaster, ThrowingBroadcaster>()
            .AddTrax(trax => trax.AddEffects(effects => effects.UseInMemory().AddJunctionEvents()))
            .BuildServiceProvider();
        Decider.Use(new ScriptedDecider().Choose(Lane.Express, 0.9));

        using var scope = provider.CreateScope();
        var train = scope.ServiceProvider.GetRequiredService<ILaneTrain>();
        var output = await train.Run(JunctionEventTrains.Parcel());

        output.Should().Be("loaded|stamped");
        train.Metadata!.TrainState.Should().Be(TrainState.Completed);

        await provider.GetRequiredService<JunctionRunWriter>().FlushAsync();
        var context = scope.ServiceProvider.GetRequiredService<IDataContext>();
        (await context.JunctionRuns.AsNoTracking().ForRun(train.Metadata.Id).CountAsync())
            .Should()
            .Be(5, "storing does not depend on the handlers or the transport");
    }

    [Test]
    public async Task A_host_observer_decision_recording_and_junction_events_are_all_told()
    {
        var observer = new CountingObserver();
        var handler = new CapturingHandler();
        var services = JunctionEventTrains.Register(new ServiceCollection(), Decider);

        // Registered before AddTrax: it used to win, and silently turn decision recording off.
        services.AddSingleton<IDecisionObserver>(observer);

        await using var provider = services
            .AddSingleton<IJunctionEventHandler>(handler)
            .AddTrax(trax =>
                trax.AddEffects(effects =>
                    effects.UseInMemory().AddJunctionEvents().AddDecisionRecording()
                )
            )
            .BuildServiceProvider();
        Decider.Use(new ScriptedDecider().Choose(Lane.Express, 0.9));

        using var scope = provider.CreateScope();
        var train = scope.ServiceProvider.GetRequiredService<ILaneTrain>();
        await train.Run(JunctionEventTrains.Parcel());

        observer.Decided.Should().Be(1);
        observer.Routed.Should().Be(1);
        handler.For(train.Metadata!.Id).Should().Contain(e => e.EventType == "Decided");
        var context = scope.ServiceProvider.GetRequiredService<IDataContext>();
        (await context.RecordedDecisions.CountAsync(d => d.MetadataId == train.Metadata.Id))
            .Should()
            .Be(1, "decision recording is told as well");
    }

    [Test]
    public async Task A_decision_a_required_observer_could_not_record_is_not_reported_as_made()
    {
        var handler = new CapturingHandler();
        var services = JunctionEventTrains.Register(new ServiceCollection(), Decider);
        services.AddSingleton<IDecisionObserver, FailingRequiredObserver>();

        await using var provider = services
            .AddSingleton<IJunctionEventHandler>(handler)
            .AddTrax(trax => trax.AddEffects(effects => effects.UseInMemory().AddJunctionEvents()))
            .BuildServiceProvider();
        Decider.Use(new ScriptedDecider().Choose(Lane.Express, 0.9));

        using var scope = provider.CreateScope();
        var train = scope.ServiceProvider.GetRequiredService<ILaneTrain>();
        var run = async () => await train.Run(JunctionEventTrains.Parcel());

        await run.Should().ThrowAsync<Exception>();
        handler
            .For(train.Metadata!.Id)
            .Should()
            .NotContain(e => e.EventType == "Decided" || e.EventType == "Routed");
    }

    private async Task<(long MetadataId, string? Output)> Run<TTrain>(bool expectFailure = false)
        where TTrain : class, IServiceTrain<Parcel, string>
    {
        using var scope = _provider.CreateScope();
        var train = scope.ServiceProvider.GetRequiredService<TTrain>();
        string? output = null;

        try
        {
            output = await train.Run(JunctionEventTrains.Parcel());
            expectFailure.Should().BeFalse("the run completed");
        }
        catch (Exception) when (expectFailure) { }

        var serviceTrain = (ServiceTrain<Parcel, string>)(object)train;
        await _provider.GetRequiredService<JunctionRunWriter>().FlushAsync();
        return (serviceTrain.Metadata!.Id, output);
    }

    private async Task<List<JunctionRun>> Rows(long metadataId)
    {
        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IDataContext>();
        return await context.JunctionRuns.AsNoTracking().ForRun(metadataId).ToListAsync();
    }
}

internal static class JunctionEventTrains
{
    public const string InputSecret = "INPUT-SECRET-7f3a";
    public const string OutputSecret = "OUTPUT-SECRET-91c2";
    public const string FailureSecret = "FAILURE-SECRET-5d0e";

    public static Parcel Parcel() => new($"p-{Guid.NewGuid():N}", InputSecret);

    public static IServiceCollection Register(IServiceCollection services, IDecider decider) =>
        services
            .AddSingleton(decider)
            .AddScopedTraxRoute<ILaneTrain, LaneTrain>()
            .AddScopedTraxRoute<ILaneThenFailTrain, LaneThenFailTrain>()
            .AddScopedTraxRoute<ICustomsTrain, CustomsTrain>();
}

/// <summary>Records every junction event it is handed.</summary>
internal sealed class CapturingHandler : IJunctionEventHandler
{
    private readonly ConcurrentQueue<TrainLifecycleEventMessage> _seen = new();

    public IReadOnlyList<TrainLifecycleEventMessage> All => _seen.ToList();

    public List<TrainLifecycleEventMessage> For(long metadataId) =>
        _seen.Where(m => m.MetadataId == metadataId).ToList();

    public Task HandleAsync(TrainLifecycleEventMessage message, CancellationToken ct)
    {
        _seen.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>Records every message published to the transport.</summary>
internal sealed class CapturingBroadcaster : ITrainEventBroadcaster
{
    private readonly ConcurrentQueue<TrainLifecycleEventMessage> _seen = new();

    public List<TrainLifecycleEventMessage> For(long metadataId) =>
        _seen.Where(m => m.MetadataId == metadataId).ToList();

    public Task PublishAsync(TrainLifecycleEventMessage message, CancellationToken ct)
    {
        _seen.Enqueue(message);
        return Task.CompletedTask;
    }
}

internal sealed class ThrowingHandler : IJunctionEventHandler
{
    public Task HandleAsync(TrainLifecycleEventMessage message, CancellationToken ct) =>
        throw new InvalidOperationException("the handler is broken");
}

internal sealed class ThrowingBroadcaster : ITrainEventBroadcaster
{
    public Task PublishAsync(TrainLifecycleEventMessage message, CancellationToken ct) =>
        throw new InvalidOperationException("the broker is down");
}

internal sealed class CountingObserver : IDecisionObserver
{
    private int _decided;
    private int _routed;

    public int Decided => _decided;
    public int Routed => _routed;

    Task IDecisionObserver.Decided(DecisionMade decision, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _decided);
        return Task.CompletedTask;
    }

    Task IDecisionObserver.Routed(TrackRouted routing, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _routed);
        return Task.CompletedTask;
    }
}

/// <summary>A required observer whose every write fails.</summary>
internal sealed class FailingRequiredObserver : IDecisionObserver
{
    public bool Required => true;

    public Task Decided(DecisionMade decision, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("the journal is down");

    public Task Routed(TrackRouted routing, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("the journal is down");
}

/// <summary>The registered decider, swapped per test.</summary>
internal sealed class SwitchableDecider : IDecider
{
    private IDecider _inner = new ScriptedDecider();

    public void Use(IDecider inner) => _inner = inner;

    public Task<DecisionResult> Decide(DecisionRequest request, CancellationToken ct) =>
        _inner.Decide(request, ct);
}

internal sealed class WarehouseClassifier : IFailureClassifier
{
    public FailureClass? Classify(Exception exception) =>
        exception is InvalidOperationException ? FailureClass.Permanent : null;
}

public sealed record Parcel(string Id, [property: TraxSensitive] string Secret);

[Asks("Which lane should this parcel take?")]
public enum Lane
{
    Express,
    Ground,
}

[TraxSensitive]
[Asks("Which customs tier applies to this parcel?")]
public enum CustomsTier
{
    Green,
    Red,
}

public class Weigh : EffectJunction<Parcel, Parcel>
{
    public override Task<Parcel> Run(Parcel input) => Task.FromResult(input);
}

public class Load : EffectJunction<Parcel, string>
{
    public override Task<string> Run(Parcel input) => Task.FromResult("loaded");
}

public class LoadSecret : EffectJunction<Parcel, string>
{
    public override Task<string> Run(Parcel input) =>
        Task.FromResult(JunctionEventTrains.OutputSecret);
}

public class Stamp : EffectJunction<string, string>
{
    public override Task<string> Run(string input) => Task.FromResult($"{input}|stamped");
}

public class Explode : EffectJunction<string, string>
{
    public override Task<string> Run(string input) =>
        throw new InvalidOperationException(JunctionEventTrains.FailureSecret);
}

public class NeverReached : EffectJunction<string, string>
{
    public override Task<string> Run(string input) => Task.FromResult(input);
}

public interface ILaneTrain : IServiceTrain<Parcel, string>;

public class LaneTrain : ServiceTrain<Parcel, string>, ILaneTrain
{
    protected override Task<Either<Exception, string>> Junctions() =>
        Chain<Weigh>()
            .Switch<Parcel, Lane>(tracks =>
                tracks
                    .When(Lane.Express, t => t.Chain<Load>())
                    .When(Lane.Ground, t => t.Chain<Load>())
            )
            .Chain<Stamp>()
            .Resolve();
}

public interface ILaneThenFailTrain : IServiceTrain<Parcel, string>;

/// <summary>Its output carries a secret, and its last junction fails with one in the message.</summary>
public class LaneThenFailTrain : ServiceTrain<Parcel, string>, ILaneThenFailTrain
{
    protected override Task<Either<Exception, string>> Junctions() =>
        Chain<Weigh>()
            .Switch<Parcel, Lane>(tracks =>
                tracks
                    .When(Lane.Express, t => t.Chain<LoadSecret>())
                    .When(Lane.Ground, t => t.Chain<LoadSecret>())
            )
            .Chain<Stamp>()
            .Chain<Explode>()
            .Chain<NeverReached>()
            .Resolve();
}

public interface ICustomsTrain : IServiceTrain<Parcel, string>;

public class CustomsTrain : ServiceTrain<Parcel, string>, ICustomsTrain
{
    protected override Task<Either<Exception, string>> Junctions() =>
        Switch<Parcel, CustomsTier>(tracks =>
                tracks
                    .When(CustomsTier.Green, t => t.Chain<Load>())
                    .When(CustomsTier.Red, t => t.Chain<Load>())
            )
            .Resolve();
}
