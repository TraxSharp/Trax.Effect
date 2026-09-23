using Microsoft.EntityFrameworkCore;
using Trax.Effect.Data.Services.IDataContextFactory;

namespace Trax.Effect.Data.Services.WorkQueuePromotion;

/// <inheritdoc />
public class WorkQueuePromotion(IDataContextProviderFactory contextFactory) : IWorkQueuePromotion
{
    /// <inheritdoc />
    public async Task<bool> PromoteAsync(long workQueueId, CancellationToken cancellationToken)
    {
        using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var promoted = await context
            .WorkQueues.Where(w => w.Id == workQueueId && w.ConfirmedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(w => w.ConfirmedAt, DateTime.UtcNow),
                cancellationToken
            );

        return promoted > 0;
    }

    /// <inheritdoc />
    public async Task<int> PromoteStaleAsync(
        TimeSpan olderThan,
        CancellationToken cancellationToken
    )
    {
        using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var cutoff = DateTime.UtcNow - olderThan;

        return await context
            .WorkQueues.Where(w => w.ConfirmedAt == null && w.CreatedAt < cutoff)
            .ExecuteUpdateAsync(
                s => s.SetProperty(w => w.ConfirmedAt, DateTime.UtcNow),
                cancellationToken
            );
    }
}
