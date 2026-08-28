namespace Synestra.Domain.Jobs;

public sealed class JobDefinition
{
    private JobDefinition()
    {
    }

    public JobDefinition(
        string type,
        string displayName,
        string? description,
        bool isEnabled,
        DateTime createdAtUtc)
    {
        Id = Guid.CreateVersion7();
        Type = DomainValidation.RequiredText(type, 100, nameof(type));
        DisplayName = DomainValidation.RequiredText(displayName, 200, nameof(displayName));
        Description = DomainValidation.OptionalText(description, 1000, nameof(description));
        IsEnabled = isEnabled;
        CreatedAtUtc = DomainValidation.Utc(createdAtUtc, nameof(createdAtUtc));
        UpdatedAtUtc = CreatedAtUtc;
    }

    public Guid Id { get; private set; }
    public string Type { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public string? Description { get; private set; }
    public bool IsEnabled { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
}
