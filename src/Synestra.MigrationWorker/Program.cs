using Synestra.MigrationWorker;
using Synestra.Persistence.Extensions;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

var connectionString =
    builder.Configuration.GetConnectionString("synestra")
    ?? throw new InvalidOperationException("Connection string 'synestra' was not found.");

builder.Services.AddPersistence(connectionString);
builder.Services.AddHostedService<MigrationWorker>();

await builder.Build().RunAsync();
