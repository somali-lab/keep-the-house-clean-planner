using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Integration.Tests.Fixtures;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>The web client sends X-Profile-Id on every request; endpoints without a policy must not depend on the user lookup.</summary>
public sealed class LazyIdentityTests
{
    private const string AnyProfile = "0123456789abcdef01234567";

    private static async Task<(HttpResponseMessage Response, FakeUserDirectory Users)> GetHealth(bool databaseReachable)
    {
        var users = new FakeUserDirectory { Failure = new PortError("user store down") };
        using var factory = ApiFactory.WithoutDatabase()
            .WithPort<ForFindingUsers>(users)
            .WithPort<ForCheckingHealth>(new FakeHealthPort(databaseReachable));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v2/health");
        request.Headers.Add("X-Profile-Id", AnyProfile);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response, users);
    }

    [Fact]
    public async Task Health_withAProfileHeader_whileTheUserLookupFails_stillReportsOk()
    {
        var (response, users) = await GetHealth(databaseReachable: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        users.Lookups.Should().BeEmpty();
    }

    [Fact]
    public async Task Health_withAProfileHeader_whileTheDatabaseIsDown_stillReports503ByTheHealthContract()
    {
        var (response, _) = await GetHealth(databaseReachable: false);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("database").GetString().Should().Be("error");
    }
}
