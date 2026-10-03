using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Integration.Tests.Fixtures;
using OneOf;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>GET /api/v2/meta/limits and GET /api/v2/calendar through the real pipeline; both are open reads (no profile header).</summary>
public sealed class MetaAndCalendarEndpointTests
{
    private sealed class FakeAnchor(OneOf<DateOnly, SettingsMissing, PortError> result) : ForReadingCycleAnchor
    {
        public Task<OneOf<DateOnly, SettingsMissing, PortError>> GetAnchorAsync(CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private static ApiFactory Factory(OneOf<DateOnly, SettingsMissing, PortError>? anchor = null) =>
        ApiFactory.WithoutDatabase().WithPort<ForReadingCycleAnchor>(new FakeAnchor(anchor ?? new DateOnly(2026, 9, 14)));

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Limits_returnsTheGroupedDocumentWithoutAProfile()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v2/meta/limits", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await BodyOf(response);
        body.GetProperty("tasks").GetProperty("maxPoints").GetInt32().Should().Be(1000);
        body.GetProperty("calendar").GetProperty("maxRangeDays").GetInt32().Should().Be(371);
        body.GetProperty("badges").GetProperty("imageTypes").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("image/png", "image/jpeg", "image/webp");
        var intervals = body.GetProperty("defaults").GetProperty("intervals");
        intervals.GetArrayLength().Should().Be(7);
        intervals[0].GetProperty("key").GetString().Should().Be("daily");
        intervals[6].GetProperty("perCycle").ValueKind.Should().Be(JsonValueKind.Null);
        body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "calendar", "tasks", "points", "bonuses", "rewards", "badges", "notifications", "ai", "audit", "statistics", "defaults");
    }

    [Fact]
    public async Task Calendar_returnsOneEntryPerDayWithCycleWeekAndIsoWeek()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v2/calendar?from=2026-10-11&to=2026-10-12", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await BodyOf(response);
        body.GetProperty("timezone").GetString().Should().Be("Europe/Amsterdam");
        var days = body.GetProperty("days");
        days.GetArrayLength().Should().Be(2);
        days[0].GetProperty("dayKey").GetString().Should().Be("2026-10-11");
        days[0].GetProperty("weekday").GetInt32().Should().Be(0);
        days[0].GetProperty("cycleIndex").GetInt32().Should().Be(0);
        days[0].GetProperty("weekIndex").GetInt32().Should().Be(3);
        days[0].GetProperty("isoWeek").GetString().Should().Be("2026-W41");
        days[0].GetProperty("weekStart").GetString().Should().Be("2026-10-05");
        days[1].GetProperty("dayKey").GetString().Should().Be("2026-10-12");
        days[1].GetProperty("cycleIndex").GetInt32().Should().Be(1);
        days[1].GetProperty("weekIndex").GetInt32().Should().Be(0);
        days[1].GetProperty("weekStart").GetString().Should().Be("2026-10-12");
    }

    [Fact]
    public async Task Calendar_acrossTheDstSwitchHasOneEntryForEveryCalendarDay()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var body = await BodyOf(await client.GetAsync("/api/v2/calendar?from=2026-03-28&to=2026-03-30", TestContext.Current.CancellationToken));

        body.GetProperty("days").EnumerateArray().Select(d => d.GetProperty("dayKey").GetString())
            .Should().Equal("2026-03-28", "2026-03-29", "2026-03-30");
    }

    [Theory]
    [InlineData("", "from,to")]
    [InlineData("?from=2026-10-01", "to")]
    [InlineData("?to=2026-10-01", "from")]
    [InlineData("?from=2026-02-30&to=2026-10-01", "from")]
    [InlineData("?from=2026-10-02&to=2026-10-01", "from")]
    [InlineData("?from=2026-01-01&to=2027-01-07", "to")]
    public async Task Calendar_withInvalidInput_returnsValidationProblemDetails(string query, string fields)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v2/calendar" + query, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await BodyOf(response);
        body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:validation_error");
        body.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(fields.Split(','));
    }

    [Fact]
    public async Task Calendar_atTheMaximumRange_isAccepted()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var body = await BodyOf(await client.GetAsync("/api/v2/calendar?from=2026-01-01&to=2027-01-06", TestContext.Current.CancellationToken));

        body.GetProperty("days").GetArrayLength().Should().Be(371);
    }

    [Fact]
    public async Task Calendar_withoutSettings_returns500SettingsMissing()
    {
        using var factory = Factory(new SettingsMissing());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v2/calendar?from=2026-10-01&to=2026-10-02", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await BodyOf(response)).GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:settings_missing");
    }

    [Fact]
    public async Task Calendar_whenTheAnchorPortFails_returns500InternalErrorWithoutTheReason()
    {
        using var factory = Factory(new PortError("secret reason"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v2/calendar?from=2026-10-01&to=2026-10-02", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.Should().Contain("urn:huishoudplanner:problem:internal_error").And.NotContain("secret reason");
    }

    [Fact]
    public async Task Calendar_withTheDefaultComposition_answersSettingsMissingUntilTheSettingsAdapterExists()
    {
        using var factory = ApiFactory.WithoutDatabase();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v2/calendar?from=2026-10-01&to=2026-10-02", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }
}
