var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Synestra_Api>("synestra-api");

builder.Build().Run();
