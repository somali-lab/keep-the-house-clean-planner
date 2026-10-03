using Huishoudplanner.Host;
using Huishoudplanner.Host.Configuration;
using Huishoudplanner.Host.Telemetry;

// NODE_ENV is a legacy alias of ASPNETCORE_ENVIRONMENT (see AppOptionsBinder).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    EnvironmentName = AppOptionsBinder.ResolveLegacyHostEnvironment(Environment.GetEnvironmentVariable),
});

builder.Configuration.AddLegacyEnvironmentAliases();
// Build-time OpenAPI generation has no configuration and no telemetry exporters: skip startup validation and telemetry (see BuildTimeGeneration).
builder.Services.AddAppOptions(builder.Configuration, validateOnStart: !BuildTimeGeneration.IsRunning);
if (!BuildTimeGeneration.IsRunning)
{
    builder.Services.AddAppTelemetry(builder.Configuration);
}
builder.Services.AddApiServices();

var app = builder.Build();

app.UseApiPipeline();

await app.RunAsync();

// Makes the entry point visible to WebApplicationFactory<Program> in the integration tests.
public partial class Program;
