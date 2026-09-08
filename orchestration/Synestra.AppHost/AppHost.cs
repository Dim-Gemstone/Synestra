var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder
    .AddPostgres("postgres")
    .WithImageTag("17.11-alpine3.24")
    .WithDataVolume();

var database = postgres.AddDatabase("synestra");

var migrations = builder
    .AddProject<Projects.Synestra_MigrationWorker>("synestra-migrations")
    .WithReference(database)
    .WaitFor(database);

var api = builder
    .AddProject<Projects.Synestra_Api>("synestra-api", launchProfileName: "http")
    .WithReference(database)
    .WithHttpHealthCheck("/health")
    .WaitForCompletion(migrations);

var workerStateDirectory = builder.Configuration["Worker:StateDirectory"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Synestra", "worker");

builder.AddProject<Projects.Synestra_Worker>("synestra-worker")
    .WithEnvironment("Worker__ApiBaseAddress", api.GetEndpoint("http"))
    .WithEnvironment("Worker__StateDirectory", workerStateDirectory)
    .WaitFor(api);

builder.Build().Run();
