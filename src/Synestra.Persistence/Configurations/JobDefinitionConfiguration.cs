using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Synestra.Domain.Jobs;

namespace Synestra.Persistence.Configurations;

internal sealed class JobDefinitionConfiguration : IEntityTypeConfiguration<JobDefinition>
{
    public void Configure(EntityTypeBuilder<JobDefinition> builder)
    {
        builder.ToTable("job_definitions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Type).HasColumnName("type").HasMaxLength(100).IsRequired();
        builder.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(1000);
        builder.Property(x => x.IsEnabled).HasColumnName("is_enabled").IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();
        builder.HasIndex(x => x.Type).IsUnique();
    }
}
