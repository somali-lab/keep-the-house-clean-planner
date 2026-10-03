using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>Ports apps/server/test/static.test.ts and adds the traversal, HEAD, content type and missing-directory cases.</summary>
public sealed class StaticWebAppTests : IDisposable
{
    private readonly string dist = Path.Combine(Path.GetTempPath(), "hhp-dist-" + Guid.NewGuid().ToString("N"));

    public StaticWebAppTests()
    {
        Directory.CreateDirectory(Path.Combine(dist, "assets"));
        File.WriteAllText(Path.Combine(dist, "index.html"), "<!doctype html><title>Keep the House Clean</title>");
        File.WriteAllText(Path.Combine(dist, "assets", "app.js"), "console.log(1)");
        File.WriteAllText(Path.Combine(dist, "assets", "style.css"), "body{}");
    }

    public void Dispose() => Directory.Delete(dist, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApiFactory Factory() => ApiFactory.WithoutDatabase().WithWebDist(dist);

    [Fact]
    public async Task Root_servesIndexHtml()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("Keep the House Clean");
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    [Fact]
    public async Task Asset_isServedWithItsContentTypeAndRevalidationHeaders()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var js = await client.GetAsync("/assets/app.js", Ct);
        var css = await client.GetAsync("/assets/style.css", Ct);

        js.StatusCode.Should().Be(HttpStatusCode.OK);
        js.Content.Headers.ContentType!.MediaType.Should().BeOneOf("text/javascript", "application/javascript");
        css.Content.Headers.ContentType!.MediaType.Should().Be("text/css");
        js.Headers.ETag.Should().NotBeNull();
        js.Headers.CacheControl!.Public.Should().BeTrue();
        js.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("/vandaag")]
    [InlineData("/rooms/12/edit")]
    [InlineData("/vandaag?tab=1")]
    public async Task ClientRoute_fallsBackToIndexHtml(string url)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(url, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("Keep the House Clean");
    }

    [Theory]
    [InlineData("/api/nope")]
    [InlineData("/api/v2/unknown")]
    [InlineData("/api/v2/unknown/deep")]
    [InlineData("/api")]
    [InlineData("/api/")]
    public async Task UnknownApiRoute_staysProblemDetails404(string url)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(url, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("status").GetInt32().Should().Be(404);
    }

    [Theory]
    [InlineData("/assets/missing.js")]
    [InlineData("/favicon.ico")]
    public async Task MissingFileWithExtension_is404(string url)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(url, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Post_toAClientRoute_doesNotFallBack()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/vandaag", content: null, Ct);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Head_returnsHeadersWithoutBody()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var asset = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/assets/app.js"), Ct);
        var route = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/vandaag"), Ct);

        asset.StatusCode.Should().Be(HttpStatusCode.OK);
        (await asset.Content.ReadAsStringAsync(Ct)).Should().BeEmpty();
        route.StatusCode.Should().Be(HttpStatusCode.OK);
        route.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await route.Content.ReadAsStringAsync(Ct)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("/..%2fsecret.txt")]
    [InlineData("/%2e%2e/secret.txt")]
    [InlineData("/assets/..%5c..%5csecret.txt")]
    [InlineData("/assets/%2e%2e%2f%2e%2e%2fsecret.txt")]
    public async Task PathTraversal_neverLeaksFilesOutsideTheDistDirectory(string url)
    {
        var secret = Path.Combine(Path.GetDirectoryName(dist)!, "secret.txt");
        await File.WriteAllTextAsync(secret, "TOP-SECRET", Ct);
        try
        {
            using var factory = Factory();
            using var client = factory.CreateClient();

            var response = await client.GetAsync(url, Ct);

            (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain("TOP-SECRET");
            response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public async Task WithoutWebDistDir_clientRoutesAre404()
    {
        using var factory = ApiFactory.WithoutDatabase();
        using var client = factory.CreateClient();

        (await client.GetAsync("/", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/vandaag", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WithMissingWebDistDir_behavesAsNotConfigured()
    {
        using var factory = ApiFactory.WithoutDatabase().WithWebDist(Path.Combine(dist, "does-not-exist"));
        using var client = factory.CreateClient();

        (await client.GetAsync("/vandaag", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Health_isNotShadowedByTheFallback()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v2/health", Ct);

        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }
}
