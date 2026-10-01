using FluentAssertions;
using LanguageExt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trax.Core.Decisions;
using Trax.Core.Junction;
using Trax.Effect.Data.Extensions;
using Trax.Effect.Data.InMemory.Extensions;
using Trax.Effect.Data.Services.DataContext;
using Trax.Effect.Extensions;
using Trax.Effect.Models.Metadata;
using Trax.Effect.Models.Metadata.DTOs;
using Trax.Effect.Services.ServiceTrain;

namespace Trax.Effect.Tests.Data.InMemory.Integration.IntegrationTests;

/// <summary>
/// With <c>AddDecisionRecording</c>, every decision a train makes is written against its run, and
/// a run that names an earlier one in <c>ReplayDecisionsOf</c> takes the tracks that run took
/// instead of asking again.
/// </summary>
public class DecisionRecordingTests
{
    private ServiceProvider _provider = null!;

    private static readonly DeciderSlot Decider = new();

    [OneTimeSetUp]
    public void Build() =>
        _provider = new ServiceCollection()
            .AddTrax(trax =>
                trax.AddEffects(effects => effects.UseInMemory().AddDecisionRecording())
            )
            .AddSingleton<IDecider>(Decider)
            .AddScopedTraxRoute<IRouteOrder, RouteOrder>()
            .AddScopedTraxRoute<IRouteThenFail, RouteThenFail>()
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
        decision.QuestionKey.Should().Be(nameof(Fulfilment));
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
    public async Task A_replay_of_a_run_that_recorded_nothing_asks_afresh()
    {
        var decider = Decider.Use(new ScriptedDecider().Choose(Fulfilment.Standard));

        var (_, output) = await Run<IRouteOrder>(
            new Order("o5", 20m),
            replayDecisionsOf: 987_654_321
        );

        output.Should().Be("shipped");
        decider.Requests.Should().ContainSingle();
    }

    [Test]
    public async Task A_run_started_from_a_row_it_was_given_takes_the_rows_external_id()
    {
        // The scheduler creates the row before dispatch. Without adopting its id, the run's
        // failure data and decisions named an id that matched no row.
        Decider.Use(new ScriptedDecider().Choose(Fulfilment.Standard));

        var (train, _) = await Run<IRouteOrder>(new Order("o6", 20m), replayDecisionsOf: 1);

        ((ServiceTrain<Order, string>)(object)train)
            .ExternalId.Should()
            .Be(train.Metadata!.ExternalId);
    }

    private async Task<(TTrain Train, string Output)> Run<TTrain>(
        Order order,
        long? replayDecisionsOf = null
    )
        where TTrain : class, IServiceTrain<Order, string>
    {
        using var scope = _provider.CreateScope();
        var train =
            (ServiceTrain<Order, string>)(object)scope.ServiceProvider.GetRequiredService<TTrain>();

        if (replayDecisionsOf is null)
            return ((TTrain)(object)train, await train.Run(order));

        var metadata = Metadata.Create(
            new CreateMetadata
            {
                Name = typeof(TTrain).FullName!,
                ExternalId = Guid.NewGuid().ToString("N"),
                Input = order,
                ReplayDecisionsOf = replayDecisionsOf,
            }
        );

        return ((TTrain)(object)train, await train.Run(order, metadata));
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

    /// <summary>The registered decider, swapped per test.</summary>
    private sealed class DeciderSlot : IDecider
    {
        private ScriptedDecider _inner = new();

        public ScriptedDecider Use(ScriptedDecider inner) => _inner = inner;

        public Task<DecisionResult> Decide(DecisionRequest request, CancellationToken ct) =>
            _inner.Decide(request, ct);
    }
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

public class Ship : Junction<Order, string>
{
    public override Task<string> Run(Order input) => Task.FromResult("shipped");
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
