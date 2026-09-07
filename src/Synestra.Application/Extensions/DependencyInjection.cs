using Microsoft.Extensions.DependencyInjection;
using Synestra.Application.Executions;
using Synestra.Application.Jobs;
using Synestra.Application.Workers;

namespace Synestra.Application.Extensions;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<GetJob>();
        services.AddScoped<SubmitJob>();
        services.AddSingleton<WorkerLivenessOptions>();
        services.AddScoped<RegisterWorker>();
        services.AddScoped<RecordWorkerHeartbeat>();
        services.AddSingleton<ClaimWorkOptions>();
        services.AddScoped<ClaimWork>();
        services.AddScoped<RenewLease>();
        services.AddScoped<ReportExecutionCompletion>();
        services.AddScoped<FinalizeExpiredExecution>();
        services.AddScoped<FinalizeExpiredExecutionSweep>();
        return services;
    }
}
