using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Integration.Tests.Fixtures;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>Ports the health cases of apps/server/test/health.test.ts and health-down.test.ts through the HTTP pipeline.</summary>
public sealed class HealthEndpointTests(MongoContainerFixture mongo)
{
    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static ApiFactory FakeDatabase(bool reachable) =>
        ApiFactory.WithoutDatabase().WithPort<ForCheckingHealth>(new FakeHealthPort(reachable));

    [Fact]
    public async Task Health_withReachableDatabasePort_returns200AndOkBody()
    {
        using var factory = FakeDatabase(true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v2/health", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await BodyOf(response);
        body.GetProperty("status").GetString().Should().Be("ok");
        body.GetProperty("database").GetString().Should().Be("ok");
        body.GetProperty("version").GetString().Should().MatchRegex(@"^\d+\.\d+\.\d+");
    }

    [Fact]
    public async Task Health_reportsTheVersionOfVersionTxt()
    {
        var expected = (await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "version.txt"), TestContext.Current.CancellationToken)).Trim();
        using var factory = FakeDatabase(true);
        using var client = factory.CreateClient();

        var body = await BodyOf(await client.GetAsync("/api/v2/health", TestContext.Current.CancellationToken));

        body.GetProperty("version").GetString().Should().Be(expected);
    }

    [Fact]
    public async Task Health_withUnreachableDatabasePort_returns503AndErrorBody()
    {
        using var factory = FakeDatabase(false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v2/health", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await BodyOf(response);
        body.GetProperty("status").GetString().Should().Be("error");
        body.GetProperty("database").GetString().Should().Be("error");
    }

    [Fact]
    public async Task Health_withRealMongo_reportsOk()
    {
        using var factory = ApiFactory.ForMongo(mongo);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v2/health", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyOf(response)).GetProperty("database").GetString().Should().Be("ok");
    }

    [Fact]
    public async Task Health_whenMongoIsDown_returns503WithTheRealAdapter()
    {
        using var factory = ApiFactory.ForUnreachableMongo();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v2/health", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await BodyOf(response);
        body.GetProperty("status").GetString().Should().Be("error");
        body.GetProperty("database").GetString().Should().Be("error");
    }

    [Fact]
    public async Task UnknownApiRoute_returnsProblemJson404()
    {
        using var factory = FakeDatabase(true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v2/nope", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await BodyOf(response);
        body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:not_found");
        body.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "version.txt")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("version.txt not found above the test output.");
    }
}
