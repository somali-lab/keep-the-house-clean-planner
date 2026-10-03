using Huishoudplanner.Host;
using Huishoudplanner.Host.Configuration;

// NODE_ENV is a legacy alias of ASPNETCORE_ENVIRONMENT (see AppOptionsBinder).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    EnvironmentName = AppOptionsBinder.ResolveLegacyHostEnvironment(Environment.GetEnvironmentVariable),
});

builder.Configuration.AddLegacyEnvironmentAliases();
// Build-time OpenAPI generation has no configuration: skip the startup validation (see BuildTimeGeneration).
builder.Services.AddAppOptions(builder.Configuration, validateOnStart: !BuildTimeGeneration.IsRunning);
builder.Services.AddApiServices();

var app = builder.Build();

app.UseApiPipeline();

await app.RunAsync();

// Makes the entry point visible to WebApplicationFactory<Program> in the integration tests.
public partial class Program;
