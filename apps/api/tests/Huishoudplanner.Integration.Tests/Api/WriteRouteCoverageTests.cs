#pragma warning disable CA1861 // inline arrays in request bodies of tests
using System.Net;
using System.Reflection;
using Huishoudplanner.Adapters.Mongo;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Ports <c>apps/server/test/write-routes-coverage.test.ts</c>: every write endpoint of the real host has a scenario in
/// <see cref="Scenarios"/>, and running the scenario proves, with the <see cref="WriteCapture"/> on the application's MongoDB client, that
/// every state change comes with audit entries of the right entity, action, source and actor, that nothing is written outside the
/// collections of the data layer or changes the schema, and that where the request is idempotent the same request again writes and audits
/// nothing. Read-only POSTs are proven to write nothing at all; the few endpoints that deliberately record no audit entry are listed with their
/// reason and proven to change no state outside the audit log.
/// </summary>
/// <remarks>
/// <b>This test fails when a write endpoint has no scenario.</b> That is the point: a slice that adds a write endpoint (the points ledger, the
/// promote and ad-hoc endpoints, badges, import and so on) must extend <see cref="Scenarios"/> in the same change. Endpoints of slices that are
/// not merged yet are simply absent from the table.
/// </remarks>
public sealed class WriteRouteCoverageTests(AuditCoverageHarness h) : IClassFixture<AuditCoverageHarness>
{
    private static readonly string[] ReadMethods = [HttpMethods.Get, HttpMethods.Head, HttpMethods.Options];

    private static readonly HashSet<string> DataLayerCollections = typeof(MongoCollections)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral)
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToHashSet();

    /// <summary>
    /// Collections of derived state (ADR-0011): the points ledger is kept in step with the occurrences by the sync hook inside the same transaction,
    /// and its changes are not audited one by one. A write to them is covered by the audit entry of the occurrence change that caused it (or by the
    /// one summary entry of a reconciliation), so a request that writes only derived collections without a <c>points</c> summary is a failure.
    /// </summary>
    private static readonly HashSet<string> DerivedCollections = [MongoCollections.PointEntries];

    /// <summary>
    /// Which collection an audit entity records changes of. A non-derived collection written by a request must be covered by an entry of its entity,
    /// or by the scenario declaring it in <c>Covers</c> (a summary entry that stands for many documents, such as the statistics reset). The import
    /// entity is a summary of everything and has no collection of its own here. Limit: this checks that the collection is covered by some entry, not
    /// that every document is.
    /// </summary>
    private static readonly Dictionary<string, string> EntityCollections = new()
    {
        ["user"] = MongoCollections.Users,
        ["room"] = MongoCollections.Rooms,
        ["task"] = MongoCollections.Tasks,
        ["cyclePlan"] = MongoCollections.CyclePlans,
        ["occurrence"] = MongoCollections.Occurrences,
        ["cycle"] = MongoCollections.Cycles,
        ["settings"] = MongoCollections.Settings,
        ["points"] = MongoCollections.PointEntries,
        ["badge"] = MongoCollections.Badges,
        ["badgeAward"] = MongoCollections.BadgeAwards,
    };

    internal enum Kind
    {
        /// <summary>Changes state, so the request must write entity and audit entry (and repeat as a no-op where <c>Idempotent</c>).</summary>
        Audited,

        /// <summary>A POST that only reads or computes; it must not write at all.</summary>
        ReadOnly,

        /// <summary>Writes only the audit log, deliberately without an entry of its own (see the reason).</summary>
        Unaudited,
    }

    internal sealed record Call(HttpMethod Method, string Url, object? Body, UserIdentity Actor);

    internal sealed record Scenario(
        string Route,
        Kind Kind,
        Func<AuditCoverageHarness, Task<Call>> Build,
        string? Entity = null,
        string? Action = null,
        string Source = "ui",
        bool Idempotent = false,
        string? Reason = null,
        string[]? Covers = null);

    internal static Func<AuditCoverageHarness, Task<Call>> Fixed(Func<AuditCoverageHarness, Call> call) => harness => Task.FromResult(call(harness));

    private static async Task<string> CreateAsync(AuditCoverageHarness h, string url, object body, UserIdentity actor)
    {
        var (status, json) = await h.SendAsync(HttpMethod.Post, url, body, actor);
        status.Should().Be(HttpStatusCode.Created, json.ToString());
        return json.GetProperty("id").GetString()!;
    }

    private static async Task DoAsync(AuditCoverageHarness h, HttpMethod method, string url, object? body, UserIdentity actor)
    {
        var (status, json) = await h.SendAsync(method, url, body, actor);
        ((int)status).Should().BeLessThan(300, json.ToString());
    }

    /// <summary>
    /// Ordered: later scenarios may rely on earlier ones, and each arranges the data it needs in its <c>Build</c> (outside the capture).
    /// The occurrence scenarios come first, while the generated occurrences of the household are still untouched.
    /// </summary>
    private static readonly Scenario[] Scenarios =
    [
        new("POST /api/v2/users", Kind.Audited, Fixed(h => new(HttpMethod.Post, "/api/v2/users", new { name = "Logé", color = "#16a34a" }, h.Admin)), "user", "create"),
        new("PATCH /api/v2/users/{id}", Kind.Audited, Fixed(h => new(HttpMethod.Patch, $"/api/v2/users/{h.P2.Id}", new { name = "Bram" }, h.Admin)), "user", "update", Idempotent: true),
        new("PUT /api/v2/users/{id}/browser-notifications", Kind.Audited, Fixed(h => new(HttpMethod.Put, $"/api/v2/users/{h.P2.Id}/browser-notifications", new { enabled = true, times = new[] { "18:30", "08:00" } }, h.P2)), "user", "update", Idempotent: true),

        new("POST /api/v2/rooms", Kind.Audited, Fixed(h => new(HttpMethod.Post, "/api/v2/rooms", new { name = "Zolder" }, h.Admin)), "room", "create"),
        new("PATCH /api/v2/rooms/{id}", Kind.Audited, Fixed(h => new(HttpMethod.Patch, $"/api/v2/rooms/{h.Room}", new { name = "Badkamer boven" }, h.Admin)), "room", "update", Idempotent: true),
        new("DELETE /api/v2/rooms/{id}", Kind.Audited, async h => new(HttpMethod.Delete, $"/api/v2/rooms/{await CreateAsync(h, "/api/v2/rooms", new { name = "Tijdelijke ruimte" }, h.Admin)}", null, h.Admin), "room", "delete", Idempotent: true),

        new("POST /api/v2/tasks", Kind.Audited, Fixed(h => new(HttpMethod.Post, "/api/v2/tasks", new { name = "Stoffen", roomId = h.Room, intervalKey = "1w", durationMinutes = 15 }, h.Planner)), "task", "create"),

        new("POST /api/v2/occurrences/{id}/complete", Kind.Audited, async h => new(HttpMethod.Post, $"/api/v2/occurrences/{await h.OccurrenceIdAsync(h.Weekly, "2026-09-21")}/complete", null, h.P1), "occurrence", "complete", Idempotent: true),
        new("POST /api/v2/occurrences/{id}/uncomplete", Kind.Audited, async h =>
        {
            var id = await h.OccurrenceIdAsync(h.Weekly, "2026-09-28");
            await DoAsync(h, HttpMethod.Post, $"/api/v2/occurrences/{id}/complete", null, h.P1);
            return new(HttpMethod.Post, $"/api/v2/occurrences/{id}/uncomplete", null, h.P1);
        }, "occurrence", "uncomplete", Idempotent: true),
        new("POST /api/v2/occurrences/{id}/completion", Kind.Audited, async h =>
        {
            var id = await h.OccurrenceIdAsync(h.Weekly, "2026-10-05");
            await DoAsync(h, HttpMethod.Post, $"/api/v2/occurrences/{id}/complete", null, h.P1);
            return new(HttpMethod.Post, $"/api/v2/occurrences/{id}/completion", new { date = "2026-10-05", completedAt = "2026-09-16T08:30:00.000Z", completedBy = h.P2.Id }, h.Admin);
        }, "occurrence", "update", Idempotent: true),
        new("POST /api/v2/occurrences/{id}/skip", Kind.Audited, async h => new(HttpMethod.Post, $"/api/v2/occurrences/{await h.OccurrenceIdAsync(h.Twice, "2026-09-24")}/skip", new { reason = "geen tijd" }, h.P1), "occurrence", "skip", Idempotent: true),
        new("POST /api/v2/occurrences/{id}/reschedule", Kind.Audited, async h => new(HttpMethod.Post, $"/api/v2/occurrences/{await h.OccurrenceIdAsync(h.Weekly, "2026-09-14")}/reschedule", new { date = "2026-09-16" }, h.P1), "occurrence", "reschedule", Idempotent: true),
        new("POST /api/v2/occurrences/{id}/assignment", Kind.Audited, async h => new(HttpMethod.Post, $"/api/v2/occurrences/{await h.OccurrenceIdAsync(h.Twice, "2026-10-15")}/assignment", new { assigneeId = h.P1.Id }, h.P1), "occurrence", "assign", Idempotent: true),
        new("POST /api/v2/occurrences/{id}/claim", Kind.Audited, async h => new(HttpMethod.Post, $"/api/v2/occurrences/{await h.OccurrenceIdAsync(h.Twice, "2026-09-30")}/claim", null, h.P2), "occurrence", "assign", Idempotent: true),
        new("DELETE /api/v2/occurrences/{id}", Kind.Audited, async h =>
        {
            var id = await h.OccurrenceIdAsync(h.Twice, "2026-10-29");
            await DoAsync(h, HttpMethod.Post, $"/api/v2/occurrences/{id}/complete", new { completedBy = h.P1.Id }, h.P1);
            return new(HttpMethod.Delete, $"/api/v2/occurrences/{id}", null, h.Admin);
        }, "occurrence", "delete", Idempotent: true),

        new("POST /api/v2/occurrences", Kind.Audited, async h => new(HttpMethod.Post, "/api/v2/occurrences", new { taskId = await CreateAsync(h, "/api/v2/tasks", new { name = "Ramen lappen", roomId = h.Room, intervalKey = "1w", durationMinutes = 15 }, h.Planner), date = "2026-09-19" }, h.P1), "occurrence", "create"),
        new("POST /api/v2/occurrences/one-off", Kind.Audited, Fixed(h => new(HttpMethod.Post, "/api/v2/occurrences/one-off", new { name = "Eenmalige klus", roomId = h.Room, durationMinutes = 15, date = "2026-09-19" }, h.P1)), "occurrence", "create"),
        new("POST /api/v2/occurrences/{id}/retraction", Kind.Audited, async h =>
        {
            // Work recorded as done today is the retractable kind (a planned extra is not).
            var task = await CreateAsync(h, "/api/v2/tasks", new { name = "Vergeten klus", roomId = h.Room, intervalKey = "1w", durationMinutes = 15 }, h.Planner);
            var id = await CreateAsync(h, "/api/v2/occurrences", new { taskId = task, date = "2026-09-16", done = true }, h.P1);
            return new(HttpMethod.Post, $"/api/v2/occurrences/{id}/retraction", null, h.P1);
        }, "occurrence", "delete", Idempotent: true),

        new("PATCH /api/v2/tasks/{id}", Kind.Audited, Fixed(h => new(HttpMethod.Patch, $"/api/v2/tasks/{h.Twice}", new { durationMinutes = 20 }, h.Planner)), "task", "update", Idempotent: true),
        new("POST /api/v2/rooms/{id}/tasks/bulk", Kind.Audited, Fixed(h => new(HttpMethod.Post, $"/api/v2/rooms/{h.Room}/tasks/bulk", new { op = "reassign", defaultAssigneeId = h.P2.Id }, h.Planner)), "task", "assign", Idempotent: true),
        new("PATCH /api/v2/settings", Kind.Audited, Fixed(h => new(HttpMethod.Patch, "/api/v2/settings", new { promoteThreshold = 4 }, h.Admin)), "settings", "update", Idempotent: true),

        new("POST /api/v2/cycle-plans", Kind.Audited, Fixed(h => new(HttpMethod.Post, "/api/v2/cycle-plans", new { name = "Nieuw" }, h.Planner)), "cyclePlan", "create"),
        new("PATCH /api/v2/cycle-plans/{id}", Kind.Audited, async h => new(HttpMethod.Patch, $"/api/v2/cycle-plans/{await CreateAsync(h, "/api/v2/cycle-plans", new { name = "Concept" }, h.Planner)}", new { name = "Nieuw plan" }, h.Planner), "cyclePlan", "update", Idempotent: true),
        new("DELETE /api/v2/cycle-plans/{id}", Kind.Audited, async h => new(HttpMethod.Delete, $"/api/v2/cycle-plans/{await CreateAsync(h, "/api/v2/cycle-plans", new { name = "Weg" }, h.Planner)}", null, h.Planner), "cyclePlan", "delete", Idempotent: true),
        new("PUT /api/v2/cycle-plans/{id}/slots", Kind.Audited, Fixed(h => new(HttpMethod.Put, $"/api/v2/cycle-plans/{h.ActivePlan}/slots", new
        {
            slots = new object[]
            {
                new { taskId = h.Weekly, weekIndex = 1, weekday = 1, assigneeId = h.P1.Id },
                new { taskId = h.Weekly, weekIndex = 2, weekday = 1, assigneeId = (string?)null },
            },
        }, h.Planner)), "cyclePlan", "update"), // not idempotent by design: saving the slots of the active plan always replaces the open generated occurrences (requirements 4.3, as in Node)
        new("POST /api/v2/jobs/generation", Kind.Audited, async h =>
        {
            // The slot save above already synchronized the future occurrences, so remove them to give the run work to repair.
            await h.DeleteGeneratedOccurrencesAsync();
            return new(HttpMethod.Post, "/api/v2/jobs/generation", null, h.Planner);
        }, "occurrence", "create", Idempotent: true),

        new("POST /api/v2/ai/propose-plan", Kind.Audited, Fixed(h => new(HttpMethod.Post, "/api/v2/ai/propose-plan", new { constraints = "geen nat werk doordeweeks" }, h.Planner)), "cyclePlan", "create", Source: "ai"),
        new("POST /api/v2/ai/rebalance", Kind.Audited, Fixed(h => new(HttpMethod.Post, "/api/v2/ai/rebalance", new { planId = h.ActivePlan }, h.Planner)), "cyclePlan", "create", Source: "ai"),
        new("POST /api/v2/ai/test", Kind.ReadOnly, Fixed(h => new(HttpMethod.Post, "/api/v2/ai/test", new { aiProvider = new { type = "mock" } }, h.Planner))),
        new("POST /api/v2/ai/suggest-tasks", Kind.ReadOnly, Fixed(h => new(HttpMethod.Post, "/api/v2/ai/suggest-tasks", new { roomId = h.Room }, h.Planner))),
        new("POST /api/v2/ai/explain", Kind.ReadOnly, Fixed(h => new(HttpMethod.Post, "/api/v2/ai/explain", new { planId = h.ActivePlan }, h.Planner))),
        new("POST /api/v2/cycle-plans/{id}/validation", Kind.ReadOnly, Fixed(h => new(HttpMethod.Post, $"/api/v2/cycle-plans/{h.ActivePlan}/validation", null, h.Planner))),
        new("POST /api/v2/cycle-plans/validation", Kind.ReadOnly, Fixed(h => new(HttpMethod.Post, "/api/v2/cycle-plans/validation", new { slots = new object[] { new { taskId = h.Weekly, weekIndex = 0, weekday = 1, assigneeId = h.P1.Id } } }, h.Planner))),

        new("POST /api/v2/cycle-plans/{id}/activation", Kind.Audited, async h =>
        {
            var copy = await CreateAsync(h, "/api/v2/cycle-plans", new { name = "Zomer", copyFromId = h.ActivePlan }, h.Planner);
            var (status, preview) = await h.SendAsync(HttpMethod.Get, $"/api/v2/cycle-plans/{copy}/activation-preview", null, h.Planner);
            status.Should().Be(HttpStatusCode.OK, preview.ToString());
            return new(HttpMethod.Post, $"/api/v2/cycle-plans/{copy}/activation", new { previewToken = preview.GetProperty("previewToken").GetString() }, h.Planner);
        }, "cyclePlan", "activate", Idempotent: true, Covers: [MongoCollections.Settings]), // the activation guard document (ADR-0008) lives in settings and is bookkeeping of the activation, not household state

        new("POST /api/v2/points/recompute", Kind.Audited, async h =>
        {
            await h.InsertStrayLedgerEntryAsync();
            return new(HttpMethod.Post, "/api/v2/points/recompute", null, h.Admin);
        }, "points", "recompute", Idempotent: true),
        new("DELETE /api/v2/stats", Kind.Audited, Fixed(h => new(HttpMethod.Delete, "/api/v2/stats", null, h.Admin)), "settings", "reset", Covers: [MongoCollections.Occurrences, MongoCollections.Tasks, MongoCollections.Cycles]), // one summary entry stands for every document the reset touches // not idempotent by design (as in Node): a reset that finds nothing to reset still records its counts as one audit entry

        // The only deliberately unaudited writes. Both only ever delete audit entries; neither may touch other state.
        new("POST /api/v2/jobs/audit-retention", Kind.Unaudited, Fixed(h => new(HttpMethod.Post, "/api/v2/jobs/audit-retention", null, h.Planner)),
            Reason: "Prunes the audit log itself; Node writes no entry for it either (an entry about the pruning would be part of what is pruned). Disabled without AUDIT_RETENTION_DAYS."),
        new("DELETE /api/v2/audit", Kind.Unaudited, Fixed(h => new(HttpMethod.Delete, "/api/v2/audit", null, h.Admin)),
            Reason: "Clearing the log cannot audit itself without making the cleared log non-empty again; Node does not audit it. Must stay last: it empties the audit log."),
    ];

    private static string Key(RouteEndpoint endpoint) =>
        $"{string.Join(",", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])} {endpoint.RoutePattern.RawText}";

    [Fact]
    public void EveryWriteEndpointOfTheRealHost_hasAScenario_andNoScenarioIsStale()
    {
        var registered = h.Endpoints
            .Where(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Any(m => !ReadMethods.Contains(m, StringComparer.OrdinalIgnoreCase)))
            .Select(Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        var covered = Scenarios.Select(s => s.Route).ToList();

        registered.Should().NotBeEmpty();
        var missing = registered.Except(covered).ToList();
        var stale = covered.Except(registered).ToList();
        missing.Should().BeEmpty(
            "every write endpoint needs a scenario in {0}.{1} (or, when it deliberately records no audit entry, an Unaudited scenario with its reason): add one for {2}",
            nameof(WriteRouteCoverageTests), nameof(Scenarios), string.Join(", ", missing));
        stale.Should().BeEmpty("a scenario names an endpoint that is no longer registered: {0}", string.Join(", ", stale));
        covered.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task EveryScenario_auditsEveryStateChange_writesOnlyInTheDataLayer_andARepeatWritesNothing()
    {
        Scenarios.Last().Route.Should().Be("DELETE /api/v2/audit", "clearing the audit log must run last: it empties what the other scenarios assert on");
        Scenarios.Select((s, i) => (s, i)).Where(t => t.s.Route == "DELETE /api/v2/audit").Should().ContainSingle();
        var failures = new List<string>();
        foreach (var scenario in Scenarios)
        {
            try
            {
                failures.AddRange((await RunAsync(h, scenario)).Select(f => $"{scenario.Route}: {f}"));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add($"{scenario.Route}: arranging or running threw {exception.GetType().Name}: {exception.Message}");
            }
        }

        failures.Should().BeEmpty();
    }

    /// <summary>Runs one scenario and returns what is wrong with it (empty when it holds). Also used to prove that a bypassing write fails.</summary>
    internal static async Task<List<string>> RunAsync(AuditCoverageHarness h, Scenario scenario)
    {
        var failures = new List<string>();
        var call = await scenario.Build(h);
        var known = await h.AuditIdsAsync();

        h.Capture.Clear();
        var (status, body) = await h.SendAsync(call.Method, call.Url, call.Body, call.Actor);
        var all = h.Capture.All();
        var writes = h.Capture.Writes();
        var auditInserts = h.Capture.AuditInserts();
        var entries = await h.AuditEntriesExceptAsync(known);

        if ((int)status >= 300)
        {
            failures.Add($"answered {(int)status}: {body}");
            return failures;
        }

        var outside = all.Where(w => !DataLayerCollections.Contains(w.Collection)).ToList();
        if (outside.Count > 0)
        {
            failures.Add($"wrote outside the data layer collections: {Describe(outside)}");
        }

        if (h.Capture.SchemaChanges().Count > 0)
        {
            failures.Add($"changed the schema while serving a request: {Describe(h.Capture.SchemaChanges())}");
        }

        switch (scenario.Kind)
        {
            case Kind.Audited:
                if (writes.Count == 0)
                {
                    failures.Add("wrote no state at all; the scenario must make the request change something");
                }

                if (writes.Count > 0 && auditInserts == 0)
                {
                    failures.Add($"UNAUDITED WRITE: {Describe(writes)} without any audit entry");
                }

                if (writes.Count > 0 && writes.All(w => DerivedCollections.Contains(w.Collection)) && scenario.Entity != "points")
                {
                    failures.Add($"only derived state was written ({Describe(writes)}); the primary change that causes it is missing or unaudited");
                }

                var covered = entries.Select(e => EntityCollections.GetValueOrDefault(e["entity"].AsString)).Concat(scenario.Covers ?? []).ToHashSet();
                var uncovered = writes.Select(w => w.Collection).Distinct().Where(c => !DerivedCollections.Contains(c) && !covered.Contains(c)).ToList();
                if (uncovered.Count > 0)
                {
                    failures.Add($"UNAUDITED WRITE: no audit entry covers the writes to {string.Join(", ", uncovered)} (entries: [{string.Join(", ", entries.Select(e => e["entity"].AsString))}])");
                }

                if (!entries.Any(e => e["entity"].AsString == scenario.Entity && e["action"].AsString == scenario.Action && e["source"].AsString == scenario.Source))
                {
                    failures.Add($"no audit entry {scenario.Entity}/{scenario.Action}/{scenario.Source}; got [{string.Join(", ", entries.Select(e => $"{e["entity"].AsString}/{e["action"].AsString}/{e["source"].AsString}"))}]");
                }

                if (entries.Count != auditInserts)
                {
                    failures.Add($"the capture saw {auditInserts} audit inserts but {entries.Count} entries exist");
                }

                if (entries.Any(e => e["actorId"] != ActorValue(call.Actor)))
                {
                    failures.Add("an audit entry is not attributed to the requesting profile");
                }

                break;
            case Kind.ReadOnly:
                if (all.Count > 0)
                {
                    failures.Add($"must write nothing, but wrote {Describe(all)}");
                }

                break;
            default:
                if (writes.Count > 0)
                {
                    failures.Add($"may only touch the audit log, but wrote state: {Describe(writes)}");
                }

                if (auditInserts > 0)
                {
                    failures.Add("is listed as unaudited but inserted an audit entry; move it to the audited scenarios");
                }

                break;
        }

        if (scenario.Kind == Kind.Audited && scenario.Idempotent)
        {
            var afterFirst = await h.AuditIdsAsync();
            h.Capture.Clear();
            var again = await h.SendAsync(call.Method, call.Url, call.Body, call.Actor);
            var repeated = h.Capture.All();
            var repeatedEntries = await h.AuditEntriesExceptAsync(afterFirst);
            if (repeated.Count > 0)
            {
                failures.Add($"the same request again (answered {(int)again.Status}) must write and audit nothing, but wrote {Describe(repeated)} [audit: {string.Join(", ", repeatedEntries.Select(e => $"{e["entity"].AsString}/{e["action"].AsString} {e["meta"]}"))}]");
            }
        }

        return failures;
    }

    private static BsonObjectId ActorValue(UserIdentity actor) => new(ObjectId.Parse(actor.Id));

    private static string Describe(IEnumerable<CapturedWrite> writes) =>
        string.Join(", ", writes.Select(w => $"{w.Operation} {w.Collection} x{w.Documents}"));
}
