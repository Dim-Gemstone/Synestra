namespace Synestra.Worker;

internal static class WorkerProgram
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            using var host = WorkerHost.CreateBuilder(args).Build();
            var status = host.Services.GetRequiredService<WorkerExitStatus>();
            await host.RunAsync();
            return status.ExitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Worker startup failed. Failure type: {exception.GetType().Name}.");
            return 1;
        }
    }
}
