using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Huishoudplanner.Adapters.Http;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>The error value to Problem Details mapping and the exception and status-code pipeline, on a tiny host with one endpoint per error.</summary>
public sealed class ProblemDetailsTests : IAsyncLifetime
{
    private WebApplication app = null!;
    private HttpClient client = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<Huishoudplanner.Domain.Ports.Driven.ForFindingUsers>(new Fixtures.FakeUserDirectory());
        builder.Services.AddHttpAdapter();
        app = builder.Build();
        app.UseHttpAdapter();
        app.MapGet("/not-found", () => ProblemResults.From(new NotFound()));
        app.MapGet("/conflict", () => ProblemResults.From(new ConflictError("room_in_use", "The room still has tasks.")));
        app.MapGet("/validation", () => ProblemResults.From(ValidationErrors.For("name", "Name is required.", "Name is too short.")));
        app.MapGet("/port-error", (ILogger<PortError> logger) => ProblemResults.From(new PortError("secret connection string leaked"), logger));
        app.MapGet("/throws", () => ThrowSecret());
        app.MapPost("/post-only", () => "x");
        await app.StartAsync(TestContext.Current.CancellationToken);
        client = app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await app.DisposeAsync();
    }

    private static string ThrowSecret() => throw new InvalidOperationException("secret stack detail");

    private async Task<(HttpResponseMessage Response, JsonElement Body)> Get(string path)
    {
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("status").GetInt32().Should().Be((int)response.StatusCode);
        return (response, body);
    }

    [Fact]
    public async Task NotFound_maps_to_404_not_found()
    {
        var (response, body) = await Get("/not-found");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:not_found");
        body.GetProperty("detail").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ConflictError_maps_to_409_with_its_own_code_and_detail()
    {
        var (response, body) = await Get("/conflict");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:room_in_use");
        body.GetProperty("detail").GetString().Should().Be("The room still has tasks.");
    }

    [Fact]
    public async Task ValidationErrors_map_to_400_with_the_errors_extension()
    {
        var (response, body) = await Get("/validation");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:validation_error");
        body.GetProperty("errors").GetProperty("name").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("Name is required.", "Name is too short.");
    }

    [Fact]
    public async Task PortError_maps_to_500_internal_error_without_its_message()
    {
        var (response, body) = await Get("/port-error");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:internal_error");
        body.GetRawText().Should().NotContain("secret");
    }

    [Fact]
    public async Task UnhandledException_becomes_500_internal_error_without_the_exception_message()
    {
        var (response, body) = await Get("/throws");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:internal_error");
        body.GetProperty("detail").GetString().Should().Be(ProblemResults.UnexpectedErrorDetail);
        body.GetRawText().Should().NotContain("secret");
    }

    [Fact]
    public async Task UnknownRoute_becomes_a_404_problem()
    {
        var (response, body) = await Get("/nowhere");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:not_found");
        body.GetProperty("detail").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task WrongMethod_becomes_a_405_problem()
    {
        var (response, body) = await Get("/post-only");

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:method_not_allowed");
    }
}
