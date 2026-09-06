using System.Data;
using Microsoft.EntityFrameworkCore;
using Synestra.Application.Workers;

namespace Synestra.Persistence.Workers;

internal sealed class RenewLeasePersistence(SynestraDbContext dbContext) : IRenewLeasePersistence
{
    public async Task<IRenewLeaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new ExecutionLeaseTransaction(dbContext,
            await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken));
}
