namespace Synestra.Domain.Workers;

public sealed class Worker
{
    private Worker()
    {
    }

    public Worker(string name, int capacity, DateTime registeredAtUtc)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
        }

        Id = Guid.CreateVersion7();
        Name = DomainValidation.RequiredText(name, 200, nameof(name));
        Capacity = capacity;
        RegisteredAtUtc = DomainValidation.Utc(registeredAtUtc, nameof(registeredAtUtc));
        LastSeenAtUtc = RegisteredAtUtc;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public int Capacity { get; private set; }
    public DateTime RegisteredAtUtc { get; private set; }
    public DateTime LastSeenAtUtc { get; private set; }
}
