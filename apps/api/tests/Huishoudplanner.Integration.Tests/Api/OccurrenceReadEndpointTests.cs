using System.Net;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// <c>GET /api/v2/occurrences</c> and <c>GET /api/v2/occurrences/{id}</c> (ports of "GET /api/occurrences" of <c>occurrences.test.ts</c>) on the
/// real host: day keys, <c>isOverdue</c>, <c>movedFrom</c>, filters, query validation, paging, and the cycle and week index of every view.
/// Generated on Monday 14 Sep, the tests run on Wednesday 16 Sep 10:00 Amsterdam.
/// </summary>
public sealed class OccurrenceReadEndpointTests(OccurrenceHarness h) : IClassFixture<OccurrenceHarness>
{
    private static string Type(JsonElement problem) => problem.GetProperty("type").GetString()!;

    private static List<JsonElement> Items(JsonElement body) => [.. body.GetProperty("items").EnumerateArray()];

    [Fact]
    public async Task List_returnsDayKeysWithIsOverdueAndMovedFromForARange()
    {
        var response = await h.SendAsync(HttpMethod.Get, "/api/v2/occurrences?from=2026-09-14&to=2026-09-20", null, null);

        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        var week = Items(response.Body).ToList();
        week.Select(o => (o.GetProperty("date").GetString(), o.GetProperty("taskNameSnapshot").GetString())).Should().Equal(
            ("2026-09-14", "Badkamer schoonmaken"),
            ("2026-09-16", "Badkamer schoonmaken"),
            ("2026-09-16", "Wastafel"),
            ("2026-09-17", "Wastafel"));
        week.Select(o => o.GetProperty("isOverdue").GetBoolean()).Should().Equal(true, false, false, false);
        week.Should().OnlyContain(o => o.GetProperty("movedFrom").ValueKind == JsonValueKind.Null && o.GetProperty("plannedDate").GetString() == o.GetProperty("date").GetString());
        response.Body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task List_carriesTheCycleAndWeekIndexOfEveryDay()
    {
        var response = await h.SendAsync(HttpMethod.Get, "/api/v2/occurrences?from=2026-09-14&to=2026-10-19", null, null);

        var byDay = Items(response.Body).GroupBy(o => o.GetProperty("date").GetString()!).ToDictionary(g => g.Key, g => g.First());
        (byDay["2026-09-14"].GetProperty("cycleIndex").GetInt32(), byDay["2026-09-14"].GetProperty("weekIndex").GetInt32()).Should().Be((0, 0));
        (byDay["2026-10-08"].GetProperty("cycleIndex").GetInt32(), byDay["2026-10-08"].GetProperty("weekIndex").GetInt32()).Should().Be((0, 3));
        (byDay["2026-10-19"].GetProperty("cycleIndex").GetInt32(), byDay["2026-10-19"].GetProperty("weekIndex").GetInt32()).Should().Be((1, 1));
    }

    [Fact]
    public async Task List_filtersByAssigneeAndStatus()
    {
        var mine = await h.SendAsync(HttpMethod.Get, $"/api/v2/occurrences?from=2026-09-14&to=2026-09-20&assigneeId={h.P2.Id}", null, null);
        var done = await h.SendAsync(HttpMethod.Get, "/api/v2/occurrences?from=2026-09-14&to=2026-09-14&status=done", null, null);

        Items(mine.Body).Select(o => o.GetProperty("date").GetString()).Should().Equal("2026-09-17");
        Items(done.Body).Should().BeEmpty();
    }

    [Theory]
    [InlineData("from=2026-09-20&to=2026-09-14", "from")]
    [InlineData("from=2026-9-1&to=2026-09-14", "from")]
    [InlineData("from=2026-09-14", "to")]
    [InlineData("from=2026-09-14&to=2026-09-20&status=weird", "status")]
    [InlineData("from=2026-09-14&to=2026-09-20&assigneeId=nope", "assigneeId")]
    [InlineData("from=2026-09-14&to=2026-09-20&limit=abc", "limit")]
    [InlineData("from=2026-09-14&to=2026-09-20&limit=501", "limit")]
    [InlineData("from=2026-09-14&to=2026-09-20&cursor=garbage", "cursor")]
    public async Task List_aBadQueryIsAFieldKeyedValidationError(string query, string field)
    {
        var response = await h.SendAsync(HttpMethod.Get, $"/api/v2/occurrences?{query}", null, null);

        response.Status.Should().Be(HttpStatusCode.BadRequest);
        Type(response.Body).Should().Be("urn:huishoudplanner:problem:validation_error");
        response.Body.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue(response.Body.ToString());
    }

    [Fact]
    public async Task List_pagesWithACursor()
    {
        var first = await h.SendAsync(HttpMethod.Get, "/api/v2/occurrences?from=2026-09-14&to=2026-09-20&limit=3", null, null);
        var cursor = first.Body.GetProperty("nextCursor").GetString();
        var second = await h.SendAsync(HttpMethod.Get, $"/api/v2/occurrences?from=2026-09-14&to=2026-09-20&limit=3&cursor={Uri.EscapeDataString(cursor!)}", null, null);

        Items(first.Body).Should().HaveCount(3);
        cursor.Should().NotBeNullOrEmpty();
        Items(second.Body).Should().ContainSingle();
        second.Body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        Items(first.Body).Concat(Items(second.Body)).Select(o => o.GetProperty("id").GetString()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Get_returnsOneOccurrenceWithItsViewFields()
    {
        var id = await h.IdOfAsync(h.Weekly, "2026-09-14");

        var response = await h.SendAsync(HttpMethod.Get, $"/api/v2/occurrences/{id}", null, null);

        response.Status.Should().Be(HttpStatusCode.OK);
        var o = response.Body;
        (o.GetProperty("id").GetString(), o.GetProperty("status").GetString(), o.GetProperty("origin").GetString(), o.GetProperty("isOverdue").GetBoolean()).Should().Be((id, "open", "generated", true));
        (o.GetProperty("assigneeId").GetString(), o.GetProperty("roomNameSnapshot").GetString(), o.GetProperty("durationMinutesSnapshot").GetInt32()).Should().Be((h.P1.Id, "Badkamer", 30));
        o.GetProperty("recordedDone").GetBoolean().Should().BeFalse();
        o.TryGetProperty("warnings", out _).Should().BeFalse("only the answers of reschedule and assignment carry warnings");
    }

    [Fact]
    public async Task Get_anUnknownIdIs404AndAMalformedIdIs400()
    {
        var unknown = await h.SendAsync(HttpMethod.Get, "/api/v2/occurrences/0123456789abcdef01234567", null, null);
        var bad = await h.SendAsync(HttpMethod.Get, "/api/v2/occurrences/nope", null, null);

        unknown.Status.Should().Be(HttpStatusCode.NotFound);
        Type(unknown.Body).Should().Be("urn:huishoudplanner:problem:not_found");
        bad.Status.Should().Be(HttpStatusCode.BadRequest);
        bad.Body.GetProperty("errors").GetProperty("id")[0].GetString().Should().Be("invalid_object_id");
    }
}
