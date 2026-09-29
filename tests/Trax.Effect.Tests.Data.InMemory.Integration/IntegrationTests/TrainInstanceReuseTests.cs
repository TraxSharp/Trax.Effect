using FluentAssertions;
using LanguageExt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trax.Effect.Data.Services.DataContext;
using Trax.Effect.Data.Services.IDataContextFactory;
using Trax.Effect.Enums;
using Trax.Effect.Extensions;
using Trax.Effect.Services.ServiceTrain;
using Trax.Effect.Tests.Data.InMemory.Integration.Fixtures;

namespace Trax.Effect.Tests.Data.InMemory.Integration.IntegrationTests;

/// <summary>
/// A train resolved once and run twice, which a scoped registration hands back within one scope
/// and a singleton registration hands back for the life of the process, records each run.
/// </summary>
public class TrainInstanceReuseTests : TestSetup
{
    public override ServiceProvider ConfigureServices(IServiceCollection services) =>
        services
            .AddScopedTraxRoute<IToggleTrain, ToggleTrain>()
            .AddSingletonTraxRoute<ISingletonToggleTrain, SingletonToggleTrain>()
            .BuildServiceProvider();

    [Test]
    public async Task A_scoped_train_run_twice_in_one_scope_records_two_runs()
    {
        var train = Scope.ServiceProvider.GetRequiredService<IToggleTrain>();

        var failing = async () => await train.Run(true);
        await failing.Should().ThrowAsync<InvalidOperationException>();
        await train.Run(false);

        var rows = await Rows(typeof(IToggleTrain).FullName!);

        rows.Should()
            .BeEquivalentTo(
                [TrainState.Failed, TrainState.Completed],
                "each run is its own execution; the second must not overwrite the first's record"
            );
    }

    [Test]
    public async Task A_singleton_train_records_every_run()
    {
        var first = Scope.ServiceProvider.GetRequiredService<ISingletonToggleTrain>();
        await first.Run(false);

        using var otherScope = Scope.ServiceProvider.CreateScope();
        var second = otherScope.ServiceProvider.GetRequiredService<ISingletonToggleTrain>();
        await second.Run(false);

        var rows = await Rows(typeof(ISingletonToggleTrain).FullName!);

        rows.Should().HaveCount(2, "two runs happened, from two different scopes");
    }

    private async Task<List<TrainState>> Rows(string name)
    {
        var factory = Scope.ServiceProvider.GetRequiredService<IDataContextProviderFactory>();
        using var context = (IDataContext)factory.Create();

        return await context
            .Metadatas.AsNoTracking()
            .Where(m => m.Name == name)
            .OrderBy(m => m.Id)
            .Select(m => m.TrainState)
            .ToListAsync();
    }

    private class ToggleTrain : ServiceTrain<bool, Unit>, IToggleTrain
    {
        protected override Task<Either<Exception, Unit>> Junctions() =>
            Task.FromResult<Either<Exception, Unit>>(
                TrainInput ? new InvalidOperationException("asked to fail") : Unit.Default
            );
    }

    private class SingletonToggleTrain : ToggleTrain, ISingletonToggleTrain { }

    public interface IToggleTrain : IServiceTrain<bool, Unit> { }

    public interface ISingletonToggleTrain : IServiceTrain<bool, Unit> { }
}
