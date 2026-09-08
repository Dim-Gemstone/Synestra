using Synestra.Api.Executions;
using Synestra.Application.Extensions;
using Synestra.Persistence.Extensions;

var builder = WebApplication.CreateBuilder(args);

var connectionString =
    builder.Configuration.GetConnectionString("synestra")
    ?? throw new InvalidOperationException("Connection string 'synestra' was not found.");

builder.Services.AddPersistence(connectionString);
builder.Services.AddApplication();
builder.Services.AddOptions<ExecutionFinalizationOptions>()
    .BindConfiguration(ExecutionFinalizationOptions.SectionName)
    .Validate(options => options.IntervalSeconds > 0 && options.IntervalSeconds <= (uint.MaxValue - 1) / 1000,
        "ExecutionFinalization:IntervalSeconds must be positive and within the timer's supported range.")
    .Validate(options => options.BatchSize > 0, "ExecutionFinalization:BatchSize must be positive.")
    .ValidateOnStart();
builder.Services.AddHostedService<ExpiredExecutionFinalizer>();
builder.Services.AddControllers();
builder.Services.AddHealthChecks();
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        if (!context.ProblemDetails.Extensions.ContainsKey("code"))
        {
            context.ProblemDetails.Extensions["code"] = "internal_error";
            context.ProblemDetails.Type = "urn:synestra:problem:internal-error";
            context.ProblemDetails.Title = "An unexpected error occurred.";
        }
    };
});
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

public partial class Program;
