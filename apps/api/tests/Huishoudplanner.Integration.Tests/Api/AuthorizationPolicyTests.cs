using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Huishoudplanner.Adapters.Http;
using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Routing;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Ports apps/server/test/identity.test.ts and the permission cases of roles.test.ts through the real pipeline:
/// header adapter, authentication handler and the three policies, on test-only endpoints.
/// </summary>
public sealed class AuthorizationPolicyTests : IDisposable
{
    private readonly FakeUserDirectory users = new();
    private readonly ApiFactory factory;
    private readonly HttpClient client;

    public AuthorizationPolicyTests()
    {
        factory = ApiFactory.WithoutDatabase().WithPort<ForFindingUsers>(users).WithEndpoints(MapTestEndpoints);
        client = factory.CreateClient();
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
    }

    private static void MapTestEndpoints(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/test/whoami", async Task<IResult> (HttpContext http) => Results.Json(Describe(await http.GetActorAsync())));
        routes.MapPost("/test/write", async Task<IResult> (HttpContext http) => Results.Json(Describe(await http.GetActorAsync()))).RequireActor();
        routes.MapPost("/test/plan", async Task<IResult> (HttpContext http) => Results.Json(Describe(await http.GetActorAsync()))).RequirePlanner();
        routes.MapPost("/test/admin", async Task<IResult> (HttpContext http) => Results.Json(Describe(await http.GetActorAsync()))).RequireAdmin();
        routes.MapPost("/test/both", () => "x").RequirePlanner().RequireAdmin();
        routes.MapGet("/test/challenge", async (HttpContext http) => { await http.ChallengeAsync(); });
        routes.MapGet("/test/forbid", async (HttpContext http) => { await http.ForbidAsync(); });
    }

    private static object Describe(Actor? actor) => new
    {
        actorId = actor?.ActorId,
        source = actor?.Source.ToString().ToLowerInvariant(),
        role = actor?.Role.ToString().ToLowerInvariant(),
    };

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? profileId = null, string? clientHeader = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (profileId is not null)
        {
            request.Headers.Add("X-Profile-Id", profileId);
        }

        if (clientHeader is not null)
        {
            request.Headers.Add("X-Client", clientHeader);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static async Task<JsonElement> AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await Body(response);
        body.GetProperty("type").GetString().Should().Be($"urn:huishoudplanner:problem:{code}");
        body.GetProperty("status").GetInt32().Should().Be((int)status);
        body.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
        return body;
    }

    [Fact]
    public async Task Write_withActiveProfileAndWebClient_resolvesTheActorWithSourceUi()
    {
        var user = users.Add(Role.Member);

        var response = await Send(HttpMethod.Post, "/test/write", user.Id, "web");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Body(response);
        body.GetProperty("actorId").GetString().Should().Be(user.Id);
        body.GetProperty("source").GetString().Should().Be("ui");
        body.GetProperty("role").GetString().Should().Be("member");
    }

    [Fact]
    public async Task Write_withoutWebClient_hasSourceApi()
    {
        var user = users.Add(Role.Member);

        var response = await Send(HttpMethod.Post, "/test/write", user.Id);

        (await Body(response)).GetProperty("source").GetString().Should().Be("api");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nope")]
    [InlineData("0123456789abcdef01234567")]
    public async Task Write_withoutAnActiveProfile_is400ProfileRequired(string? profileId)
    {
        var response = await Send(HttpMethod.Post, "/test/write", profileId);

        await AssertProblem(response, HttpStatusCode.BadRequest, "profile_required");
    }

    [Fact]
    public async Task Write_fromAnInactiveProfile_is400ProfileRequired()
    {
        var inactive = users.Add(Role.Admin, active: false);

        var response = await Send(HttpMethod.Post, "/test/write", inactive.Id);

        await AssertProblem(response, HttpStatusCode.BadRequest, "profile_required");
    }

    [Fact]
    public async Task Read_withoutAProfile_isAllowed_andHasNoActor()
    {
        var response = await Send(HttpMethod.Get, "/test/whoami");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Body(response);
        body.GetProperty("actorId").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("source").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Read_withAnActiveProfile_exposesTheActor()
    {
        var user = users.Add(Role.Planner);

        var body = await Body(await Send(HttpMethod.Get, "/test/whoami", user.Id, "web"));

        body.GetProperty("actorId").GetString().Should().Be(user.Id);
    }

    [Theory]
    [InlineData("/test/plan")]
    [InlineData("/test/admin")]
    public async Task ElevatedPolicies_withoutAProfile_are400ProfileRequired(string path)
    {
        var response = await Send(HttpMethod.Post, path);

        await AssertProblem(response, HttpStatusCode.BadRequest, "profile_required");
    }

    [Theory]
    [InlineData(Role.Member, "/test/write", HttpStatusCode.OK)]
    [InlineData(Role.Member, "/test/plan", HttpStatusCode.Forbidden)]
    [InlineData(Role.Member, "/test/admin", HttpStatusCode.Forbidden)]
    [InlineData(Role.Planner, "/test/write", HttpStatusCode.OK)]
    [InlineData(Role.Planner, "/test/plan", HttpStatusCode.OK)]
    [InlineData(Role.Planner, "/test/admin", HttpStatusCode.Forbidden)]
    [InlineData(Role.Admin, "/test/write", HttpStatusCode.OK)]
    [InlineData(Role.Admin, "/test/plan", HttpStatusCode.OK)]
    [InlineData(Role.Admin, "/test/admin", HttpStatusCode.OK)]
    public async Task RoleMatrix_allowsEqualOrHigherRolesOnly(Role role, string path, HttpStatusCode expected)
    {
        var user = users.Add(role);

        var response = await Send(HttpMethod.Post, path, user.Id, "web");

        if (expected == HttpStatusCode.OK)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        else
        {
            await AssertProblem(response, HttpStatusCode.Forbidden, "permission_denied");
        }
    }

    [Fact]
    public async Task Forbidden_namesTheRequiredRole_andNotTheCallersId()
    {
        var member = users.Add(Role.Member);

        var response = await Send(HttpMethod.Post, "/test/admin", member.Id);

        var body = await Body(response);
        body.GetProperty("detail").GetString().Should().Be("The admin role is required.");
        body.GetRawText().Should().NotContain(member.Id);
    }

    [Fact]
    public async Task FailingUserLookup_is500InternalError_withoutLeakingTheReason()
    {
        users.Failure = new PortError("secret connection string");

        var response = await Send(HttpMethod.Post, "/test/write", "0123456789abcdef01234567");

        var body = await AssertProblem(response, HttpStatusCode.InternalServerError, "internal_error");
        body.GetRawText().Should().NotContain("secret");
    }

    [Fact]
    public async Task FailingUserLookup_onAnUnprotectedRead_isAlso500()
    {
        users.Failure = new PortError("down");

        var response = await Send(HttpMethod.Get, "/test/whoami", "0123456789abcdef01234567");

        await AssertProblem(response, HttpStatusCode.InternalServerError, "internal_error");
    }

    [Fact]
    public async Task ActorIsResolvedOncePerRequest()
    {
        var admin = users.Add(Role.Admin);

        await Send(HttpMethod.Post, "/test/admin", admin.Id);

        users.Lookups.Should().HaveCount(1);
    }

    [Fact]
    public async Task Forbidden_withSeveralPolicies_namesTheHighestRequiredRole()
    {
        var member = users.Add(Role.Member);

        var response = await Send(HttpMethod.Post, "/test/both", member.Id);

        var body = await AssertProblem(response, HttpStatusCode.Forbidden, "permission_denied");
        body.GetProperty("detail").GetString().Should().Be("The admin role is required.");
    }

    [Fact]
    public async Task DirectChallenge_answersProfileRequiredProblemDetails()
    {
        var response = await Send(HttpMethod.Get, "/test/challenge");

        await AssertProblem(response, HttpStatusCode.BadRequest, "profile_required");
    }

    [Fact]
    public async Task DirectForbid_answersPermissionDeniedProblemDetails()
    {
        var member = users.Add(Role.Member);

        var response = await Send(HttpMethod.Get, "/test/forbid", member.Id);

        await AssertProblem(response, HttpStatusCode.Forbidden, "permission_denied");
    }

    [Fact]
    public void AddHttpAdapter_withoutAUserPort_failsAtStartupWithAClearMessage()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddHttpAdapter();
        using var app = builder.Build();

        var act = () => app.UseHttpAdapter();

        act.Should().Throw<InvalidOperationException>().WithMessage("*ForFindingUsers*");
    }
}
