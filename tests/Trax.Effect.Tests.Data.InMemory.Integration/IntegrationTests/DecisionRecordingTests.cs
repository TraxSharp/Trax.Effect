using FluentAssertions;
using LanguageExt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trax.Core.Decisions;
using Trax.Core.Exceptions;
using Trax.Core.Junction;
using Trax.Effect.Data.Decisions;
using Trax.Effect.Data.Extensions;
using Trax.Effect.Data.InMemory.Extensions;
using Trax.Effect.Data.Services.DataContext;
using Trax.Effect.Data.Services.IDataContextFactory;
using Trax.Effect.Enums;
using Trax.Effect.Extensions;
using Trax.Effect.Models.Metadata;
using Trax.Effect.Models.Metadata.DTOs;
using Trax.Effect.Services.EffectJunction;
using Trax.Effect.Services.EffectProvider;
using Trax.Effect.Services.ServiceTrain;

namespace Trax.Effect.Tests.Data.InMemory.Integration.IntegrationTests;

/// <summary>
/// With <c>AddDecisionRecording</c>, every decision a train makes is written against its run as
/// it is made, and a run that names an earlier one in <c>ReplayDecisionsOf</c> takes the tracks
/// that run took instead of asking again.
/// </summary>
public class DecisionRecordingTests
{
    private ServiceProvider _provider = null!;

    private static readonly DeciderSlot Decider = new();

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [OneTimeSetUp]
    public void Build() =>
        _provider = DecisionTrains
            .Register(new ServiceCollection(), Decider)
            .AddTrax(trax =>
                trax.AddEffects(effects =>
                    effects.UseInMemory().AddDecisionRecording().AddDecisionRecording()
                )
            )
            .BuildServiceProvider();

    [OneTimeTearDown]
    public async Task Dispose() => await _provider.DisposeAsync();

    [Test]
    public async Task A_run_records_each_decision_and_the_track_it_took()
    {
        var decider = Decider.Use(
            new ScriptedDecider().Choose(Fulfilment.Standard, 0.97, model: "jev-1.13.0")
        );

        var (train, output) = await Run<IRouteOrder>(new Order("o1", 20m));

        output.Should().Be("shipped");
        var recorded = await Recorded(train.Metadata!.Id);
        var decision = recorded.Should().ContainSingle().Subject;
        decision.QuestionKey.Should().Be(QuestionKey.For<Fulfilment>());
        decision.QuestionKey.Should().Be(typeof(Fulfilment).FullName);
        decision.Occurrence.Should().Be(0);
        decision.Kind.Should().Be("choice");
        decision.Model.Should().Be("jev-1.13.0");
        decision.Decider.Should().Be(typeof(DeciderSlot).FullName);
        decision.Replayed.Should().BeFalse();
        decision.Track.Should().Be("Standard");
        decision.FallbackReason.Should().BeNull();
        decision.Answer.Should().Contain("\"choice\":\"Standard\"");
        decision.Question.Should().Contain("How should this order be fulfilled?");
        decider.Requests.Should().ContainSingle();
    }

    [Test]
    public async Task A_decision_that_was_not_followed_records_why()
    {
        Decider.Use(new ScriptedDecider().Choose(Fulfilment.Standard, 0.4));

        var (train, output) = await Run<IRouteOrder>(new Order("o2", 20m));

        output.Should().Be("held for review");
        var decision = (await Recorded(train.Metadata!.Id)).Should().ContainSingle().Subject;
        decision.Track.Should().Be("Otherwise");
        decision.FallbackReason.Should().Contain("confidence of 0.4");
    }

    [Test]
    public async Task A_decision_is_written_before_the_run_ends()
    {
        // What a run killed mid-way (an OOM, a timeout, a deploy) leaves behind is whatever was
        // written before it died. The junction after the switch reads the table through a
        // context of its own while the run is still in progress.
        Decider.Use(new ScriptedDecider().Choose(Fulfilment.ManualCheck));

        var (train, _) = await Run<IRouteThenPeek>(new Order("o-durable", 20m));

        var seen = PeekDecisions.Seen.Should().ContainSingle().Subject;
        seen.MetadataId.Should().Be(train.Metadata!.Id);
        seen.State.Should().Be(TrainState.InProgress);
        seen.Track.Should().Be("ManualCheck");
    }

    [Test]
    public async Task A_run_that_fails_after_deciding_still_records_the_decision()
    {
        Decider.Use(new ScriptedDecider().Choose(Fulfilment.ManualCheck));

        using var scope = _provider.CreateScope();
        var train = (RouteThenFail)scope.ServiceProvider.GetRequiredService<IRouteThenFail>();

        var run = () => train.Run(new Order("o3", 9000m));

        await run.Should().ThrowAsync<InvalidOperationException>();
        (await Recorded(train.Metadata!.Id))
            .Should()
            .ContainSingle()
            .Which.Track.Should()
            .Be("ManualCheck");
    }

    [Test]
    public async Task A_requeued_run_replays_the_original_decisions_without_asking()
    {
        Decider.Use(new ScriptedDecider().Choose(Fulfilment.ManualCheck));
        var (original, firstOutput) = await Run<IRouteOrder>(new Order("o4", 20m));
        firstOutput.Should().Be("held for review");

        // Asked now, the decider would ship it. The requeue must not ask.
        var decider = Decider.Use(new ScriptedDecider().Choose(Fulfilment.Standard));

        var (requeued, output) = await Run<IRouteOrder>(
            new Order("o4", 20m),
            replayDecisionsOf: original.Metadata!.Id
        );

        output.Should().Be("held for review", "the requeue takes the track the original took");
        decider.Requests.Should().BeEmpty();
        var replayed = (await Recorded(requeued.Metadata!.Id)).Should().ContainSingle().Subject;
        replayed.Replayed.Should().BeTrue();
        replayed.Decider.Should().BeNull();
        replayed.Track.Should().Be("ManualCheck");
    }

    [Test]
    public async Task A_decision_is_recorded_with_the_fingerprint_of_its_asking()
    {
        Decider.Use(new ScriptedDecider().Choose(Fulfilment.ManualCheck));
        var (original, _) = await Run<IRouteOrder>(new Order("o-print", 20m));
        Decider.Use(new ScriptedDecider().Choose(Fulfilment.ManualCheck));
        var (again, _) = await Run<IRouteOrder>(new Order("o-print-again", 99m));

        var first = (await Recorded(original.Metadata!.Id)).Should().ContainSingle().Subject;
        var second = (await Recorded(again.Metadata!.Id)).Should().ContainSingle().Subject;

        first.Fingerprint.Should().MatchRegex("^[0-9a-f]{64}$");
        second
            .Fingerprint.Should()
            .Be(first.Fingerprint, "the same step asks the same question; the state never counts");
    }

    [Test]
    public async Task A_recorded_answer_whose_fingerprint_differs_is_not_replayed()
    {
        Decider.Use(new ScriptedDecider().Choose(Fulfilment.ManualCheck));
        var (original, _) = await Run<IRouteOrder>(new Order("o-stale", 20m));

        // As if the question had been reworded since: the stored fingerprint is what comes back.
        await Rewrite(original.Metadata!.Id, d => d.Fingerprint = new string('0', 64));

        var decider = Decider.Use(new ScriptedDecider().Choose(Fulfilment.Standard));

        var (requeued, output) = await Run<IRouteOrder>(
            new Order("o-stale", 20m),
            replayDecisionsOf: original.Metadata.Id
        );

        output.Should().Be("shipped", "the decider was asked afresh");
        decider.Requests.Should().ContainSingle();
        var asked = (await Recorded(requeued.Metadata!.Id)).Should().ContainSingle().Subject;
        asked.Replayed.Should().BeFalse();
        asked.Answer.Should().Contain("replay_refused");
    }

    [Test]
    public async Task Each_asking_of_a_question_is_recorded_and_replayed_by_its_occurrence()
    {
        Decider.Use(new SequenceDecider(Fulfilment.ManualCheck, Fulfilment.Standard));
        var (original, firstOutput) = await Run<IRouteTwice>(new Order("o-twice", 20m));
        firstOutput.Should().Be("shipped");

        var recorded = (await Recorded(original.Metadata!.Id)).OrderBy(d => d.Occurrence).ToList();
        recorded.Select(d => d.Occurrence).Should().Equal(0, 1);
        recorded.Select(d => d.Track).Should().Equal("ManualCheck", "Standard");

        // Asked now, both askings would be answered the other way round.
        var decider = Decider.Use(new SequenceDecider(Fulfilment.Standard, Fulfilment.ManualCheck));

        var (requeued, output) = await Run<IRouteTwice>(
            new Order("o-twice", 20m),
            replayDecisionsOf: original.Metadata.Id
        );

        output.Should().Be("shipped");
        decider.Asked.Should().Be(0);
        (await Recorded(requeued.Metadata!.Id))
            .OrderBy(d => d.Occurrence)
            .Select(d => (d.Occurrence, d.Track, d.Replayed))
            .Should()
            .Equal((0, "ManualCheck", true), (1, "Standard", true));
    }

    [Test]
    public async Task A_replay_of_a_run_that_does_not_exist_fails_the_run_as_permanent()
    {
        var decider = Decider.Use(new ScriptedDecider().Choose(Fulfilment.Standard));

        var (train, run) = Start<IRouteOrder>(new Order("o5", 20m), replayDecisionsOf: 987_654_321);

        (await run.Should().ThrowAsync<TrainException>())
            .Which.Message.Should()
            .Contain("no run 987654321 exists");
        decider.Requests.Should().BeEmpty("the run fails before it asks anything");
        train.Metadata!.TrainState.Should().Be(TrainState.Failed);
        train.Metadata.FailureClass.Should().Be(FailureClass.Permanent);
    }

    [Test]
    public async Task A_replay_of_a_run_that_reached_fewer_questions_asks_the_rest_afresh()
    {
        Decider.Use(new SequenceDecider(Fulfilment.ManualCheck));
        var (original, _) = await Run<IRouteOrder>(new Order("o-fewer", 20m));

        var decider = Decider.Use(new SequenceDecider(Fulfilment.ManualCheck, Fulfilment.Standard));

        var (requeued, output) = await Run<IRouteTwice>(
            new Order("o-fewer", 20m),
            replayDecisionsOf: original.Metadata!.Id
        );

        output.Should().Be("held for review");
        decider.Asked.Should().Be(1, "only the second asking was never answered");
        (await Recorded(requeued.Metadata!.Id))
            .OrderBy(d => d.Occurrence)
            .Select(d => d.Replayed)
            .Should()
            .Equal(true, false);
    }

    [Test]
    public async Task A_run_started_from_a_row_it_was_given_takes_the_rows_external_id()
    {
        // The scheduler creates the row before dispatch. Without adopting its id, the run's
        // failure data and decisions named an id that matched no row.
        Decider.Use(new ScriptedDecider().Choose(Fulfilment.Standard));

        var (train, run) = Start<IRouteOrder>(new Order("o6", 20m), externalId: Id());
        await run();

        ((ServiceTrain<Order, string>)(object)train)
            .ExternalId.Should()
            .Be(train.Metadata!.ExternalId);
        (await Recorded(train.Metadata.Id)).Should().ContainSingle();
    }

    [Test]
    public async Task A_shadow_that_answers_with_numbers_JSON_cannot_hold_does_not_cost_the_live_record()
    {
        Decider.Use(new ScriptedDecider().Choose(Fulfilment.Standard));

        var (train, output) = await Run<IRouteWithShadow>(new Order("o-nan", 20m));

        output.Should().Be("shipped");
        var decision = (await Recorded(train.Metadata!.Id)).Should().ContainSingle().Subject;
        decision.Track.Should().Be("Standard");
        decision.Shadows.Should().Contain(typeof(NonFiniteShadow).FullName);
        decision.Shadows.Should().Contain("\"confidence\":\"NaN\"");
        decision.Shadows.Should().Contain("\"Standard\":\"Infinity\"");
    }

    [Test]
    public async Task Runs_sharing_an_external_id_each_record_and_replay_their_own()
    {
        // A retried dispatch can leave two rows under one external id, both running at once.
        Decider.Use(new ScriptedDecider().Choose(Fulfilment.ManualCheck));
        var (source, _) = await Run<IRouteOrder>(new Order("o-shared", 20m));

        var decider = Decider.Use(new ScriptedDecider().Choose(Fulfilment.Standard));
        var shared = Id();
        MeetBeforeDeciding.Expect(2);

        var (replaying, replayRun) = Start<IMeetThenRoute>(
            new Order("o-shared", 20m),
            externalId: shared,
            replayDecisionsOf: source.Metadata!.Id
        );
        var (asking, askRun) = Start<IMeetThenRoute>(
            new Order("o-shared", 20m),
            externalId: shared
        );

        var outputs = await Task.WhenAll(replayRun(), askRun()).WaitAsync(Wait);

        outputs.Should().Equal("held for review", "shipped");
        decider.Requests.Should().ContainSingle("only the run that replays nothing asks");
        replaying.Metadata!.Id.Should().NotBe(asking.Metadata!.Id);

        var replayed = (await Recorded(replaying.Metadata.Id)).Should().ContainSingle().Subject;
        replayed.Replayed.Should().BeTrue();
        replayed.Track.Should().Be("ManualCheck");

        var asked = (await Recorded(asking.Metadata.Id)).Should().ContainSingle().Subject;
        asked.Replayed.Should().BeFalse();
        asked.Track.Should().Be("Standard");
    }

    [Test]
    public async Task A_replay_ends_with_its_run_and_never_reaches_a_later_run_under_the_same_id()
    {
        Decider.Use(new ScriptedDecider().Choose(Fulfilment.ManualCheck));
        var (source, _) = await Run<IRouteOrder>(new Order("o-later", 20m));
        var shared = Id();

        var (_, failing) = Start<IRouteThenFail>(
            new Order("o-later", 20m),
            externalId: shared,
            replayDecisionsOf: source.Metadata!.Id
        );
        await failing.Should().ThrowAsync<InvalidOperationException>();

        var decider = Decider.Use(new ScriptedDecider().Choose(Fulfilment.Standard));
        var (later, run) = Start<IRouteOrder>(new Order("o-later", 20m), externalId: shared);

        (await run()).Should().Be("shipped");
        decider.Requests.Should().ContainSingle();
        (await Recorded(later.Metadata!.Id))
            .Should()
            .ContainSingle()
            .Which.Replayed.Should()
            .BeFalse();
    }

    [Test]
    public void Adding_decision_recording_twice_registers_it_once()
    {
        _provider.GetServices<IDecisionObserver>().Should().ContainSingle();
        _provider.GetServices<IDecisionReplay>().Should().ContainSingle();
        _provider.GetServices<DecisionJournal>().Should().ContainSingle();
    }

    private async Task<(TTrain Train, string Output)> Run<TTrain>(
        Order order,
        long? replayDecisionsOf = null
    )
        where TTrain : class, IServiceTrain<Order, string>
    {
        if (replayDecisionsOf is null)
        {
            using var scope = _provider.CreateScope();
            var train = scope.ServiceProvider.GetRequiredService<TTrain>();
            return (train, await train.Run(order));
        }

        var (started, run) = Start<TTrain>(order, replayDecisionsOf: replayDecisionsOf);
        return (started, await run());
    }

    private (TTrain Train, Func<Task<string>> Run) Start<TTrain>(
        Order order,
        string? externalId = null,
        long? replayDecisionsOf = null
    )
        where TTrain : class, IServiceTrain<Order, string>
    {
        var scope = _provider.CreateScope();
        var train =
            (ServiceTrain<Order, string>)(object)scope.ServiceProvider.GetRequiredService<TTrain>();

        var metadata = Metadata.Create(
            new CreateMetadata
            {
                Name = typeof(TTrain).FullName!,
                ExternalId = externalId ?? Id(),
                Input = order,
                ReplayDecisionsOf = replayDecisionsOf,
            }
        );

        return (
            (TTrain)(object)train,
            async () =>
            {
                using (scope)
                    return await train.Run(order, metadata);
            }
        );
    }

    private static string Id() => Guid.NewGuid().ToString("N");

    private async Task Rewrite(
        long metadataId,
        Action<Models.RecordedDecision.RecordedDecision> change
    )
    {
        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IDataContext>();

        foreach (var decision in context.RecordedDecisions.Where(d => d.MetadataId == metadataId))
            change(decision);

        await context.SaveChanges(CancellationToken.None);
    }

    private async Task<List<Models.RecordedDecision.RecordedDecision>> Recorded(long metadataId)
    {
        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IDataContext>();

        return await context
            .RecordedDecisions.AsNoTracking()
            .Where(d => d.MetadataId == metadataId)
            .ToListAsync();
    }
}

/// <summary>
/// A decision that cannot be written fails its step, and a replay a host cannot honour fails its
/// run, rather than either going ahead without the record.
/// </summary>
public class DecisionRecordingFailureTests
{
    private static readonly DeciderSlot Decider = new();

    [Test]
    public async Task A_decision_that_cannot_be_written_fails_its_step_as_transient()
    {
        await using var provider = DecisionTrains
            .Register(new ServiceCollection(), Decider)
            .AddSingleton(_ => new DecisionJournal(new DownFactory()))
            .AddTrax(trax =>
                trax.AddEffects(effects => effects.UseInMemory().AddDecisionRecording())
            )
            .BuildServiceProvider();
        var decider = Decider.Use(new ScriptedDecider().Choose(Fulfilment.Standard));
        Ship.Ran = 0;

        using var scope = provider.CreateScope();
        var train = (RouteOrder)scope.ServiceProvider.GetRequiredService<IRouteOrder>();

        var run = () => train.Run(new Order("down", 20m));

        (await run.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Be("the database is down");
        decider.Requests.Should().ContainSingle();
        Ship.Ran.Should().Be(0, "a decision that was not recorded is not acted on");
        train.Metadata!.FailureClass.Should().Be(FailureClass.Transient);
    }

    [Test]
    public async Task A_replay_on_a_host_that_records_no_decisions_fails_the_run_as_permanent()
    {
        await using var provider = DecisionTrains
            .Register(new ServiceCollection(), Decider)
            .AddTrax(trax => trax.AddEffects(effects => effects.UseInMemory()))
            .BuildServiceProvider();
        var decider = Decider.Use(new ScriptedDecider().Choose(Fulfilment.Standard));

        using var scope = provider.CreateScope();
        var train = (RouteOrder)scope.ServiceProvider.GetRequiredService<IRouteOrder>();
        var metadata = Metadata.Create(
            new CreateMetadata
            {
                Name = typeof(IRouteOrder).FullName!,
                ExternalId = Guid.NewGuid().ToString("N"),
                Input = new Order("unconfigured", 20m),
                ReplayDecisionsOf = 42,
            }
        );

        var run = () => train.Run(new Order("unconfigured", 20m), metadata);

        (await run.Should().ThrowAsync<TrainException>())
            .Which.Message.Should()
            .Contain("does not record decisions (AddDecisionRecording)");
        decider.Requests.Should().BeEmpty();
        train.Metadata!.TrainState.Should().Be(TrainState.Failed);
        train.Metadata.FailureClass.Should().Be(FailureClass.Permanent);
    }

    /// <summary>A store that cannot be reached.</summary>
    private sealed class DownFactory : IDataContextProviderFactory
    {
        public Task<IDataContext> CreateDbContextAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the database is down");

        public IEffectProvider Create() =>
            throw new InvalidOperationException("the database is down");
    }
}

/// <summary>The trains the decision recording tests run, and their registrations.</summary>
internal static class DecisionTrains
{
    public static IServiceCollection Register(IServiceCollection services, IDecider decider) =>
        services
            .AddSingleton(decider)
            .AddSingleton<NonFiniteShadow>()
            .AddScopedTraxRoute<IRouteOrder, RouteOrder>()
            .AddScopedTraxRoute<IRouteThenFail, RouteThenFail>()
            .AddScopedTraxRoute<IRouteThenPeek, RouteThenPeek>()
            .AddScopedTraxRoute<IRouteTwice, RouteTwice>()
            .AddScopedTraxRoute<IRouteWithShadow, RouteWithShadow>()
            .AddScopedTraxRoute<IMeetThenRoute, MeetThenRoute>();
}

/// <summary>The registered decider, swapped per test.</summary>
internal sealed class DeciderSlot : IDecider
{
    private IDecider _inner = new ScriptedDecider();

    public TDecider Use<TDecider>(TDecider inner)
        where TDecider : IDecider
    {
        _inner = inner;
        return inner;
    }

    public Task<DecisionResult> Decide(DecisionRequest request, CancellationToken ct) =>
        _inner.Decide(request, ct);
}

/// <summary>Answers each asking of the fulfilment question with the next choice in turn.</summary>
internal sealed class SequenceDecider(params Fulfilment[] choices) : IDecider
{
    private int _asked;

    public int Asked => _asked;

    public Task<DecisionResult> Decide(DecisionRequest request, CancellationToken ct)
    {
        var choice = choices[Interlocked.Increment(ref _asked) - 1];

        return Task.FromResult(
            new DecisionResult(
                new Dictionary<string, Answer>
                {
                    [QuestionKey.For<Fulfilment>()] = new ChoiceAnswer(choice.ToString()),
                }
            )
        );
    }
}

/// <summary>A shadow whose answer carries numbers JSON has no form for.</summary>
public sealed class NonFiniteShadow : IDecider
{
    public Task<DecisionResult> Decide(DecisionRequest request, CancellationToken ct) =>
        Task.FromResult(
            new DecisionResult(
                new Dictionary<string, Answer>
                {
                    [QuestionKey.For<Fulfilment>()] = new ChoiceAnswer(
                        "Standard",
                        double.NaN,
                        new Dictionary<string, double> { ["Standard"] = double.PositiveInfinity }
                    ),
                }
            )
        );
}

public sealed record Order(string Id, decimal Total);

[Asks("How should this order be fulfilled?")]
public enum Fulfilment
{
    Standard,
    ManualCheck,
}

public interface IRouteOrder : IServiceTrain<Order, string>;

public class RouteOrder : ServiceTrain<Order, string>, IRouteOrder
{
    protected override Task<Either<Exception, string>> Junctions() =>
        Switch<Order, Fulfilment>(tracks =>
                tracks
                    .When(Fulfilment.Standard, t => t.Chain<Ship>())
                    .When(Fulfilment.ManualCheck, t => t.Chain<HoldForReview>())
                    .RequireConfidence(0.8)
                    .Otherwise(t => t.Chain<HoldForReview>())
            )
            .Resolve();
}

public interface IRouteWithShadow : IServiceTrain<Order, string>;

public class RouteWithShadow : ServiceTrain<Order, string>, IRouteWithShadow
{
    protected override Task<Either<Exception, string>> Junctions() =>
        Switch<Order, Fulfilment>(tracks =>
                tracks
                    .When(Fulfilment.Standard, t => t.Chain<Ship>())
                    .When(Fulfilment.ManualCheck, t => t.Chain<HoldForReview>())
                    .Shadow<NonFiniteShadow>()
            )
            .Resolve();
}

public interface IRouteTwice : IServiceTrain<Order, string>;

/// <summary>Asks the same question twice, and the second answer decides the output.</summary>
public class RouteTwice : ServiceTrain<Order, string>, IRouteTwice
{
    protected override Task<Either<Exception, string>> Junctions() =>
        Switch<Order, Fulfilment>(tracks =>
                tracks
                    .When(Fulfilment.Standard, t => t.Chain<Ship>())
                    .When(Fulfilment.ManualCheck, t => t.Chain<HoldForReview>())
            )
            .Switch<Order, Fulfilment>(tracks =>
                tracks
                    .When(Fulfilment.Standard, t => t.Chain<Ship>())
                    .When(Fulfilment.ManualCheck, t => t.Chain<HoldForReview>())
            )
            .Resolve();
}

public interface IRouteThenFail : IServiceTrain<Order, string>;

public class RouteThenFail : ServiceTrain<Order, string>, IRouteThenFail
{
    protected override Task<Either<Exception, string>> Junctions() =>
        Switch<Order, Fulfilment>(tracks =>
                tracks
                    .When(Fulfilment.Standard, t => t.Chain<Ship>())
                    .When(Fulfilment.ManualCheck, t => t.Chain<HoldForReview>())
            )
            .Chain<FailAfterRouting>()
            .Resolve();
}

public interface IRouteThenPeek : IServiceTrain<Order, string>;

public class RouteThenPeek : ServiceTrain<Order, string>, IRouteThenPeek
{
    protected override Task<Either<Exception, string>> Junctions() =>
        Switch<Order, Fulfilment>(tracks =>
                tracks
                    .When(Fulfilment.Standard, t => t.Chain<Ship>())
                    .When(Fulfilment.ManualCheck, t => t.Chain<HoldForReview>())
            )
            .Chain<PeekDecisions>()
            .Resolve();
}

public interface IMeetThenRoute : IServiceTrain<Order, string>;

/// <summary>Waits for the other runs it is expected with, so they decide while all are running.</summary>
public class MeetThenRoute : ServiceTrain<Order, string>, IMeetThenRoute
{
    protected override Task<Either<Exception, string>> Junctions() =>
        Chain<MeetBeforeDeciding>()
            .Switch<Order, Fulfilment>(tracks =>
                tracks
                    .When(Fulfilment.Standard, t => t.Chain<Ship>())
                    .When(Fulfilment.ManualCheck, t => t.Chain<HoldForReview>())
            )
            .Resolve();
}

public class Ship : Junction<Order, string>
{
    public static int Ran;

    public override Task<string> Run(Order input)
    {
        Interlocked.Increment(ref Ran);
        return Task.FromResult("shipped");
    }
}

public class HoldForReview : Junction<Order, string>
{
    public override Task<string> Run(Order input) => Task.FromResult("held for review");
}

public class FailAfterRouting : Junction<string, string>
{
    public override Task<string> Run(string input) =>
        throw new InvalidOperationException("the warehouse is offline");
}

public class MeetBeforeDeciding : Junction<Order, Order>
{
    private static int _expected;
    private static int _arrived;
    private static TaskCompletionSource _all = new();

    public static void Expect(int runs)
    {
        _expected = runs;
        _arrived = 0;
        _all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public override async Task<Order> Run(Order input)
    {
        var all = _all;

        if (Interlocked.Increment(ref _arrived) == _expected)
            all.TrySetResult();

        await all.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return input;
    }
}

/// <summary>Reads the run's decisions through a context of its own, as another process would.</summary>
public class PeekDecisions(IDataContextProviderFactory factory) : EffectJunction<string, string>
{
    public static List<(long MetadataId, TrainState State, string? Track)> Seen { get; } = [];

    public override async Task<string> Run(string input)
    {
        Seen.Clear();
        var id = Metadata!.TrainMetadataId;
        using var context = await factory.CreateDbContextAsync(CancellationToken.None);

        var state = await context
            .Metadatas.AsNoTracking()
            .Where(m => m.Id == id)
            .Select(m => m.TrainState)
            .SingleAsync();

        foreach (
            var decision in await context
                .RecordedDecisions.AsNoTracking()
                .Where(d => d.MetadataId == id)
                .ToListAsync()
        )
            Seen.Add((decision.MetadataId, state, decision.Track));

        return input;
    }
}
