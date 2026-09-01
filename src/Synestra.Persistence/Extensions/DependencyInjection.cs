using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synestra.Application.Jobs;
using Synestra.Persistence.Jobs;

namespace Synestra.Persistence.Extensions;

public static class DependencyInjection
{
    public static IServiceCollection AddPersistence(this IServiceCollection s, string connectionString)
    {
        s.AddNpgsql<SynestraDbContext>(connectionString);
        s.AddScoped<ISubmitJobPersistence, SubmitJobPersistence>();
        return s;
    }
}
