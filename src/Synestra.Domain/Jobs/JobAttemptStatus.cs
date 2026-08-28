namespace Synestra.Domain.Jobs;

public enum JobAttemptStatus
{
    Running,
    Succeeded,
    Failed,
    Abandoned
}
