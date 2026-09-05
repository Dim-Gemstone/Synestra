namespace Synestra.Persistence.Workers;

internal sealed class WorkerSessionRecord
{
    public Guid WorkerId { get; set; }
    public Guid SessionId { get; set; }
}
