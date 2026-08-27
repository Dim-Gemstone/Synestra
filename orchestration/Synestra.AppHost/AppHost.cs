var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder
    .AddPostgres("postgres")
    .WithDataVolume();

var database = postgres.AddDatabase("synestra");

builder
    .AddProject<Projects.Synestra_Api>("synestra-api")
    .WithReference(database)
    .WaitFor(database);

builder.Build().Run();
