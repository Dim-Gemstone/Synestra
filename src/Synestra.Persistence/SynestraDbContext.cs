using Microsoft.EntityFrameworkCore;
using Synestra.Domain.Jobs;
using Synestra.Domain.Leases;
using Synestra.Domain.Workers;

namespace Synestra.Persistence;

public sealed class SynestraDbContext(DbContextOptions<SynestraDbContext> options) : DbContext(options)
{
    public DbSet<JobDefinition> JobDefinitions => Set<JobDefinition>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobAttempt> JobAttempts => Set<JobAttempt>();
    public DbSet<Lease> Leases => Set<Lease>();
    public DbSet<Worker> Workers => Set<Worker>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SynestraDbContext).Assembly);
    }
}
