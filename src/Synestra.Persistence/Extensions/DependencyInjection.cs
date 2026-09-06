using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synestra.Application.Executions;
using Synestra.Application.Jobs;
using Synestra.Application.Workers;
using Synestra.Persistence.Executions;
using Synestra.Persistence.Jobs;
using Synestra.Persistence.Workers;

namespace Synestra.Persistence.Extensions;

public static class DependencyInjection
{
    public static IServiceCollection AddPersistence(this IServiceCollection s, string connectionString)
    {
        s.AddNpgsql<SynestraDbContext>(connectionString);
        s.AddScoped<IGetJobPersistence, GetJobPersistence>();
        s.AddScoped<ISubmitJobPersistence, SubmitJobPersistence>();
        s.AddScoped<IWorkerPersistence, WorkerPersistence>();
        s.AddScoped<IClaimWorkPersistence, ClaimWorkPersistence>();
        s.AddScoped<IRenewLeasePersistence, RenewLeasePersistence>();
        s.AddScoped<IReportExecutionCompletionPersistence, ReportExecutionCompletionPersistence>();
        s.AddScoped<IFinalizeExpiredExecutionPersistence, FinalizeExpiredExecutionPersistence>();
        return s;
    }
}
