using Microsoft.Extensions.DependencyInjection;
using Synestra.Application.Jobs;

namespace Synestra.Application.Extensions;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<GetJob>();
        services.AddScoped<SubmitJob>();
        return services;
    }
}
