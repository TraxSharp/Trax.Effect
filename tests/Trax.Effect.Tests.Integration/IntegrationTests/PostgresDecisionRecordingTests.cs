using FluentAssertions;
using LanguageExt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trax.Core.Decisions;
using Trax.Core.Exceptions;
using Trax.Core.Junction;
using Trax.Effect.Data.Extensions;
using Trax.Effect.Data.Postgres.Extensions;
using Trax.Effect.Data.Services.IDataContextFactory;
using Trax.Effect.Enums;
using Trax.Effect.Extensions;
using Trax.Effect.Models.Metadata;
using Trax.Effect.Models.Metadata.DTOs;
using Trax.Effect.Services.EffectJunction;
using Trax.Effect.Services.ServiceTrain;
using Trax.Effect.Tests.Integration.Fixtures;

namespace Trax.Effect.Tests.Integration.IntegrationTests;

/// <summary>
/// Decision recording against Postgres: each decision is a <c>trax.decision</c> row written while
/// the run is still going, deleted with its run, and read back by a requeued run that names it in
/// <c>replay_decisions_of</c>.
/// </summary>
[TestFixture]
[NonParallelizable]
public class PostgresDecisionRecordingTests
{
    private ServiceProvider _provider = null!;

    private static readonly PgDecider Decider = new();

    [OneTimeSetUp]
    public void BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        var connectionString = TestPostgres.WithPort(
            configuration.GetRequiredSection("Configuration")["DatabaseConnectionString"]!
        );

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDecider>(Decider);
        services.AddTrax(trax =>
            trax.AddEffects(effects => effects.UsePostgres(connectionString).AddDecisionRecording())
        );
        services
            .AddScopedTraxRoute<IPgRouteOrder, PgRouteOrder>()
            .AddScopedTraxRoute<IPgRouteThenPeek, PgRouteThenPeek>();
        _provider = services.BuildServiceProvider();
    }

    [OneTimeTearDown]
    public async Task DisposeProvider() => await _provider.DisposeAsync();

    [Test]
    public async Task A_decision_is_a_row_written_while_the_run_is_in_progress()
    {
        Decider.Choice = PgFulfilment.ManualCheck;

        var (train, output) = await Run<IPgRouteThenPeek>();

        output.Should().Be("held");
        PgPeek.Seen.Should().Equal((train.Metadata!.Id, TrainState.InProgress, "ManualCheck"));

        var row = (await Recorded(train.Metadata.Id)).Should().ContainSingle().Subject;
        row.QuestionKey.Should().Be(QuestionKey.For<PgFulfilment>());
        row.Occurrence.Should().Be(0);
        row.Fingerprint.Should().MatchRegex("^[0-9a-f]{64}$");
        row.Kind.Should().Be("choice");
        row.Answer.Should().Contain("\"choice\": \"ManualCheck\"", "jsonb normalises the spacing");
        row.Replayed.Should().BeFalse();
        row.Track.Should().Be("ManualCheck");

        await Delete(train.Metadata.Id);
    }

    [Test]
    public async Task A_runs_decisions_are_deleted_with_it()
    {
        Decider.Choice = PgFulfilment.Standard;
        var (train, _) = await Run<IPgRouteOrder>();
        (await Recorded(train.Metadata!.Id)).Should().ContainSingle();

        await Delete(train.Metadata.Id);

        (await Recorded(train.Metadata.Id)).Should().BeEmpty("the foreign key cascades");
    }

    [Test]
    public async Task A_requeued_run_replays_the_decisions_of_the_run_it_names()
    {
        Decider.Choice = PgFulfilment.ManualCheck;
        var (original, first) = await Run<IPgRouteOrder>();
        first.Should().Be("held");

        Decider.Choice = PgFulfilment.Standard;
        Decider.Asked = 0;
        var (requeued, output) = await Run<IPgRouteOrder>(replayDecisionsOf: original.Metadata!.Id);

        output.Should().Be("held");
        Decider.Asked.Should().Be(0);
        var replayed = (await Recorded(requeued.Metadata!.Id)).Should().ContainSingle().Subject;
        replayed.Replayed.Should().BeTrue();
        replayed.Track.Should().Be("ManualCheck");

        await Delete(original.Metadata.Id, requeued.Metadata.Id);
    }

    [Test]
    public async Task A_requeue_of_a_deleted_run_fails_as_permanent_without_asking()
    {
        Decider.Choice = PgFulfilment.ManualCheck;
        var (original, _) = await Run<IPgRouteOrder>();
        await Delete(original.Metadata!.Id);
        Decider.Asked = 0;

        var run = async () => await Run<IPgRouteOrder>(replayDecisionsOf: original.Metadata.Id);

        (await run.Should().ThrowAsync<TrainException>())
            .Which.Message.Should()
            .Contain($"no run {original.Metadata.Id} exists");
        Decider.Asked.Should().Be(0);
        _lastTrain!.Metadata!.FailureClass.Should().Be(FailureClass.Permanent);

        await Delete(_lastTrain.Metadata.Id);
    }

    private IServiceTrain<PgOrder, string>? _lastTrain;

    private async Task<(IServiceTrain<PgOrder, string> Train, string Output)> Run<TTrain>(
        long? replayDecisionsOf = null
    )
        where TTrain : class, IServiceTrain<PgOrder, string>
    {
        using var scope = _provider.CreateScope();
        var train =
            (ServiceTrain<PgOrder, string>)
                (object)scope.ServiceProvider.GetRequiredService<TTrain>();
        _lastTrain = train;
        var order = new PgOrder("pg");

        var metadata = Metadata.Create(
            new CreateMetadata
            {
                Name = typeof(TTrain).FullName!,
                ExternalId = Guid.NewGuid().ToString("N"),
                Input = order,
                ReplayDecisionsOf = replayDecisionsOf,
            }
        );

        return (train, await train.Run(order, metadata));
    }

    private async Task<List<Models.RecordedDecision.RecordedDecision>> Recorded(long metadataId)
    {
        var factory = _provider.GetRequiredService<IDataContextProviderFactory>();
        using var context = await factory.CreateDbContextAsync(CancellationToken.None);

        return await context
            .RecordedDecisions.AsNoTracking()
            .Where(d => d.MetadataId == metadataId)
            .ToListAsync();
    }

    private async Task Delete(params long[] metadataIds)
    {
        var factory = _provider.GetRequiredService<IDataContextProviderFactory>();
        using var context = await factory.CreateDbContextAsync(CancellationToken.None);

        await context.Metadatas.Where(m => metadataIds.Contains(m.Id)).ExecuteDeleteAsync();
    }

    public sealed class PgDecider : IDecider
    {
        public PgFulfilment Choice { get; set; }

        public int Asked { get; set; }

        public Task<DecisionResult> Decide(DecisionRequest request, CancellationToken ct)
        {
            Asked++;

            return Task.FromResult(
                new DecisionResult(
                    new Dictionary<string, Answer>
                    {
                        [QuestionKey.For<PgFulfilment>()] = new ChoiceAnswer(Choice.ToString()),
                    }
                )
            );
        }
    }

    public sealed record PgOrder(string Id);

    [Asks("How should this order be fulfilled?")]
    public enum PgFulfilment
    {
        Standard,
        ManualCheck,
    }

    public interface IPgRouteOrder : IServiceTrain<PgOrder, string>;

    public class PgRouteOrder : ServiceTrain<PgOrder, string>, IPgRouteOrder
    {
        protected override Task<Either<Exception, string>> Junctions() =>
            Switch<PgOrder, PgFulfilment>(tracks =>
                    tracks
                        .When(PgFulfilment.Standard, t => t.Chain<PgShip>())
                        .When(PgFulfilment.ManualCheck, t => t.Chain<PgHold>())
                )
                .Resolve();
    }

    public interface IPgRouteThenPeek : IServiceTrain<PgOrder, string>;

    public class PgRouteThenPeek : ServiceTrain<PgOrder, string>, IPgRouteThenPeek
    {
        protected override Task<Either<Exception, string>> Junctions() =>
            Switch<PgOrder, PgFulfilment>(tracks =>
                    tracks
                        .When(PgFulfilment.Standard, t => t.Chain<PgShip>())
                        .When(PgFulfilment.ManualCheck, t => t.Chain<PgHold>())
                )
                .Chain<PgPeek>()
                .Resolve();
    }

    public class PgShip : Junction<PgOrder, string>
    {
        public override Task<string> Run(PgOrder input) => Task.FromResult("shipped");
    }

    public class PgHold : Junction<PgOrder, string>
    {
        public override Task<string> Run(PgOrder input) => Task.FromResult("held");
    }

    /// <summary>Reads the run's decisions on a connection of its own, as another process would.</summary>
    public class PgPeek(IDataContextProviderFactory factory) : EffectJunction<string, string>
    {
        public static List<(long, TrainState, string?)> Seen { get; } = [];

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
                var row in await context
                    .RecordedDecisions.AsNoTracking()
                    .Where(d => d.MetadataId == id)
                    .ToListAsync()
            )
                Seen.Add((row.MetadataId, state, row.Track));

            return input;
        }
    }
}
