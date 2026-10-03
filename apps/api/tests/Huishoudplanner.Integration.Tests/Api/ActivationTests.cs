using System.Net;
using System.Text.Json;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Ports the activation scenarios of apps/server/test/generation.test.ts (the <c>activate</c> block) and of cyclePlans.test.ts (the preview and
/// the preview token) against the real host and a real MongoDB replica set: the preview, the token and its recheck inside the transaction, the
/// mid-cycle activation, the audit entries, and two concurrent activations (the guard document of ADR-0008 as amended). The activation of an AI
/// draft (ai-draft.test.ts) is in <see cref="AiProposalEndpointTests"/>. Not ported: the occurrence actions that the Node tests use to
/// arrange state (complete, skip, reschedule belong to slice 3.2) are written straight to the collection instead.
/// </summary>
public sealed class ActivationTests(MongoContainerFixture mongo)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Blocked = ["2026-09-16", "2026-09-23", "2026-09-30"];

    private sealed record Arranged(GenerationHarness H, string Room, string PlanId) : IDisposable
    {
        public void Dispose() => H.Dispose();
    }

    private async Task<Arranged> ArrangeAsync()
    {
        var h = new GenerationHarness(mongo);
        var room = await h.SeedRoomAsync("Badkamer");
        return new Arranged(h, room, await h.ActivePlanIdAsync());
    }

    private static string Preview(string planId) => $"/api/v2/cycle-plans/{planId}/activation-preview";

    private static string Activation(string planId) => $"/api/v2/cycle-plans/{planId}/activation";

    private static async Task<string> NewPlanAsync(GenerationHarness h, string name, string? copyFrom = null)
    {
        var created = await h.SendAsync(HttpMethod.Post, "/api/v2/cycle-plans", copyFrom is null ? new { name } : new { name, copyFromId = copyFrom });
        created.Status.Should().Be(HttpStatusCode.Created, created.Body.ToString());
        return created.Body.GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> PreviewOkAsync(GenerationHarness h, string planId)
    {
        var preview = await h.SendAsync(HttpMethod.Get, Preview(planId));
        preview.Status.Should().Be(HttpStatusCode.OK, preview.Body.ToString());
        return preview.Body;
    }

    private static List<string> Ids(JsonElement list) => [.. list.EnumerateArray().Select(i => i.GetProperty("occurrenceId").GetString()!)];

    private static List<string> Dates(JsonElement list) => [.. list.EnumerateArray().Select(i => i.GetProperty("date").GetString()!)];

    private static void ShouldBeProblem(HttpResponseMessage response, JsonElement body, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        body.GetProperty("type").GetString().Should().Be($"urn:huishoudplanner:problem:{code}");
    }

    private static Task<UpdateResult> SetAsync(GenerationHarness h, string id, BsonDocument set) =>
        h.Occurrences.UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(id)), new BsonDocument("$set", set), cancellationToken: Ct);

    private static string IdOf(BsonDocument occurrence) => occurrence["_id"].AsObjectId.ToString();

    private static BsonDocument On(IEnumerable<BsonDocument> occurrences, string day) => occurrences.Single(o => GenerationHarness.DayOf(o) == day);

    [Fact]
    public async Task The_preview_does_not_add_a_generated_insert_that_a_preserved_occurrence_blocks()
    {
        using var a = await ArrangeAsync();
        var task = await a.H.NewTaskAsync("Ramen", a.Room, "1w");
        await a.H.PutSlotsAsync(a.PlanId, Enumerable.Range(0, 4).Select(w => (task, w, 3, (string?)null)).ToArray());
        var occurrences = await a.H.OccurrencesOfAsync(task);
        var done = On(occurrences, "2026-09-16");
        var skipped = On(occurrences, "2026-09-23");
        var moved = On(occurrences, "2026-09-30");
        await SetAsync(a.H, IdOf(done), new BsonDocument { { "status", "done" }, { "completedAt", new BsonDateTime(a.H.Clock.GetUtcNow().UtcDateTime) } });
        await SetAsync(a.H, IdOf(skipped), new BsonDocument("status", "skipped"));
        await SetAsync(a.H, IdOf(moved), new BsonDocument("date", new BsonDateTime(DateTime.Parse("2026-10-01T22:00:00Z", null, System.Globalization.DateTimeStyles.AdjustToUniversal))));
        var copy = await NewPlanAsync(a.H, "Dezelfde slots", a.PlanId);

        var preview = await PreviewOkAsync(a.H, copy);

        Ids(preview.GetProperty("preserved").GetProperty("done")).Should().Contain(IdOf(done));
        Ids(preview.GetProperty("preserved").GetProperty("skipped")).Should().Contain(IdOf(skipped));
        Ids(preview.GetProperty("preserved").GetProperty("moved")).Should().Contain(IdOf(moved));
        Dates(preview.GetProperty("added")).Should().NotContain(Blocked);

        var activated = await a.H.SendAsync(HttpMethod.Post, Activation(copy), new { previewToken = preview.GetProperty("previewToken").GetString() });

        activated.Status.Should().Be(HttpStatusCode.OK, activated.Body.ToString());
        var fresh = await a.H.OccurrencesOfAsync(extra: new BsonDocument("planId", ObjectId.Parse(copy)));
        GenerationHarness.DaysOf(fresh).Should().Equal(Dates(preview.GetProperty("added")).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_preview_writes_nothing_and_needs_a_planner()
    {
        using var a = await ArrangeAsync();
        var plan = await NewPlanAsync(a.H, "Nieuw");
        var auditBefore = await a.H.AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

        var first = await a.H.SendAsync(HttpMethod.Get, Preview(plan));
        var anonymous = await a.H.SendWithResponseAsync(HttpMethod.Get, Preview(plan), withProfile: false);
        var unknown = await a.H.SendWithResponseAsync(HttpMethod.Get, Preview("0123456789abcdef01234567"));
        var malformed = await a.H.SendWithResponseAsync(HttpMethod.Get, Preview("nope"));

        first.Status.Should().Be(HttpStatusCode.OK);
        first.Body.GetProperty("planId").GetString().Should().Be(plan);
        first.Body.GetProperty("previewToken").GetString().Should().MatchRegex("^[a-f0-9]{64}$");
        first.Body.GetProperty("asOfDate").GetString().Should().Be("2026-09-14");
        ShouldBeProblem(anonymous.Response, anonymous.Body, HttpStatusCode.BadRequest, "profile_required");
        ShouldBeProblem(unknown.Response, unknown.Body, HttpStatusCode.NotFound, "not_found");
        ShouldBeProblem(malformed.Response, malformed.Body, HttpStatusCode.BadRequest, "validation_error");
        (await a.H.AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(auditBefore, "previewing audits nothing");
        (await a.H.Cycles.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(0, "previewing creates no cycles");
    }

    [Fact]
    public async Task A_stale_token_is_409_stale_activation_preview_and_leaves_everything_untouched()
    {
        using var a = await ArrangeAsync();
        var task = await a.H.NewTaskAsync("Ramen", a.Room, "1w");
        var plan = await NewPlanAsync(a.H, "Nieuw");
        var first = await PreviewOkAsync(a.H, plan);
        var oldToken = first.GetProperty("previewToken").GetString();

        var noBody = await a.H.SendWithResponseAsync(HttpMethod.Post, Activation(plan));
        var badToken = await a.H.SendWithResponseAsync(HttpMethod.Post, Activation(plan), new { previewToken = "xyz" });
        await a.H.PutSlotsAsync(plan, (task, 0, 3, null));
        var auditBefore = await a.H.AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);
        var rejected = await a.H.SendWithResponseAsync(HttpMethod.Post, Activation(plan), new { previewToken = oldToken });

        ShouldBeProblem(noBody.Response, noBody.Body, HttpStatusCode.BadRequest, "validation_error");
        ShouldBeProblem(badToken.Response, badToken.Body, HttpStatusCode.BadRequest, "validation_error");
        ShouldBeProblem(rejected.Response, rejected.Body, HttpStatusCode.Conflict, "stale_activation_preview");
        (await a.H.AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(auditBefore);
        (await a.H.Cycles.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(0);
        (await a.H.ActivePlanIdAsync()).Should().Be(a.PlanId);
        (await PreviewOkAsync(a.H, plan)).GetProperty("previewToken").GetString().Should().NotBe(oldToken);

        // A change of a task the plan uses between preview and confirmation makes the token stale as well.
        var second = (await PreviewOkAsync(a.H, plan)).GetProperty("previewToken").GetString();
        var patched = await a.H.SendAsync(HttpMethod.Patch, $"/api/v2/tasks/{task}", new { durationMinutes = 20 });
        patched.Status.Should().Be(HttpStatusCode.OK, patched.Body.ToString());
        var stale = await a.H.SendWithResponseAsync(HttpMethod.Post, Activation(plan), new { previewToken = second });
        ShouldBeProblem(stale.Response, stale.Body, HttpStatusCode.Conflict, "stale_activation_preview");
        (await a.H.ActivePlanIdAsync()).Should().Be(a.PlanId);
    }

    [Fact]
    public async Task Generated_work_beyond_the_next_cycle_stays_outside_the_preview_and_the_replacement()
    {
        using var a = await ArrangeAsync();
        var task = await a.H.NewTaskAsync("Ramen", a.Room, "1w");
        await a.H.PutSlotsAsync(a.PlanId, (task, 0, 3, null));
        await a.H.GenerateUpcomingAsync();
        await a.H.GenerateCycleAsync(2, "future-scope");
        var farFuture = (await a.H.OccurrencesOfAsync(task)).Single(o => GenerationHarness.DayOf(o) == "2026-11-11");
        var empty = await NewPlanAsync(a.H, "Leeg");

        var preview = await PreviewOkAsync(a.H, empty);
        Ids(preview.GetProperty("removed")).Should().NotContain(IdOf(farFuture));
        var activated = await a.H.SendAsync(HttpMethod.Post, Activation(empty), new { previewToken = preview.GetProperty("previewToken").GetString() });

        activated.Status.Should().Be(HttpStatusCode.OK, activated.Body.ToString());
        (await a.H.Occurrences.CountDocumentsAsync(new BsonDocument("_id", farFuture["_id"]), cancellationToken: Ct)).Should().Be(1);
    }

    [Fact]
    public async Task A_mid_cycle_activation_keeps_done_skipped_moved_past_and_ad_hoc_occurrences_and_replaces_the_rest()
    {
        using var a = await ArrangeAsync(); // Monday 14 Sep
        var p1 = await a.H.SeedPersonAsync("Persoon A");
        var p2 = await a.H.SeedPersonAsync("Persoon B");
        var weekly = await a.H.NewTaskAsync("Badkamer", a.Room, "1w", 30);
        var twice = await a.H.NewTaskAsync("Wastafel", a.Room, "2w");
        await a.H.PutSlotsAsync(
            a.PlanId,
            [
                .. Enumerable.Range(0, 4).Select(w => (weekly, w, 1, (string?)p1)),
                .. Enumerable.Range(0, 4).SelectMany(w => new[] { (twice, w, 3, (string?)null), (twice, w, 6, (string?)null) }),
            ]);
        var weeklyDays = await a.H.OccurrencesOfAsync(weekly);
        var twiceDays = await a.H.OccurrencesOfAsync(twice);
        var past = On(weeklyDays, "2026-09-14");
        var done = On(weeklyDays, "2026-09-21");
        var skipped = On(twiceDays, "2026-09-23");
        var dragged = On(weeklyDays, "2026-09-28");
        await SetAsync(a.H, IdOf(done), new BsonDocument { { "status", "done" }, { "completedAt", new BsonDateTime(a.H.Clock.GetUtcNow().UtcDateTime) } });
        await SetAsync(a.H, IdOf(skipped), new BsonDocument("status", "skipped"));
        await SetAsync(a.H, IdOf(dragged), new BsonDocument("date", new BsonDateTime(DateTime.Parse("2026-09-28T22:00:00Z", null, System.Globalization.DateTimeStyles.AdjustToUniversal))));
        var adhoc = new BsonDocument(done)
        {
            ["_id"] = ObjectId.GenerateNewId(), ["origin"] = "adhoc", ["planId"] = BsonNull.Value, ["status"] = "open",
            ["date"] = new BsonDateTime(DateTime.Parse("2026-09-29T22:00:00Z", null, System.Globalization.DateTimeStyles.AdjustToUniversal)),
            ["plannedDate"] = new BsonDateTime(DateTime.Parse("2026-09-29T22:00:00Z", null, System.Globalization.DateTimeStyles.AdjustToUniversal)),
            ["completedAt"] = BsonNull.Value, ["completedBy"] = BsonNull.Value,
        };
        await a.H.Occurrences.InsertOneAsync(adhoc, cancellationToken: Ct);

        // Two days later a new plan is activated.
        a.H.Clock.Set("2026-09-16T08:00:00.000Z");
        var plan = await NewPlanAsync(a.H, "Nieuw");
        await a.H.PutSlotsAsync(plan, Enumerable.Range(0, 4).Select(w => (twice, w, 4, (string?)p2)).ToArray());
        var oldOpenFuture = await a.H.OccurrencesOfAsync(extra: Builders<BsonDocument>.Filter.And(
            new BsonDocument { { "planId", ObjectId.Parse(a.PlanId) }, { "status", "open" }, { "origin", "generated" } },
            new BsonDocument("date", new BsonDocument("$gte", new BsonDateTime(DateTime.Parse("2026-09-15T22:00:00Z", null, System.Globalization.DateTimeStyles.AdjustToUniversal)))),
            new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$date", "$plannedDate" }))));
        oldOpenFuture.Should().NotBeEmpty();

        var preview = await PreviewOkAsync(a.H, plan);

        preview.GetProperty("asOfDate").GetString().Should().Be("2026-09-16");
        Ids(preview.GetProperty("removed")).Order().Should().Equal(oldOpenFuture.Select(IdOf).Order());
        Ids(preview.GetProperty("preserved").GetProperty("done")).Should().Contain(IdOf(done));
        Ids(preview.GetProperty("preserved").GetProperty("skipped")).Should().Contain(IdOf(skipped));
        Ids(preview.GetProperty("preserved").GetProperty("moved")).Should().Contain(IdOf(dragged));
        Ids(preview.GetProperty("preserved").GetProperty("adhoc")).Should().Contain(IdOf(adhoc));

        var auditBefore = await a.H.AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);
        var result = await a.H.SendAsync(HttpMethod.Post, Activation(plan), new { previewToken = preview.GetProperty("previewToken").GetString() });

        result.Status.Should().Be(HttpStatusCode.OK, result.Body.ToString());
        var runId = result.Body.GetProperty("runId").GetString()!;
        result.Body.GetProperty("plan").GetProperty("active").GetBoolean().Should().BeTrue();
        result.Body.GetProperty("removed").GetInt32().Should().Be(oldOpenFuture.Count);
        result.Body.GetProperty("generated").GetArrayLength().Should().Be(2);

        // The activation entry: the profile as actor, the UI as source, the run id in meta.
        var activate = await a.H.AuditAsync("cyclePlan", "activate");
        activate.Should().ContainSingle();
        activate[0]["entityId"].AsObjectId.ToString().Should().Be(plan);
        activate[0]["actorId"].AsObjectId.ToString().Should().Be(a.H.Planner.Id);
        activate[0]["source"].AsString.Should().Be("ui");
        activate[0]["before"].AsBsonDocument.ToJson().Should().Be("{ \"active\" : false }");
        activate[0]["after"].AsBsonDocument.ToJson().Should().Be("{ \"active\" : true }");
        activate[0]["meta"]["runId"].AsString.Should().Be(runId);

        // Kept.
        foreach (var kept in new[] { past, done, skipped, dragged, adhoc })
        {
            (await a.H.Occurrences.CountDocumentsAsync(new BsonDocument("_id", kept["_id"]), cancellationToken: Ct)).Should().Be(1, GenerationHarness.DayOf(kept));
        }

        // Replaced, each removal audited with the system as source and the run id of the activation.
        (await a.H.Occurrences.CountDocumentsAsync(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(oldOpenFuture.Select(o => o["_id"])))), cancellationToken: Ct)).Should().Be(0);
        var deletions = await a.H.AuditAsync("occurrence", "delete");
        deletions.Should().HaveCount(oldOpenFuture.Count);
        deletions.Should().OnlyContain(d => d["source"].AsString == "system" && d["meta"]["runId"].AsString == runId && d["meta"]["reason"].AsString == "plan_activation");

        // Regenerated from the new plan: the Thursdays from today in cycle 0 and all of cycle 1.
        var fresh = await a.H.OccurrencesOfAsync(extra: new BsonDocument("planId", ObjectId.Parse(plan)));
        GenerationHarness.DaysOf(fresh).Should().Equal("2026-09-17", "2026-09-24", "2026-10-01", "2026-10-08", "2026-10-15", "2026-10-22", "2026-10-29", "2026-11-05");
        GenerationHarness.DaysOf(fresh).Should().Equal(Dates(preview.GetProperty("added")).Order(StringComparer.Ordinal));
        fresh.Should().OnlyContain(o => o["assigneeId"].AsObjectId.ToString() == p2);
        var creates = await a.H.AuditAsync("occurrence", "create", new BsonDocument("meta.runId", runId));
        creates.Should().HaveCount(fresh.Count);

        // Only one plan is active; the deactivation of the old one is audited and names the activated plan.
        var active = await a.H.Database.GetCollection<BsonDocument>("cyclePlans").Find(new BsonDocument("active", true)).ToListAsync(Ct);
        active.Select(p => p["_id"].AsObjectId.ToString()).Should().Equal(plan);
        var deactivated = await a.H.AuditAsync("cyclePlan", "update", new BsonDocument { { "entityId", ObjectId.Parse(a.PlanId) }, { "after.active", false } });
        deactivated.Should().ContainSingle();
        deactivated[0]["meta"]["activatedPlanId"].AsObjectId.ToString().Should().Be(plan);
        deactivated[0]["meta"]["runId"].AsString.Should().Be(runId);
        (await a.H.AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().BeGreaterThan(auditBefore);
    }

    [Fact]
    public async Task Activating_an_unknown_plan_is_404_and_a_malformed_id_or_a_missing_profile_is_refused()
    {
        using var a = await ArrangeAsync();
        var token = new string('a', 64);

        var unknown = await a.H.SendWithResponseAsync(HttpMethod.Post, Activation("0123456789abcdef01234567"), new { previewToken = token });
        var malformed = await a.H.SendWithResponseAsync(HttpMethod.Post, Activation("nope"), new { previewToken = token });
        var anonymous = await a.H.SendWithResponseAsync(HttpMethod.Post, Activation(a.PlanId), new { previewToken = token }, withProfile: false);

        ShouldBeProblem(unknown.Response, unknown.Body, HttpStatusCode.NotFound, "not_found");
        ShouldBeProblem(malformed.Response, malformed.Body, HttpStatusCode.BadRequest, "validation_error");
        ShouldBeProblem(anonymous.Response, anonymous.Body, HttpStatusCode.BadRequest, "profile_required");
    }

    [Fact]
    public async Task Activating_the_active_plan_again_regenerates_its_upcoming_occurrences_and_audits_the_activation()
    {
        using var a = await ArrangeAsync();
        var task = await a.H.NewTaskAsync("Ramen", a.Room, "1w");
        await a.H.PutSlotsAsync(a.PlanId, (task, 0, 3, null));
        var preview = await PreviewOkAsync(a.H, a.PlanId);

        var result = await a.H.SendAsync(HttpMethod.Post, Activation(a.PlanId), new { previewToken = preview.GetProperty("previewToken").GetString() });

        result.Status.Should().Be(HttpStatusCode.OK, result.Body.ToString());
        (await a.H.AuditAsync("cyclePlan", "activate")).Should().ContainSingle();
        (await a.H.AuditAsync("cyclePlan", "update", new BsonDocument("after.active", false))).Should().BeEmpty("no other plan was active");
        (await a.H.OccurrencesOfAsync(task)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Two_concurrent_activations_with_the_same_token_let_exactly_one_win_and_the_other_see_a_stale_preview()
    {
        using var a = await ArrangeAsync();
        var task = await a.H.NewTaskAsync("Ramen", a.Room, "1w");
        await a.H.PutSlotsAsync(a.PlanId, (task, 0, 3, null));
        var first = await NewPlanAsync(a.H, "Eerste");
        var second = await NewPlanAsync(a.H, "Tweede");
        await a.H.PutSlotsAsync(first, (task, 0, 1, null));
        await a.H.PutSlotsAsync(second, (task, 0, 2, null));
        var tokenFirst = (await PreviewOkAsync(a.H, first)).GetProperty("previewToken").GetString();
        var tokenSecond = (await PreviewOkAsync(a.H, second)).GetProperty("previewToken").GetString();

        // Different plans, so neither token covers the other plan's result: only the guard document makes the two activations conflict.
        var results = await Task.WhenAll(
            a.H.SendAsync(HttpMethod.Post, Activation(first), new { previewToken = tokenFirst }),
            a.H.SendAsync(HttpMethod.Post, Activation(second), new { previewToken = tokenSecond }));

        results.Count(r => r.Status == HttpStatusCode.OK).Should().Be(1, string.Join(" | ", results.Select(r => r.Body.ToString())));
        var loser = results.Single(r => r.Status != HttpStatusCode.OK);
        loser.Status.Should().Be(HttpStatusCode.Conflict);
        loser.Body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:stale_activation_preview");
        var active = await a.H.Database.GetCollection<BsonDocument>("cyclePlans").Find(new BsonDocument("active", true)).ToListAsync(Ct);
        active.Should().ContainSingle();
        (await a.H.AuditAsync("cyclePlan", "activate")).Should().ContainSingle();
        var guard = await a.H.Database.GetCollection<BsonDocument>("settings").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Ct);
        guard["activationVersion"].ToInt32().Should().Be(1, "only the committed activation wrote the guard; the loser's write rolled back with its transaction");
    }

    [Fact]
    public async Task The_guard_document_makes_overlapping_transactions_conflict_so_the_runner_has_to_retry_one_of_them()
    {
        using var a = await ArrangeAsync();
        using var scope = a.H.Services.CreateScope();
        var transactions = scope.ServiceProvider.GetRequiredService<ForRunningTransactions>();
        var activation = scope.ServiceProvider.GetRequiredService<ForActivatingCyclePlans>();
        var attempts = 0;

        async Task<TransactionOutcome<bool>> Work(CancellationToken ct)
        {
            Interlocked.Increment(ref attempts);
            (await activation.TouchGuardAsync(ct)).IsT0.Should().BeTrue();
            await Task.Delay(40, ct); // hold the transaction open so the two overlap
            return TransactionOutcome.Commit(true);
        }

        var both = await Task.WhenAll(transactions.RunAsync(Work, Ct), transactions.RunAsync(Work, Ct));

        both.Should().OnlyContain(r => r.IsT0);
        attempts.Should().BeGreaterThan(2, "without the shared guard write both transactions would commit on their first attempt");
        var guard = await a.H.Database.GetCollection<BsonDocument>("settings").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Ct);
        guard["activationVersion"].ToInt32().Should().Be(2);
    }
}
