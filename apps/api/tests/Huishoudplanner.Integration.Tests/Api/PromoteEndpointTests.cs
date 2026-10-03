using System.Net;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Ports the <c>GET /api/promote-suggestions</c> scenarios of apps/server/test/promote.test.ts to <c>GET /api/v2/promote-suggestions</c> on
/// the real host and a real MongoDB replica set, with the moves made through the occurrence endpoints of slice 3.2 (<c>PATCH reschedule</c> is
/// <c>POST .../reschedule</c>, <c>assign</c> is <c>POST .../assignment</c>). Anchor Monday 2026-09-14; the slot is week index 1, Tuesday, for the
/// first person: cycle 0 plans it on 22 Sep, cycle 1 on 20 Oct, cycle 2 on 17 Nov. Not ported: the <c>apply</c> and <c>dismiss</c> routes and their
/// scenarios (they write the plan and the settings and are not part of this read-only slice); the dismissal is arranged by writing the entry
/// the dismiss route would store. New: the open policy, the empty list without moves, that a cycle still to come is ignored and that the read audits nothing.
/// </summary>
public sealed class PromoteEndpointTests(MongoContainerFixture mongo)
{
    private const string Anchor = "2026-09-14";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Arranged : IDisposable
    {
        public Arranged(GenerationHarness h, string planId, string taskId, string p1, string p2)
        {
            H = h;
            PlanId = planId;
            TaskId = taskId;
            P1 = p1;
            P2 = p2;
        }

        public GenerationHarness H { get; }

        public string PlanId { get; }

        public string TaskId { get; }

        public string P1 { get; }

        public string P2 { get; }

        public void Dispose() => H.Dispose();

        public async Task<JsonElement> GetAsync()
        {
            var (status, body) = await H.SendAsync(HttpMethod.Get, "/api/v2/promote-suggestions", withProfile: false);
            status.Should().Be(HttpStatusCode.OK, body.ToString());
            return body.GetProperty("items");
        }

        /// <summary>Drags the occurrence planned in <paramref name="cycle"/> to <paramref name="date"/>, optionally to another person; returns its id.</summary>
        public async Task<string> MoveAsync(int cycle, string date, string? assigneeId = null)
        {
            var planned = DateOnly.Parse(Anchor, System.Globalization.CultureInfo.InvariantCulture).AddDays((cycle * 28) + 7 + 1)
                .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var occurrence = (await H.OccurrencesOfAsync(TaskId)).Single(o => PlannedDay(o) == planned);
            var id = occurrence["_id"].AsObjectId.ToString();
            var moved = await H.SendAsync(HttpMethod.Post, $"/api/v2/occurrences/{id}/reschedule", new { date });
            moved.Status.Should().Be(HttpStatusCode.OK, moved.Body.ToString());
            if (assigneeId is not null)
            {
                var assigned = await H.SendAsync(HttpMethod.Post, $"/api/v2/occurrences/{id}/assignment", new { assigneeId });
                assigned.Status.Should().Be(HttpStatusCode.OK, assigned.Body.ToString());
            }

            return id;
        }

        private static string PlannedDay(BsonDocument occurrence) =>
            TimeZoneInfo.ConvertTime(new DateTimeOffset(occurrence["plannedDate"].ToUniversalTime(), TimeSpan.Zero), TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam"))
                .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<Arranged> ArrangeAsync()
    {
        var h = new GenerationHarness(mongo, $"{Anchor}T06:00:00.000Z");
        var room = await h.SeedRoomAsync("Badkamer");
        var p1 = await h.SeedPersonAsync("Persoon 1");
        var p2 = await h.SeedPersonAsync("Persoon 2");
        var task = await h.NewTaskAsync("Badkamer schoonmaken", room, "4wk", 30);
        var plan = await h.ActivePlanIdAsync();
        await h.PutSlotsAsync(plan, (task, 1, 2, p1));
        await h.GenerateUpcomingAsync();
        return new Arranged(h, plan, task, p1, p2);
    }

    [Fact]
    public async Task DoesNotSuggestWithoutMovesOrAfterASingleMove()
    {
        using var a = await ArrangeAsync();
        (await a.GetAsync()).GetArrayLength().Should().Be(0);

        await a.MoveAsync(0, "2026-09-23");

        (await a.GetAsync()).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task SuggestsTheNewWeekdayWhenTwoConsecutiveCyclesWereMovedTheSameWay()
    {
        using var a = await ArrangeAsync();
        var first = await a.MoveAsync(0, "2026-09-23");
        var second = await a.MoveAsync(1, "2026-10-21");

        var items = await a.GetAsync();

        var suggestion = items.EnumerateArray().Should().ContainSingle().Subject;
        suggestion.GetProperty("planId").GetString().Should().Be(a.PlanId);
        suggestion.GetProperty("taskId").GetString().Should().Be(a.TaskId);
        suggestion.GetProperty("taskName").GetString().Should().Be("Badkamer schoonmaken");
        var from = suggestion.GetProperty("fromSlot");
        (from.GetProperty("weekIndex").GetInt32(), from.GetProperty("weekday").GetInt32(), from.GetProperty("assigneeId").GetString()).Should().Be((1, 2, a.P1));
        suggestion.GetProperty("toWeekday").GetInt32().Should().Be(3);
        suggestion.GetProperty("toAssigneeId").ValueKind.Should().Be(JsonValueKind.Null);
        suggestion.GetProperty("evidence").EnumerateArray().Select(e => e.GetString()).Should().Equal(second, first);
    }

    [Fact]
    public async Task DoesNotSuggestWhenTheMovesWentToDifferentWeekdays()
    {
        using var a = await ArrangeAsync();
        await a.MoveAsync(0, "2026-09-23"); // Wednesday
        await a.MoveAsync(1, "2026-10-22"); // Thursday

        (await a.GetAsync()).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task IncludesTheOtherPersonWhenEveryMoveAlsoWentToThem()
    {
        using var a = await ArrangeAsync();
        await a.MoveAsync(0, "2026-09-24", a.P2);
        await a.MoveAsync(1, "2026-10-22", a.P2);

        var suggestion = (await a.GetAsync()).EnumerateArray().Should().ContainSingle().Subject;

        (suggestion.GetProperty("toWeekday").GetInt32(), suggestion.GetProperty("toAssigneeId").GetString()).Should().Be((4, a.P2));
    }

    [Fact]
    public async Task KeepsADismissedSuggestionAwayUntilThereIsNewerEvidence()
    {
        using var a = await ArrangeAsync();
        await a.MoveAsync(0, "2026-09-23");
        var second = await a.MoveAsync(1, "2026-10-21");
        (await a.GetAsync()).GetArrayLength().Should().Be(1);

        // What POST .../dismiss would store (the route itself is not part of this slice).
        var dismissal = new BsonDocument
        {
            { "planId", ObjectId.Parse(a.PlanId) }, { "taskId", ObjectId.Parse(a.TaskId) }, { "weekIndex", 1 }, { "weekday", 2 },
            { "toWeekday", 3 }, { "toAssigneeId", BsonNull.Value }, { "lastEvidenceId", ObjectId.Parse(second) },
        };
        await a.H.Database.GetCollection<BsonDocument>("settings").UpdateOneAsync(
            new BsonDocument(),
            new BsonDocument("$push", new BsonDocument("dismissedPromotions", dismissal)),
            cancellationToken: Ct);
        (await a.GetAsync()).GetArrayLength().Should().Be(0);

        // A cycle later the same move happens again.
        a.H.Clock.Set("2026-10-12T06:00:00.000Z");
        await a.H.GenerateUpcomingAsync();
        var third = await a.MoveAsync(2, "2026-11-18");

        var evidence = (await a.GetAsync()).EnumerateArray().Single().GetProperty("evidence").EnumerateArray().Select(e => e.GetString()).ToList();
        evidence[0].Should().Be(third);
    }

    [Fact]
    public async Task IgnoresACycleThatIsStillToComeAndUntouched()
    {
        using var a = await ArrangeAsync();
        var first = await a.MoveAsync(0, "2026-09-23");
        var second = await a.MoveAsync(1, "2026-10-21");
        a.H.Clock.Set("2026-10-12T06:00:00.000Z");
        await a.H.GenerateUpcomingAsync(); // cycle 2 now exists, planned for 17 Nov, nobody touched it

        var evidence = (await a.GetAsync()).EnumerateArray().Single().GetProperty("evidence").EnumerateArray().Select(e => e.GetString());

        evidence.Should().Equal(second, first);
    }

    [Fact]
    public async Task AnswersWithoutAProfile_likeTheNodeRoute()
    {
        using var a = await ArrangeAsync();

        var (status, _) = await a.H.SendAsync(HttpMethod.Get, "/api/v2/promote-suggestions", withProfile: false);

        status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WritesNothingAndAuditsNothing()
    {
        using var a = await ArrangeAsync();
        await a.MoveAsync(0, "2026-09-23");
        await a.MoveAsync(1, "2026-10-21");
        var before = await a.H.AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

        await a.GetAsync();
        await a.GetAsync();

        (await a.H.AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(before);
    }
}
