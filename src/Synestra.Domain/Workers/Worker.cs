namespace Synestra.Domain.Workers;

public sealed class Worker
{
    private readonly List<WorkerSupportedType> _supportedTypes = [];

    private Worker()
    {
    }

    public Worker(WorkerRegistration registration, DateTime registeredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(registration);
        Id = registration.WorkerId;
        RegisteredAtUtc = DomainValidation.Utc(registeredAtUtc, nameof(registeredAtUtc));
        LastSeenAtUtc = RegisteredAtUtc;
        UpdateRegistration(registration, registeredAtUtc);
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public int Capacity { get; private set; }
    public Guid? SessionId { get; private set; }
    public DateTime? SessionStartedAtUtc { get; private set; }
    public DateTime RegisteredAtUtc { get; private set; }
    public DateTime LastSeenAtUtc { get; private set; }
    public IReadOnlyCollection<WorkerSupportedType> SupportedTypes => _supportedTypes.AsReadOnly();

    public void UpdateRegistration(WorkerRegistration registration, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(registration);
        DomainValidation.Utc(nowUtc, nameof(nowUtc));
        if (registration.WorkerId != Id)
        {
            throw new ArgumentException("Registration must identify this Worker.", nameof(registration));
        }

        if (SessionId != registration.SessionId)
        {
            SessionId = registration.SessionId;
            SessionStartedAtUtc = nowUtc;
        }

        Name = registration.Name;
        Capacity = registration.Capacity;
        _supportedTypes.RemoveAll(item => !registration.SupportedTypes.Contains(item.Type, StringComparer.Ordinal));
        foreach (var type in registration.SupportedTypes)
        {
            if (!_supportedTypes.Any(item => StringComparer.Ordinal.Equals(item.Type, type)))
            {
                _supportedTypes.Add(new WorkerSupportedType(Id, type));
            }
        }

        RecordHeartbeat(registration.SessionId, nowUtc);
    }

    public bool RecordHeartbeat(Guid sessionId, DateTime nowUtc)
    {
        WorkerRegistration.ValidateIdentity(sessionId, nameof(sessionId));
        DomainValidation.Utc(nowUtc, nameof(nowUtc));
        if (SessionId != sessionId)
        {
            return false;
        }

        if (nowUtc > LastSeenAtUtc)
        {
            LastSeenAtUtc = nowUtc;
        }

        return true;
    }
}
