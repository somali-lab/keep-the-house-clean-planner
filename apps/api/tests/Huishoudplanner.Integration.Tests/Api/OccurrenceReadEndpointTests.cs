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
    [InlineData("from=0001-01-01&to=2026-09-14", "from")]
    [InlineData("from=2026-09-14&to=9999-12-31", "to")]
    [InlineData("from=2026-09-14&to=2026-09-20&status=weird", "status")]
    [InlineData("from=2026-09-14&to=2026-09-20&assigneeId=nope", "assigneeId")]
    [InlineData("from=2026-09-14&to=2026-09-20&limit=abc", "limit")]
    [InlineData("from=2026-09-14&to=2026-09-20&limit=501", "limit")]
    [InlineData("from=2026-09-14&to=2026-09-20&cursor=garbage", "cursor")]
    [InlineData("from=2026-09-14&to=2026-09-20&order=sideways", "order")]
    [InlineData("from=2026-09-14&to=2026-09-20&order=ASC", "order")]
    [InlineData("from=2026-09-14&to=2026-09-20&order=", "order")]
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

    private static List<string?> Ids(JsonElement body) => [.. Items(body).Select(o => o.GetProperty("id").GetString())];

    [Fact]
    public async Task List_withoutOrderIsAscendingAndAscExplicitlyIsTheSame()
    {
        var plain = await h.SendAsync(HttpMethod.Get, "/api/v2/occurrences?from=2026-09-14&to=2026-09-20", null, null);
        var asc = await h.SendAsync(HttpMethod.Get, "/api/v2/occurrences?from=2026-09-14&to=2026-09-20&order=asc", null, null);

        Ids(asc.Body).Should().Equal(Ids(plain.Body));
        Items(plain.Body).First().GetProperty("date").GetString().Should().Be("2026-09-14");
    }

    [Fact]
    public async Task List_descIsTheReverseOfAscAcrossPagesAndKeepsTheOrderInItsCursor()
    {
        var asc = await h.SendAsync(HttpMethod.Get, "/api/v2/occurrences?from=2026-09-14&to=2026-09-20&order=asc", null, null);
        var seen = new List<string?>();
        string? cursor = null;
        do
        {
            var url = "/api/v2/occurrences?from=2026-09-14&to=2026-09-20&order=desc&limit=1" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var page = await h.SendAsync(HttpMethod.Get, url, null, null);
            page.Status.Should().Be(HttpStatusCode.OK, page.Body.ToString());
            seen.AddRange(Ids(page.Body));
            cursor = page.Body.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        seen.Should().Equal(Ids(asc.Body).AsEnumerable().Reverse());
    }

    [Fact]
    public async Task List_aCursorOfTheOtherOrderIsRefusedOnTheCursorField()
    {
        var asc = await h.SendAsync(HttpMethod.Get, "/api/v2/occurrences?from=2026-09-14&to=2026-09-20&limit=2", null, null);
        var desc = await h.SendAsync(HttpMethod.Get, "/api/v2/occurrences?from=2026-09-14&to=2026-09-20&limit=2&order=desc", null, null);
        var ascCursor = Uri.EscapeDataString(asc.Body.GetProperty("nextCursor").GetString()!);
        var descCursor = Uri.EscapeDataString(desc.Body.GetProperty("nextCursor").GetString()!);

        var ascOnDesc = await h.SendAsync(HttpMethod.Get, $"/api/v2/occurrences?from=2026-09-14&to=2026-09-20&order=desc&cursor={ascCursor}", null, null);
        var descOnAsc = await h.SendAsync(HttpMethod.Get, $"/api/v2/occurrences?from=2026-09-14&to=2026-09-20&cursor={descCursor}", null, null);

        foreach (var refused in new[] { ascOnDesc, descOnAsc })
        {
            refused.Status.Should().Be(HttpStatusCode.BadRequest);
            Type(refused.Body).Should().Be("urn:huishoudplanner:problem:validation_error");
            refused.Body.GetProperty("errors").GetProperty("cursor")[0].GetString().Should().Be("cursor_order_mismatch");
        }
    }

    [Fact]
    public async Task List_descCombinesWithTheFilters()
    {
        var mine = await h.SendAsync(HttpMethod.Get, $"/api/v2/occurrences?from=2026-09-14&to=2026-09-20&assigneeId={h.P1.Id}&status=open&order=desc", null, null);
        var narrow = await h.SendAsync(HttpMethod.Get, "/api/v2/occurrences?from=2026-09-16&to=2026-09-16&order=desc", null, null);

        Items(mine.Body).Select(o => o.GetProperty("date").GetString()).Should().Equal("2026-09-16", "2026-09-14");
        Items(narrow.Body).Select(o => o.GetProperty("taskNameSnapshot").GetString()).Should().Equal("Wastafel", "Badkamer schoonmaken");
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
