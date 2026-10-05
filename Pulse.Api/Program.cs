using Pulse.Api.Configuration;

EnvFileLoader.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Host.AddPulseLogging();
builder.Services.AddPulseServices();

var app = builder.Build();

await app.ApplyMigrationsAsync();
app.RegisterHangfireJobs();
app.UsePulsePipeline();
app.MapPulseEndpoints();

app.Run();

// Expose for WebApplicationFactory in integration tests
public partial class Program { }
