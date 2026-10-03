// API integration test template: the /api/v2 HTTP pipeline via WebApplicationFactory<Program>.
// Framework: xunit.v3 + AwesomeAssertions.
// Requests hit the real Minimal API endpoints in the Host, against the shared Testcontainers
// mongo replica set (each factory points at its own uniquely named database) and a fixed clock.
// Identity is the profile header adapter: the actor comes from X-Profile-Id, not from a login
// (ADR-0003). Errors are RFC 9457 Problem Details with a stable type URN and a traceId.
// Every write endpoint is also walked by the audit coverage test.
// Illustrative: it need not compile, but the shapes are the rules.

using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Huishoudplanner.Adapters.Http.Contracts;
using Huishoudplanner.Integration.Tests.Fixtures;

namespace Huishoudplanner.Integration.Tests.Api;

// [Collection] shares one HouseholdApiFactory (one database, one host, one FakeTimeProvider)
// across the endpoint test classes. factory.CreateClient() yields an HttpClient for the in-memory server.
[Collection("ApiIntegration")]
public sealed class OccurrenceEndpointsTemplateTests(HouseholdApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Complete_OpenOccurrenceAsPlanner_ReturnsOkAndWritesAuditEntry()
    {
        // Arrange: the seeded planner profile and an open occurrence created through the data layer.
        var ct = TestContext.Current.CancellationToken;
        var planner = await factory.SeedPlannerAsync(ct);
        var occurrence = await factory.SeedOpenOccurrenceAsync(ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/occurrences/{occurrence.Id}/complete")
        {
            Content = JsonContent.Create(new { requestId = Guid.NewGuid().ToString("N") }),
        };
        request.Headers.Add("X-Profile-Id", planner.Id); // 24-hex id

        // Act
        var response = await _client.SendAsync(request, ct);

        // Assert: the intent endpoint succeeded and the audit entry exists next to the change.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<OccurrenceResponse>(cancellationToken: ct);
        body!.Status.Should().Be("completed");
        (await factory.CountAuditEntriesAsync(occurrence.Id, ct)).Should().Be(1);
    }

    [Fact]
    public async Task Complete_AlreadyCompleted_ReturnsProblemDetailsWithStableType()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var planner = await factory.SeedPlannerAsync(ct);
        var occurrence = await factory.SeedCompletedOccurrenceAsync(ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/occurrences/{occurrence.Id}/complete");
        request.Headers.Add("X-Profile-Id", planner.Id);

        // Act
        var response = await _client.SendAsync(request, ct);

        // Assert: ConflictError becomes 409 application/problem+json; type is the stable URN, traceId is present.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(cancellationToken: ct);
        problem!.Type.Should().Be("urn:huishoudplanner:problem:invalid_transition");
        problem.TraceId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Complete_WithoutProfileHeader_IsRejectedBeforeAnyWork()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act: no actor resolved, so the RequireActor policy rejects the write.
        var response = await _client.PostAsync("/api/v2/occurrences/000000000000000000000000/complete", content: null, ct);

        // Assert: same contract as today (requirements section 8): 400 profile_required.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(cancellationToken: ct);
        problem!.Type.Should().Be("urn:huishoudplanner:problem:profile_required");
    }

    [Fact]
    public async Task List_WithLimit_ReturnsBoundedPageWithNextCursor()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var planner = await factory.SeedPlannerAsync(ct);
        await factory.SeedOpenOccurrencesAsync(count: 3, ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v2/occurrences?limit=2");
        request.Headers.Add("X-Profile-Id", planner.Id);

        // Act
        var response = await _client.SendAsync(request, ct);

        // Assert: every list is bounded and carries nextCursor.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<OccurrencePageResponse>(cancellationToken: ct);
        page!.Items.Should().HaveCount(2);
        page.NextCursor.Should().NotBeNull();
    }
}
