using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Integration.Tests.Fixtures;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// The OpenAPI document is generated at build time into apps/api/openapi/v2.json (plan section 4.1) and is the reviewed
/// source of the generated TypeScript client. These tests fail when the live document and the checked-in one differ.
/// </summary>
public sealed class OpenApiDocumentTests
{
    private const string RegenerateCommand = "dotnet build apps/api/src/Huishoudplanner.Host (from the repository root; writes apps/api/openapi/v2.json)";

    private static ApiFactory Development() =>
        ApiFactory.WithoutDatabase().WithPort<ForCheckingHealth>(new FakeHealthPort(true)).InEnvironment("Development");

    private static async Task<JsonNode> LiveDocument()
    {
        using var factory = Development();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v2.json", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task LiveDocument_equalsTheCheckedInDocument()
    {
        var path = Path.Combine(RepositoryRoot(), "apps", "api", "openapi", "v2.json");
        File.Exists(path).Should().BeTrue($"the document must be checked in at {path}; regenerate it with: {RegenerateCommand}");
        var checkedIn = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;

        var live = await LiveDocument();

        JsonNode.DeepEquals(live, checkedIn).Should().BeTrue(
            $"the live OpenAPI document drifted from apps/api/openapi/v2.json. Regenerate and commit it: {RegenerateCommand}.{Environment.NewLine}Live document:{Environment.NewLine}{live.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}");
    }

    [Fact]
    public async Task LiveDocument_isOpenApi31AndDescribesTheHealthEndpoint()
    {
        var live = await LiveDocument();

        live["openapi"]!.GetValue<string>().Should().StartWith("3.1");
        live["info"]!["title"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        var version = (await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "version.txt"), TestContext.Current.CancellationToken)).Trim();
        live["info"]!["version"]!.GetValue<string>().Should().Be(version);
        var get = live["paths"]!["/api/v2/health"]!["get"]!;
        get["operationId"]!.GetValue<string>().Should().Be("getHealth");
        get["summary"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        get["tags"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Contain("Health");
        var responses = get["responses"]!.AsObject().Select(r => r.Key);
        responses.Should().BeEquivalentTo("200", "500", "503");
        live["components"]!["schemas"]!.AsObject().Select(s => s.Key).Should().Contain("HealthResponse").And.Contain("ProblemDetails");
    }

    [Fact]
    public async Task OpenApiDocument_isNotServedOutsideDevelopment()
    {
        using var factory = ApiFactory.WithoutDatabase();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v2.json", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ScalarUi_isServedInDevelopment()
    {
        using var factory = Development();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/scalar/v2", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DocumentAndScalarUi_areNotShadowedByTheSpaFallback()
    {
        var dist = Directory.CreateTempSubdirectory("openapi-spa").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dist, "index.html"), "<html>spa</html>", TestContext.Current.CancellationToken);
            using var factory = Development().WithWebDist(dist);
            using var client = factory.CreateClient();

            var document = await client.GetAsync("/openapi/v2.json", TestContext.Current.CancellationToken);
            var ui = await client.GetAsync("/scalar/v2", TestContext.Current.CancellationToken);
            var spa = await client.GetAsync("/some/page", TestContext.Current.CancellationToken);

            document.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            (await ui.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain("<html>spa</html>");
            (await spa.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("spa");
        }
        finally
        {
            Directory.Delete(dist, recursive: true);
        }
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
