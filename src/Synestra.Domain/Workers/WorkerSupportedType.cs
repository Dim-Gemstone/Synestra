namespace Synestra.Domain.Workers;

public sealed class WorkerSupportedType
{
    private WorkerSupportedType() { }

    internal WorkerSupportedType(Guid workerId, string type)
    {
        WorkerId = workerId;
        Type = type;
    }

    public Guid WorkerId { get; private set; }
    public string Type { get; private set; } = null!;
}
