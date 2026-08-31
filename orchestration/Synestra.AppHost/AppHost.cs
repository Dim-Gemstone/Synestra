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

builder
    .AddProject<Projects.Synestra_Api>("synestra-api")
    .WithReference(database)
    .WaitForCompletion(migrations);

builder.Build().Run();
