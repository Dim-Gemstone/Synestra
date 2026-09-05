namespace Synestra.Domain.Workers;

public sealed class WorkerRegistration
{
    public WorkerRegistration(Guid workerId, Guid sessionId, string name, int capacity, IReadOnlyCollection<string> supportedTypes)
    {
        ValidateIdentity(workerId, nameof(workerId));
        ValidateIdentity(sessionId, nameof(sessionId));
        Name = ValidateText(name, 200, nameof(name));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentNullException.ThrowIfNull(supportedTypes);
        if (supportedTypes.Count is < 1 or > 100)
        {
            throw new ArgumentException("Between 1 and 100 supported types are required.", nameof(supportedTypes));
        }

        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in supportedTypes)
        {
            ValidateText(type, 100, nameof(supportedTypes));
            if (!types.Add(type))
            {
                throw new ArgumentException("Supported types must be unique.", nameof(supportedTypes));
            }
        }

        WorkerId = workerId;
        SessionId = sessionId;
        Capacity = capacity;
        SupportedTypes = Array.AsReadOnly(types.Order(StringComparer.Ordinal).ToArray());
    }

    public Guid WorkerId { get; }
    public Guid SessionId { get; }
    public string Name { get; }
    public int Capacity { get; }
    public IReadOnlyList<string> SupportedTypes { get; }

    public static void ValidateIdentity(Guid value, string parameterName)
    {
        if (value.Version != 7 || (value.Variant & 0b1100) != 0b1000)
        {
            throw new ArgumentException("Identity must be an RFC UUID v7.", parameterName);
        }
    }

    private static string ValidateText(string value, int maximumLength, string parameterName)
    {
        DomainValidation.RequiredText(value, maximumLength, parameterName);
        if (value.Contains('\0'))
        {
            throw new ArgumentException("Text cannot contain NUL.", parameterName);
        }

        return value;
    }
}
