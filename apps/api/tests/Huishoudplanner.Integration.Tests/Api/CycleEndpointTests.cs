using System.Net;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Ports apps/server/test/cycles-api.test.ts to <c>GET /api/v2/cycles</c>: empty before any generation, generated cycles in order with day-key
/// boundaries, and no write route (the Node server answers 404 for a POST, ASP.NET Core 405 for a route that only has GET). The generation is
/// triggered through the use case, because the job endpoint is slice 6.3. Adds paging, the field-keyed 400s and the Node-shaped documents.
/// </summary>
public sealed class CycleEndpointTests(MongoContainerFixture mongo)
{
    private static string[] ErrorFields(JsonElement problem) => [.. problem.GetProperty("errors").EnumerateObject().Select(p => p.Name)];

    [Fact]
    public async Task The_list_is_empty_before_any_generation_and_needs_no_profile()
    {
        using var h = new GenerationHarness(mongo, "2026-09-14T06:00:00.000Z");

        var (status, body) = await h.SendAsync(HttpMethod.Get, "/api/v2/cycles", withProfile: false);

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("items").GetArrayLength().Should().Be(0);
        body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Generated_cycles_are_listed_in_order_with_day_key_boundaries_and_a_plan_id()
    {
        using var h = new GenerationHarness(mongo, "2026-09-14T06:00:00.000Z");
        var run = await h.GenerateUpcomingAsync();

        var (status, body) = await h.SendAsync(HttpMethod.Get, "/api/v2/cycles", withProfile: false);

        status.Should().Be(HttpStatusCode.OK);
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items.Select(c => (c.GetProperty("index").GetInt32(), c.GetProperty("startDate").GetString(), c.GetProperty("endDate").GetString())).Should().Equal(
            (0, "2026-09-14", "2026-10-11"),
            (1, "2026-10-12", "2026-11-08"));
        items[0].GetProperty("planId").GetString().Should().HaveLength(24);
        items[0].GetProperty("id").GetString().Should().HaveLength(24);
        items[0].TryGetProperty("_id", out _).Should().BeFalse();
        items[0].GetProperty("generationRunId").GetString().Should().Be(run.RunId);
        items[0].GetProperty("generatedAt").GetDateTimeOffset().Should().Be(new DateTimeOffset(2026, 9, 14, 6, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_cycle_generated_without_an_active_plan_has_a_null_plan_id()
    {
        using var h = new GenerationHarness(mongo, "2026-09-14T06:00:00.000Z");
        await h.Database.GetCollection<MongoDB.Bson.BsonDocument>("cyclePlans").UpdateManyAsync(
            MongoDB.Driver.FilterDefinition<MongoDB.Bson.BsonDocument>.Empty, new MongoDB.Bson.BsonDocument("$set", new MongoDB.Bson.BsonDocument("active", false)), cancellationToken: TestContext.Current.CancellationToken);
        await h.GenerateUpcomingAsync();

        var (_, body) = await h.SendAsync(HttpMethod.Get, "/api/v2/cycles", withProfile: false);

        body.GetProperty("items").EnumerateArray().Should().OnlyContain(c => c.GetProperty("planId").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task The_list_pages_with_limit_and_cursor_also_across_a_negative_index()
    {
        using var h = new GenerationHarness(mongo, "2026-09-20T08:00:00.000Z");
        await h.GenerateUpcomingAsync(); // cycles 0 and 1
        (await h.SendAsync(HttpMethod.Patch, "/api/v2/settings", new { cycleAnchorDate = "2026-09-21" }, asAdmin: true)).Status.Should().Be(HttpStatusCode.OK);
        await h.GenerateUpcomingAsync(); // current is now -1, next 0; cycle 1 stays

        var first = (await h.SendAsync(HttpMethod.Get, "/api/v2/cycles?limit=2")).Body;
        var second = (await h.SendAsync(HttpMethod.Get, $"/api/v2/cycles?limit=2&cursor={Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!)}")).Body;

        first.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("index").GetInt32()).Should().Equal(-1, 0);
        second.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("index").GetInt32()).Should().Equal(1);
        second.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=201", "limit")]
    [InlineData("limit=abc", "limit")]
    [InlineData("limit=-1", "limit")]
    [InlineData("cursor=garbage", "cursor")]
    public async Task A_malformed_limit_or_cursor_is_a_validation_error_on_that_field(string query, string field)
    {
        using var h = new GenerationHarness(mongo);

        var (response, body) = await Send(h, $"/api/v2/cycles?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:validation_error");
        body.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
        ErrorFields(body).Should().Equal(field);
    }

    [Fact]
    public async Task There_is_no_write_route()
    {
        using var h = new GenerationHarness(mongo);

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            var (status, _) = await h.SendAsync(method, "/api/v2/cycles", new { });
            status.Should().Be(HttpStatusCode.MethodNotAllowed, method.Method);
        }
    }

    private static async Task<(HttpResponseMessage Response, JsonElement Body)> Send(GenerationHarness h, string url)
    {
        var (_, body, response) = await h.SendWithResponseAsync(HttpMethod.Get, url, withProfile: false);
        return (response, body);
    }
}
