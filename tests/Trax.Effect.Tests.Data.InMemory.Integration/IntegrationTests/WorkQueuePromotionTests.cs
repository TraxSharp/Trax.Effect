using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trax.Effect.Data.Services.IDataContextFactory;
using Trax.Effect.Data.Services.WorkQueuePromotion;
using Trax.Effect.Enums;
using Trax.Effect.Models.WorkQueue;
using Trax.Effect.Models.WorkQueue.DTOs;
using Trax.Effect.Tests.Data.InMemory.Integration.Fixtures;

namespace Trax.Effect.Tests.Data.InMemory.Integration.IntegrationTests;

/// <summary>
/// The second phase of a two-phase enqueue, and the sweep that resolves entries a crash left
/// unconfirmed, on the in-memory provider. That provider cannot translate ExecuteUpdate, and a
/// deferring train that threw after its hook had already run would be the result.
/// </summary>
public class WorkQueuePromotionTests : TestSetup
{
    public override ServiceProvider ConfigureServices(IServiceCollection services) =>
        services.AddScoped<IWorkQueuePromotion, WorkQueuePromotion>().BuildServiceProvider();

    private IWorkQueuePromotion Promotion =>
        Scope.ServiceProvider.GetRequiredService<IWorkQueuePromotion>();

    [Test]
    public async Task Promoting_a_staged_entry_confirms_it()
    {
        var id = await Stage();

        var promoted = await Promotion.PromoteAsync(id, CancellationToken.None);

        promoted.Should().BeTrue();
        (await Load(id)).ConfirmedAt.Should().NotBeNull();
    }

    [Test]
    public async Task Promoting_an_entry_cancelled_while_its_hook_ran_leaves_it_cancelled()
    {
        var id = await Stage(status: WorkQueueStatus.Cancelled);

        var promoted = await Promotion.PromoteAsync(id, CancellationToken.None);

        promoted.Should().BeFalse("an operator cancelled it, and confirming it would revive it");
        (await Load(id)).ConfirmedAt.Should().BeNull();
    }

    [Test]
    public async Task The_stale_sweep_cancels_old_staged_entries_and_leaves_recent_ones()
    {
        var stale = await Stage(age: TimeSpan.FromHours(1));
        var recent = await Stage();

        var cancelled = await Promotion.CancelStaleAsync(
            TimeSpan.FromMinutes(10),
            CancellationToken.None
        );

        cancelled.Should().BeGreaterThanOrEqualTo(1);
        (await Load(stale)).Status.Should().Be(WorkQueueStatus.Cancelled);
        (await Load(recent))
            .Status.Should()
            .Be(WorkQueueStatus.Queued, "an entry younger than the window may still be mid-hook");
    }

    [Test]
    public async Task The_opt_in_stale_sweep_promotes_old_staged_entries()
    {
        var stale = await Stage(age: TimeSpan.FromHours(1));

        await Promotion.PromoteStaleAsync(TimeSpan.FromMinutes(10), CancellationToken.None);

        var entry = await Load(stale);
        entry.Status.Should().Be(WorkQueueStatus.Queued);
        entry.ConfirmedAt.Should().NotBeNull();
    }

    private async Task<long> Stage(
        TimeSpan? age = null,
        WorkQueueStatus status = WorkQueueStatus.Queued
    )
    {
        var entry = WorkQueue.Create(
            new CreateWorkQueue
            {
                TrainName = "Staged.Train",
                InputTypeName = "Staged.Input",
                DeferPromotion = true,
            }
        );
        entry.CreatedAt = DateTime.UtcNow - (age ?? TimeSpan.Zero);
        entry.Status = status;

        using var context = await Factory.CreateDbContextAsync(CancellationToken.None);
        await context.Track(entry);
        await context.SaveChanges(CancellationToken.None);

        return entry.Id;
    }

    private async Task<WorkQueue> Load(long id)
    {
        using var context = await Factory.CreateDbContextAsync(CancellationToken.None);
        return await context.WorkQueues.AsNoTracking().SingleAsync(w => w.Id == id);
    }

    private IDataContextProviderFactory Factory =>
        Scope.ServiceProvider.GetRequiredService<IDataContextProviderFactory>();
}
