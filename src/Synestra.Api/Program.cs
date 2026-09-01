using Synestra.Application.Extensions;
using Synestra.Persistence.Extensions;

var builder = WebApplication.CreateBuilder(args);

var connectionString =
    builder.Configuration.GetConnectionString("synestra")
    ?? throw new InvalidOperationException("Connection string 'synestra' was not found.");

builder.Services.AddPersistence(connectionString);
builder.Services.AddApplication();
builder.Services.AddControllers();
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

app.Run();

public partial class Program;
