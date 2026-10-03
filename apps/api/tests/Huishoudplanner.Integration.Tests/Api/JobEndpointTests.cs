using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// The manual job endpoints (<c>jobs.ts</c> of the Node server) on the real host and a real MongoDB replica set: planners start generation
/// and audit retention, a manual run is attributed to the requesting profile, and the answers have the shapes of the Node server. Ports the
/// manual-run case of audit-retention.test.ts ("can be started manually by a planner") and the manual generation of generation.test.ts.
/// Deferred to 6.3b: <c>/jobs/morning-notify</c> (needs the due list of slice 3.4) and the <c>due</c> summary of the generation answer (3.4).
/// </summary>
public sealed class JobEndpointTests(MongoContainerFixture mongo)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 3, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Instant(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    // ---- generation

    [Fact]
    public async Task A_planner_generates_the_current_and_the_next_cycle_and_the_entries_carry_the_profile_as_actor_with_the_run_id()
    {
        using var h = new GenerationHarness(mongo);
        var room = await h.SeedRoomAsync("Badkamer");
        var plan = await h.ActivePlanIdAsync();
        var task = await h.NewTaskAsync("Badkamer", room, "1w");
        await h.StoreSlotsAsync(plan, [.. Enumerable.Range(0, 4).Select(week => (task, week, 3, (string?)null))]);

        var (status, body) = await h.SendAsync(HttpMethod.Post, "/api/v2/jobs/generation");

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var runId = body.GetProperty("runId").GetString()!;
        runId.Should().NotBeNullOrWhiteSpace();
        body.GetProperty("removed").GetInt32().Should().Be(0);
        body.GetProperty("generated").EnumerateArray().Select(g => (g.GetProperty("cycleIndex").GetInt32(), g.GetProperty("inserted").GetInt32(), g.GetProperty("skipped").GetInt32()))
            .Should().Equal((0, 4, 0), (1, 4, 0));
        body.GetProperty("generated")[0].GetProperty("cycleId").GetString().Should().HaveLength(24);
        body.GetProperty("generated")[0].GetProperty("planId").GetString().Should().Be(plan);
        var created = await h.AuditAsync("occurrence", "create");
        created.Should().HaveCount(8);
        created.Select(e => e["actorId"].AsObjectId.ToString()).Distinct().Should().Equal(h.Planner.Id);
        created.Select(e => e["source"].AsString).Distinct().Should().Equal("ui");
        created.Select(e => e["meta"]["runId"].AsString).Distinct().Should().Equal(runId);
    }

    [Fact]
    public async Task A_second_manual_run_changes_and_audits_nothing_and_has_a_new_run_id()
    {
        using var h = new GenerationHarness(mongo);
        var room = await h.SeedRoomAsync("Badkamer");
        var plan = await h.ActivePlanIdAsync();
        var task = await h.NewTaskAsync("Badkamer", room, "1w");
        await h.StoreSlotsAsync(plan, [.. Enumerable.Range(0, 4).Select(week => (task, week, 3, (string?)null))]);
        var first = (await h.SendAsync(HttpMethod.Post, "/api/v2/jobs/generation")).Body;
        var audited = await h.AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

        var (status, second) = await h.SendAsync(HttpMethod.Post, "/api/v2/jobs/generation");

        status.Should().Be(HttpStatusCode.OK);
        second.GetProperty("runId").GetString().Should().NotBe(first.GetProperty("runId").GetString());
        second.GetProperty("generated").EnumerateArray().Select(g => (g.GetProperty("inserted").GetInt32(), g.GetProperty("skipped").GetInt32())).Should().Equal((0, 4), (0, 4));
        (await h.AuditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(audited);
    }

    [Fact]
    public async Task Generation_needs_a_planner_an_administrator_may_start_it_and_nobody_without_a_profile()
    {
        using var h = new GenerationHarness(mongo);

        var anonymous = await h.SendAsync(HttpMethod.Post, "/api/v2/jobs/generation", withProfile: false);
        var admin = await h.SendAsync(HttpMethod.Post, "/api/v2/jobs/generation", asAdmin: true);

        anonymous.Status.Should().Be(HttpStatusCode.BadRequest);
        anonymous.Body.GetProperty("type").GetString().Should().EndWith("profile_required");
        admin.Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_member_may_not_start_generation()
    {
        var users = new FakeUserDirectory();
        var member = users.Add(Role.Member);
        using var factory = ApiFactory.ForMongo(mongo).WithoutSeeding().WithPort<ForFindingUsers>(users);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/jobs/generation");
        request.Headers.Add("X-Profile-Id", member.Id);

        var response = await client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- audit retention

    private async Task<(JsonElement Body, HttpStatusCode Status, long Remaining)> RunRetentionAsync(string? days)
    {
        var users = new FakeUserDirectory();
        var planner = users.Add(Role.Planner);
        var databaseName = MongoContainerFixture.NewDatabaseName();
        var factory = ApiFactory.ForMongo(mongo, databaseName).WithoutSeeding().WithPort<ForFindingUsers>(users).WithPort<TimeProvider>(new FixedTimeProvider(Instant(Now)));
        if (days is not null)
        {
            factory.WithSetting("AUDIT_RETENTION_DAYS", days);
        }

        using var _ = factory;
        using var client = factory.CreateClient();
        using var mongoClient = new MongoClient(mongo.ConnectionString);
        var auditLog = mongoClient.GetDatabase(databaseName).GetCollection<BsonDocument>("auditLog");
        await auditLog.InsertOneAsync(
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "at", new BsonDateTime(Now.AddDays(-31).UtcDateTime) },
                { "actorId", ObjectId.Parse("000000000000000000000000") }, { "entity", "task" }, { "entityId", ObjectId.GenerateNewId() },
                { "action", "update" }, { "before", new BsonDocument() }, { "after", new BsonDocument("label", "old") }, { "source", "system" },
            },
            cancellationToken: Ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/jobs/audit-retention");
        request.Headers.Add("X-Profile-Id", planner.Id);

        var response = await client.SendAsync(request, Ct);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
        var remaining = await auditLog.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);
        await mongoClient.DropDatabaseAsync(databaseName, Ct);
        return (body, response.StatusCode, remaining);
    }

    [Fact]
    public async Task Audit_retention_can_be_started_manually_by_a_planner()
    {
        var (body, status, remaining) = await RunRetentionAsync("30");

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        body.GetProperty("status").GetString().Should().Be("done");
        body.GetProperty("deleted").GetInt32().Should().Be(1);
        body.GetProperty("cutoff").GetString().Should().Be(Instant(Now.AddDays(-30)));
        remaining.Should().Be(0);
    }

    [Fact]
    public async Task Audit_retention_without_AUDIT_RETENTION_DAYS_answers_disabled_and_deletes_nothing()
    {
        var (body, status, remaining) = await RunRetentionAsync(null);

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("status").GetString().Should().Be("disabled");
        body.EnumerateObject().Select(p => p.Name).Should().Equal("status");
        remaining.Should().Be(1);
    }
}
