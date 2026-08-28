namespace Synestra.Domain.Jobs;

public enum JobStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled
}
