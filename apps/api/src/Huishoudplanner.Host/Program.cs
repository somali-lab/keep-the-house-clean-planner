var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

await app.RunAsync();

// Makes the entry point visible to WebApplicationFactory<Program> in the integration tests.
public partial class Program;
