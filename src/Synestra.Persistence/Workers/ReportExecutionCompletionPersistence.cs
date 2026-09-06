using System.Data;
using Microsoft.EntityFrameworkCore;
using Synestra.Application.Workers;

namespace Synestra.Persistence.Workers;

internal sealed class ReportExecutionCompletionPersistence(SynestraDbContext dbContext) : IReportExecutionCompletionPersistence
{
    public async Task<IReportExecutionCompletionTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new ExecutionLeaseTransaction(dbContext,
            await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken));
}
