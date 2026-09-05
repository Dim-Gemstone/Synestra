namespace Synestra.Application.Jobs;

public sealed class GetJob(IGetJobPersistence persistence)
{
    public async Task<GetJobResult> ExecuteAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await persistence.FindByIdAsync(id, cancellationToken);
        return job is null
            ? new GetJobResult(GetJobOutcome.NotFound)
            : new GetJobResult(GetJobOutcome.Succeeded, job);
    }
}
