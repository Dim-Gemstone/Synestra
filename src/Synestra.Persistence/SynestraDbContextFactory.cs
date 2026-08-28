using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Synestra.Persistence;

public sealed class SynestraDbContextFactory : IDesignTimeDbContextFactory<SynestraDbContext>
{
    public SynestraDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SynestraDbContext>()
            .UseNpgsql("Host=localhost;Database=synestra")
            .Options;

        return new SynestraDbContext(options);
    }
}
