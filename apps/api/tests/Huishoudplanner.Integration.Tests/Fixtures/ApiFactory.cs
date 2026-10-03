using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OneOf;

namespace Huishoudplanner.Integration.Tests.Fixtures;

/// <summary>
/// The real Host in memory. Configuration comes from in-process settings, never from the developer's environment
/// (own database name, own state). Swap a driven port with <see cref="WithPort{TPort}"/>; without a swap the real
/// adapters run against <c>MONGO_URL</c>, so a Docker-backed variant is a factory made with <see cref="ForMongo"/>.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string mongoUrl;
    private readonly List<Action<IServiceCollection>> overrides = [];

    private readonly List<ILoggerProvider> logProviders = [];
    private string? webDistDir;

    private ApiFactory(string mongoUrl) => this.mongoUrl = mongoUrl;

    /// <summary>Backed by a real MongoDB (use the shared <see cref="MongoContainerFixture"/>), in its own database.</summary>
    public static ApiFactory ForMongo(MongoContainerFixture mongo, string? databaseName = null)
    {
        ArgumentNullException.ThrowIfNull(mongo);
        return new ApiFactory(WithDatabase(mongo.ConnectionString, databaseName ?? MongoContainerFixture.NewDatabaseName()));
    }

    /// <summary>For tests that replace every port they touch: no database is contacted unless a real adapter is used.</summary>
    public static ApiFactory WithoutDatabase() => new("mongodb://127.0.0.1:1/unused");

    /// <summary>A MongoDB address nothing listens on, for the "database is down" case with the real adapter.</summary>
    public static ApiFactory ForUnreachableMongo() => new("mongodb://127.0.0.1:1/unreachable");

    /// <summary>Collects what the host logs.</summary>
    public ApiFactory WithLogProvider(ILoggerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        logProviders.Add(provider);
        return this;
    }

    /// <summary>Serves the web app from this directory (<c>WEB_DIST_DIR</c>).</summary>
    public ApiFactory WithWebDist(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        webDistDir = directory;
        return this;
    }

    /// <summary>Replaces the registration of a driven port with a fake.</summary>
    public ApiFactory WithPort<TPort>(TPort fake)
        where TPort : class
    {
        ArgumentNullException.ThrowIfNull(fake);
        overrides.Add(services => services.Replace(ServiceDescriptor.Singleton(fake)));
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Test");
        builder.UseSetting("MONGO_URL", mongoUrl);
        if (webDistDir is not null)
        {
            builder.UseSetting("WEB_DIST_DIR", webDistDir);
        }

        builder.ConfigureLogging(logging => logProviders.ForEach(p => logging.AddProvider(p)));
        builder.ConfigureTestServices(services =>
        {
            foreach (var apply in overrides)
            {
                apply(services);
            }
        });
    }

    private static string WithDatabase(string connectionString, string databaseName)
    {
        // The container user lives in the admin database, not in the per-class database.
        var uri = new UriBuilder(connectionString) { Path = databaseName };
        uri.Query = uri.Query.TrimStart('?') + "&authSource=admin";
        return uri.Uri.ToString();
    }
}

/// <summary>A hand-written fake of <see cref="ForCheckingHealth"/>.</summary>
public sealed class FakeHealthPort(bool reachable) : ForCheckingHealth
{
    public Task<OneOf<Success, PortError>> CheckDatabaseAsync(CancellationToken cancellationToken) =>
        Task.FromResult<OneOf<Success, PortError>>(reachable ? new Success() : new PortError("fake database down"));
}
