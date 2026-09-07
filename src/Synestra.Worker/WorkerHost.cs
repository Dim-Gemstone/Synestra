using Microsoft.Extensions.Options;

namespace Synestra.Worker;

public static class WorkerHost
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ApplicationName = typeof(WorkerHost).Assembly.FullName
        });
        builder.Services.AddOptions<WorkerOptions>().BindConfiguration("Worker")
            .Validate(WorkerOptions.IsValid, "Worker requires an HTTP(S) origin, an absolute state directory and a valid name.")
            .ValidateOnStart();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<WorkerExitStatus>();
        builder.Services.AddSingleton(provider => new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        })
        {
            BaseAddress = new Uri(provider.GetRequiredService<IOptions<WorkerOptions>>().Value.ApiBaseAddress),
            Timeout = Timeout.InfiniteTimeSpan
        });
        builder.Services.AddSingleton<WorkerApiClient>();
        builder.Services.AddHostedService<WorkerAgent>();
        return builder;
    }
}

internal sealed class WorkerExitStatus
{
    public int ExitCode { get; private set; }
    public void Fail() => ExitCode = 1;
}
