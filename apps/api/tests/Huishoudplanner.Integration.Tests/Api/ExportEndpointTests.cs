using System.Net;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Ports the route and data scenarios of <c>apps/server/test/pdf-export.test.ts</c> to <c>GET /api/v2/export/pdf/*</c> on the real host, a real
/// MongoDB replica set and the real QuestPDF renderer: the content type and the quoted <c>Content-Disposition</c>, the page counts, a rescheduled task
/// on its new day, the blank checklist (a completed task is printed without any mark), header, theme, rooms, person groups and footer, the 409
/// <c>weeks_not_generated</c> with the missing ISO weeks, the query validation, the day, due and task sheets, the translated chrome with untouched
/// names, a one-off task (with and without a room) and the unknown-room fallback. The household is the one of the Node test: anchor Monday
/// 2026-09-14 (2026-W38), the nightly run generates W38 to W45, "Ramen lappen" is dragged to Saturday and Monday's bathroom is checked off. The
/// shared household is only read, so the tests do not depend on their order. Not ported: the three helper tests on <c>scheduleDocument</c> and
/// <c>escapeHtml</c> (HTML internals of the Node renderer, replaced by the view-model and renderer tests of slice 6.4a), and the file-name builder
/// (covered by <c>SheetBuilderTests</c>).
/// </summary>
public sealed class ExportEndpointTests(ExportEndpointTests.Household household) : IClassFixture<ExportEndpointTests.Household>
{
    private const string Pdf = "/api/v2/export/pdf";

    private static readonly string[] WeekThemes = ["", "Keuken", "", ""];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Two people, two rooms, three tasks and a plan with a theme in week 2, generated, with one task moved and one checked off.</summary>
    public sealed class Household(MongoContainerFixture mongo) : IAsyncLifetime
    {
        public MongoContainerFixture Mongo => mongo;

        public GenerationHarness H { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            H = new GenerationHarness(mongo, "2026-09-14T06:00:00.000Z");
            await ArrangeAsync(H);
        }

        public ValueTask DisposeAsync()
        {
            H.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task ArrangeAsync(GenerationHarness h)
    {
        var p1 = await h.SeedPersonAsync("Persoon 1");
        var p2 = await h.SeedPersonAsync("Persoon 2");
        var badkamer = await h.SeedRoomAsync("Badkamer");
        var keuken = await h.SeedRoomAsync("Keuken");
        var weekly = await h.NewTaskAsync("Badkamer schoonmaken", badkamer, "1w", 30);
        var twice = await h.NewTaskAsync("Wastafel", badkamer, "2w", 10);
        var monthly = await h.NewTaskAsync("Ramen lappen", keuken, "4wk", 45);
        var plan = await h.ActivePlanIdAsync();
        await h.PutSlotsAsync(
            plan,
            [
                .. Enumerable.Range(0, 4).Select(w => (weekly, w, 1, (string?)p1)),
                .. Enumerable.Range(0, 4).Select(w => (twice, w, 3, (string?)null)),
                (monthly, 1, 5, p2),
            ]);
        var themes = await h.SendAsync(HttpMethod.Patch, $"/api/v2/cycle-plans/{plan}", new { weekThemes = WeekThemes });
        themes.Status.Should().Be(HttpStatusCode.OK, themes.Body.ToString());
        await h.GenerateUpcomingAsync();

        // Drag "Ramen lappen" from Friday 25 Sep to Saturday 26 Sep.
        var ramen = (await h.OccurrencesOfAsync(monthly)).First();
        var moved = await h.SendAsync(HttpMethod.Post, $"/api/v2/occurrences/{ramen["_id"].AsObjectId}/reschedule", new { date = "2026-09-26" });
        moved.Status.Should().Be(HttpStatusCode.OK, moved.Body.ToString());

        // Check off Monday's bathroom: the sheet must still show it as a blank line.
        var bath = (await h.OccurrencesOfAsync(weekly)).First();
        var done = await h.SendAsync(HttpMethod.Post, $"/api/v2/occurrences/{bath["_id"].AsObjectId}/complete", new { completedBy = p1 });
        done.Status.Should().Be(HttpStatusCode.OK, done.Body.ToString());
    }

    private async Task<(HttpResponseMessage Response, byte[] Body)> GetAsync(string url, GenerationHarness? harness = null)
    {
        var response = await (harness ?? household.H).Client.GetAsync(url, Ct);
        return (response, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    private async Task<PdfProbe> ReadPdfAsync(string url, GenerationHarness? harness = null)
    {
        var (response, body) = await GetAsync(url, harness);
        response.StatusCode.Should().Be(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetString(body));
        return PdfProbe.Read(body);
    }

    private static string Disposition(HttpResponseMessage response) => string.Join(',', response.Content.Headers.GetValues("Content-Disposition"));

    private async Task<JsonElement> ProblemAsync(string url, HttpStatusCode status)
    {
        var (response, body) = await GetAsync(url);
        response.StatusCode.Should().Be(status, url);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    // ---- GET /export/pdf/schedule

    [Fact]
    public async Task The_schedule_is_a_downloadable_PDF_with_a_predictable_quoted_file_name()
    {
        var (response, body) = await GetAsync($"{Pdf}/schedule?fromWeek=2026-W38&weeks=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        Disposition(response).Should().Be("attachment; filename=\"huishoudschema-2026-w38-w39.pdf\"");
        System.Text.Encoding.ASCII.GetString(body, 0, 5).Should().Be("%PDF-");
    }

    [Theory]
    [InlineData(1, "portrait", 1)]
    [InlineData(2, "portrait", 2)]
    [InlineData(4, "portrait", 4)]
    [InlineData(2, "landscape", 1)]
    public async Task The_page_count_follows_the_weeks_and_the_orientation(int weeks, string orientation, int pages)
    {
        var pdf = await ReadPdfAsync($"{Pdf}/schedule?fromWeek=2026-W38&weeks={weeks}&orientation={orientation}");

        pdf.Pages.Should().Be(pages);
    }

    [Fact]
    public async Task A_rescheduled_occurrence_is_printed_on_its_new_day()
    {
        var pdf = await ReadPdfAsync($"{Pdf}/schedule?fromWeek=2026-W39&weeks=1");

        pdf.Between("zaterdag", "zondag").Should().Contain("Ramen lappen");
        pdf.Between("vrijdag", "zaterdag").Should().NotContain("Ramen lappen");
    }

    [Fact]
    public async Task The_sheet_is_a_blank_checklist_a_completed_occurrence_is_listed_without_any_mark()
    {
        var pdf = await ReadPdfAsync($"{Pdf}/schedule?fromWeek=2026-W38&weeks=1");

        pdf.Between("maandag", "dinsdag").Replace('\n', ' ').Should().Contain("Badkamer schoonmaken");
        foreach (var marker in new[] { "done", "gedaan", "afgevinkt door", "overgeslagen", "✓", "☑", "✔" })
        {
            pdf.Text.ToLowerInvariant().Should().NotContain(marker);
        }
    }

    [Fact]
    public async Task The_page_prints_header_theme_rooms_person_groups_and_the_footer_with_the_paper_note()
    {
        var pdf = await ReadPdfAsync($"{Pdf}/schedule?fromWeek=2026-W39&weeks=1&totals=true");

        pdf.Text.Should().Contain("Week 2 van de cyclus");
        pdf.Text.Should().Contain("ma 21-09 t/m zo 27-09-2026");
        pdf.Text.Should().Contain("Thema: Keuken");
        pdf.Text.Should().Contain("Keuken"); // the room of "Ramen lappen"
        pdf.Text.Should().Contain("Persoon 1");
        System.Text.RegularExpressions.Regex.Matches(pdf.Text, "Persoon 1").Should().HaveCount(1);
        pdf.Text.Should().Contain("Wie dan ook");
        pdf.Text.Should().Contain("Gegenereerd op 14-09-2026 08:00");
        pdf.Text.Should().Contain("Afvinken op papier wordt niet automatisch in de app verwerkt.");
        pdf.Text.Should().Contain("45 min");
    }

    [Fact]
    public async Task The_language_parameter_translates_the_chrome_and_keeps_the_names()
    {
        var pdf = await ReadPdfAsync($"{Pdf}/schedule?fromWeek=2026-W38&weeks=1&language=en");

        pdf.Text.Should().Contain("Week 1 of the cycle");
        pdf.Text.Should().Contain("Generated on 14-09-2026 08:00");
        pdf.Text.Should().Contain("Badkamer schoonmaken");
        pdf.Text.Should().NotContain("Gegenereerd");
    }

    [Fact]
    public async Task Weeks_that_have_not_been_generated_are_refused_with_their_ISO_labels()
    {
        var single = await ProblemAsync($"{Pdf}/schedule?fromWeek=2026-W46&weeks=1", HttpStatusCode.Conflict);
        var partial = await ProblemAsync($"{Pdf}/schedule?fromWeek=2026-W44&weeks=4", HttpStatusCode.Conflict);

        single.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:weeks_not_generated");
        single.GetProperty("weeks").EnumerateArray().Select(w => w.GetString()).Should().Equal("2026-W46");
        partial.GetProperty("weeks").EnumerateArray().Select(w => w.GetString()).Should().Equal("2026-W46", "2026-W47");
    }

    [Theory]
    [InlineData("fromWeek=2026-W38&weeks=3", "weeks", "invalid_enum")]
    [InlineData("fromWeek=2026-38&weeks=1", "fromWeek", "invalid_iso_week")]
    [InlineData("weeks=1", "fromWeek", "required")]
    [InlineData("fromWeek=2026-W38", "weeks", "required")]
    [InlineData("fromWeek=2026-W38&weeks=1&orientation=diagonal", "orientation", "invalid_enum")]
    [InlineData("fromWeek=2026-W38&weeks=1&totals=yes", "totals", "invalid_enum")]
    [InlineData("fromWeek=2026-W38&weeks=1&language=de", "language", "invalid_enum")]
    [InlineData("fromWeek=2027-W53&weeks=1", "fromWeek", "invalid_iso_week")]
    public async Task A_malformed_query_is_a_validation_error_keyed_by_the_field(string query, string field, string code)
    {
        var problem = await ProblemAsync($"{Pdf}/schedule?{query}", HttpStatusCode.BadRequest);

        problem.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:validation_error");
        problem.GetProperty("errors").GetProperty(field).EnumerateArray().Select(e => e.GetString()).Should().Equal(code);
    }

    [Fact]
    public async Task Every_problem_of_a_request_is_reported_at_once()
    {
        var problem = await ProblemAsync($"{Pdf}/schedule?weeks=3&orientation=diagonal", HttpStatusCode.BadRequest);

        problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("fromWeek", "weeks", "orientation");
    }

    // ---- GET /export/pdf/day and /due

    [Fact]
    public async Task A_single_day_is_one_page_with_only_that_days_tasks()
    {
        var (response, body) = await GetAsync($"{Pdf}/day?date=2026-09-16");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Disposition(response).Should().Be("attachment; filename=\"huishoudschema-2026-09-16.pdf\"");
        var pdf = PdfProbe.Read(body);
        pdf.Pages.Should().Be(1);
        pdf.Text.Should().Contain("Dagschema woensdag 16-09-2026");
        pdf.Text.Should().Contain("Wastafel");
        pdf.Text.Should().NotContain("Badkamer schoonmaken");
    }

    [Fact]
    public async Task A_day_in_a_week_that_has_not_been_generated_is_refused()
    {
        var problem = await ProblemAsync($"{Pdf}/day?date=2026-11-16", HttpStatusCode.Conflict);

        problem.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:weeks_not_generated");
        problem.GetProperty("weeks").EnumerateArray().Select(w => w.GetString()).Should().Equal("2026-W47");
    }

    [Theory]
    [InlineData("date=2026-9-16", "invalid_day_key")]
    [InlineData("", "required")]
    public async Task A_malformed_day_is_a_validation_error(string query, string code)
    {
        var problem = await ProblemAsync($"{Pdf}/day?{query}", HttpStatusCode.BadRequest);

        problem.GetProperty("errors").GetProperty("date").EnumerateArray().Select(e => e.GetString()).Should().Equal(code);
    }

    [Fact]
    public async Task The_due_list_says_so_when_nothing_is_due_on_the_day_the_tasks_were_created()
    {
        var (response, body) = await GetAsync($"{Pdf}/due");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Disposition(response).Should().Be("attachment; filename=\"achterstand-2026-09-14.pdf\"");
        var pdf = PdfProbe.Read(body);
        pdf.Pages.Should().Be(1);
        pdf.Text.Should().Contain("Achterstand");
        pdf.Text.Should().Contain("Geen achterstand.");
    }

    [Fact]
    public async Task The_due_list_prints_a_task_that_is_overdue_with_its_state_in_words()
    {
        using var h = new GenerationHarness(mongo: Mongo, "2026-09-14T06:00:00.000Z");
        var room = await h.SeedRoomAsync("Badkamer");
        await h.NewTaskAsync("Voegen schoonmaken", room, "1w", 20);
        h.Clock.Set("2026-10-30T06:00:00.000Z");

        var (response, body) = await GetAsync($"{Pdf}/due", h);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Disposition(response).Should().Be("attachment; filename=\"achterstand-2026-10-30.pdf\"");
        var pdf = PdfProbe.Read(body);
        pdf.Text.Should().Contain("Voegen schoonmaken");
        pdf.Text.Should().Contain("Flink achter");
        pdf.Text.Should().NotContain("Geen achterstand.");
    }

    // ---- GET /export/pdf/tasks

    [Fact]
    public async Task The_task_list_has_every_task_with_room_interval_and_duration_on_one_page()
    {
        var (response, body) = await GetAsync($"{Pdf}/tasks");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        Disposition(response).Should().Be("attachment; filename=\"huishoudtaken.pdf\"");
        var pdf = PdfProbe.Read(body);
        pdf.Pages.Should().Be(1);
        pdf.Text.Should().Contain("Alle huishoudtaken");
        pdf.Text.Should().Contain("Badkamer schoonmaken");
        pdf.Text.Should().Contain("1x per week");
        pdf.Text.Should().Contain("30 min");
        pdf.Text.Should().Contain("Ramen lappen");
        pdf.Text.Should().Contain("Keuken");
        pdf.Text.Should().Contain("1x per 4 weken");
        pdf.Text.Should().Contain("45 min");
    }

    [Fact]
    public async Task The_task_list_translates_the_chrome_but_keeps_task_and_room_names()
    {
        var pdf = await ReadPdfAsync($"{Pdf}/tasks?language=en");

        pdf.Text.Should().Contain("All household tasks");
        pdf.Text.Should().Contain("Room");
        pdf.Text.Should().Contain("Duration");
        pdf.Text.Should().Contain("Badkamer schoonmaken");
        pdf.Text.Should().Contain("Badkamer");
        pdf.Text.Should().NotContain("Alle huishoudtaken");
    }

    [Fact]
    public async Task A_task_whose_room_no_longer_exists_is_printed_under_the_unknown_room_text_of_the_language()
    {
        using var h = new GenerationHarness(Mongo, "2026-09-14T06:00:00.000Z");
        var room = await h.SeedRoomAsync("Zolder");
        await h.NewTaskAsync("Dozen sorteren", room, "1w", 15);
        await h.Database.GetCollection<BsonDocument>("rooms").DeleteOneAsync(new BsonDocument("_id", ObjectId.Parse(room)), Ct);

        var dutch = await ReadPdfAsync($"{Pdf}/tasks", h);
        var english = await ReadPdfAsync($"{Pdf}/tasks?language=en", h);

        dutch.Text.Should().Contain("Onbekende ruimte");
        english.Text.Should().Contain("Unknown room");
        english.Text.Should().Contain("Dozen sorteren");
    }

    // ---- one-off tasks (taskId null)

    [Fact]
    public async Task A_one_off_task_is_printed_from_its_snapshots_with_or_without_a_room_and_is_not_in_the_task_list()
    {
        using var h = new GenerationHarness(Mongo, "2026-09-14T06:00:00.000Z");
        var p2 = await h.SeedPersonAsync("Persoon 2");
        var keuken = await h.SeedRoomAsync("Keuken");
        await h.NewTaskAsync("Wastafel", keuken, "2w", 10);
        await h.GenerateUpcomingAsync();
        // What POST /occurrences/one-off (slice 3.3) stores: no task, adhoc origin, the room and name as snapshots.
        await InsertOneOffAsync(h, "Kast ophalen", 25, null, null, null);
        await InsertOneOffAsync(h, "Magnetron ontkalken", 15, keuken, "Keuken", p2);

        var week = await ReadPdfAsync($"{Pdf}/schedule?fromWeek=2026-W38&weeks=1", h);
        var day = await ReadPdfAsync($"{Pdf}/day?date=2026-09-17", h);
        var tasks = await ReadPdfAsync($"{Pdf}/tasks", h);

        var thursday = week.Between("donderdag", "vrijdag").Replace('\n', ' ');
        thursday.Should().Contain("Kast ophalen");
        thursday.Should().Contain("Magnetron ontkalken");
        thursday.Should().Contain("Keuken");
        day.Text.Should().Contain("Kast ophalen");
        tasks.Text.Should().NotContain("Kast ophalen");
    }

    private static async Task InsertOneOffAsync(GenerationHarness h, string name, int minutes, string? roomId, string? roomName, string? assigneeId)
    {
        var day = new BsonDateTime(new DateTime(2026, 9, 16, 22, 0, 0, DateTimeKind.Utc)); // Thursday 17 Sep, 00:00 in Amsterdam
        var at = new BsonDateTime(new DateTime(2026, 9, 14, 6, 0, 0, DateTimeKind.Utc));
        await h.Occurrences.InsertOneAsync(
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "taskId", BsonNull.Value }, { "date", day }, { "plannedDate", day },
                { "assigneeId", assigneeId is null ? BsonNull.Value : ObjectId.Parse(assigneeId) },
                { "status", "open" }, { "durationMinutesSnapshot", minutes }, { "taskNameSnapshot", name },
                { "roomIdSnapshot", roomId is null ? BsonNull.Value : ObjectId.Parse(roomId) },
                { "roomNameSnapshot", roomName is null ? BsonNull.Value : roomName },
                { "origin", "adhoc" }, { "createdAt", at }, { "updatedAt", at },
            },
            cancellationToken: Ct);
    }

    private MongoContainerFixture Mongo => household.Mongo;

    // ---- policy

    [Fact]
    public async Task The_exports_need_no_profile()
    {
        var (response, _) = await GetAsync($"{Pdf}/tasks");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.RequestMessage!.Headers.Contains("X-Profile-Id").Should().BeFalse();
    }
}
