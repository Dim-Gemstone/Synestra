using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Synestra.Persistence.Extensions;

public static class DependencyInjection
{
    public static IServiceCollection AddPersistence(this IServiceCollection s, string connectionString)
    {
        return s.AddNpgsql<SynestraDbContext>(connectionString);
    }
}
