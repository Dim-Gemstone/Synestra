namespace Synestra.Api.Executions;

public sealed class ExecutionFinalizationOptions
{
    public const string SectionName = "ExecutionFinalization";

    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 100;
}
