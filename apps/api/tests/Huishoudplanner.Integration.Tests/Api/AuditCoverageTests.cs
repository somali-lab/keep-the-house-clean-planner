using System.Net;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Ports <c>apps/server/test/audit-coverage.test.ts</c>: what an audit entry records, that a no-op update writes and audits nothing, and that
/// the write capture (<see cref="WriteCapture"/>, the counterpart of <c>captureWrites</c>) really catches a write that bypasses the audit
/// recording. The walk over every write endpoint is <see cref="WriteRouteCoverageTests"/>.
/// </summary>
public sealed class AuditCoverageTests(MongoContainerFixture mongo)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<UsersHost> Start(MongoContainerFixture mongo, WriteCapture capture) =>
        UsersHost.StartAsync(mongo, factory => factory.WithWriteCapture(capture).WithEndpoints(MapTestEndpoints));

    /// <summary>A deliberately bad endpoint: it writes straight to a collection, around every audited store.</summary>
    private static void MapTestEndpoints(IEndpointRouteBuilder routes) =>
        routes.MapPost("/test/bypass", async (IMongoClient client, IConfiguration configuration) =>
        {
            var database = client.GetDatabase(MongoUrl.Create(configuration["MONGO_URL"]).DatabaseName);
            await database.GetCollection<BsonDocument>("rooms").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "sneaky" } });
            return Results.Ok();
        });

    [Fact]
    public async Task ACreate_isAuditedWithActorSourceAndAllFieldsExceptTimestamps()
    {
        var capture = new WriteCapture();
        await using var host = await Start(mongo, capture);
        var before = await host.AuditCount();
        capture.Clear();

        var response = await host.Send(HttpMethod.Post, "/api/v2/users", host.AdminId, new { name = "Persoon 3", color = "#db2777" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await UsersHost.Json(response)).GetProperty("id").GetString()!;
        var entry = (await host.AuditSince(before)).Should().ContainSingle().Subject;
        entry["entity"].AsString.Should().Be("user");
        entry["action"].AsString.Should().Be("create");
        entry["source"].AsString.Should().Be("ui");
        entry["actorId"].AsObjectId.ToString().Should().Be(host.AdminId);
        entry["entityId"].AsObjectId.ToString().Should().Be(created);
        entry["at"].ToUniversalTime().Should().Be(UsersHost.Now.UtcDateTime);
        entry["before"].AsBsonDocument.ElementCount.Should().Be(0);
        entry["after"].AsBsonDocument.ToJson().Should().Be(BsonDocument.Parse("""
            { name: "Persoon 3", color: "#db2777", active: true, role: "member", unavailableWeekdays: [],
              dailyBudgetMinutes: { weekday: 60, weekend: 120 }, maxDailyMinutes: { weekday: 60, weekend: 120 },
              browserNotifications: { enabled: false, times: [] } }
            """).ToJson());
        capture.Writes().Should().Equal(new CapturedWrite("users", "insert", 1));
        capture.AuditInserts().Should().Be(1);
    }

    [Fact]
    public async Task AnUpdate_isAuditedAsTheChangedFieldsOnly()
    {
        var capture = new WriteCapture();
        await using var host = await Start(mongo, capture);
        var before = await host.AuditCount();

        var response = await host.Send(HttpMethod.Patch, $"/api/v2/users/{host.MemberId}", host.AdminId, new { name = "Anna", dailyBudgetMinutes = new { weekday = 90, weekend = 120 } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = (await host.AuditSince(before)).Should().ContainSingle().Subject;
        entry["action"].AsString.Should().Be("update");
        entry["before"].AsBsonDocument.ToJson().Should().Be(BsonDocument.Parse("{ name: \"Persoon 2\", dailyBudgetMinutes: { weekday: 60 } }").ToJson());
        entry["after"].AsBsonDocument.ToJson().Should().Be(BsonDocument.Parse("{ name: \"Anna\", dailyBudgetMinutes: { weekday: 90 } }").ToJson());
    }

    [Fact]
    public async Task ANoOpUpdate_writesAndAuditsNothing()
    {
        var capture = new WriteCapture();
        await using var host = await Start(mongo, capture);
        (await host.Send(HttpMethod.Patch, $"/api/v2/users/{host.MemberId}", host.AdminId, new { name = "Anna" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var before = await host.AuditCount();
        capture.Clear();

        var again = await host.Send(HttpMethod.Patch, $"/api/v2/users/{host.MemberId}", host.AdminId, new { name = "Anna" });

        again.StatusCode.Should().Be(HttpStatusCode.OK);
        capture.All().Should().BeEmpty("a no-op update sends no write command at all");
        (await host.AuditCount()).Should().Be(before);
    }

    [Fact]
    public async Task TheCapture_seesTheEntityAndTheAuditEntryOfAnAuditedWriteInOneTransaction_andNothingOfARead()
    {
        var capture = new WriteCapture();
        await using var host = await Start(mongo, capture);
        capture.Clear();

        (await host.Send(HttpMethod.Get, "/api/v2/users", host.AdminId)).StatusCode.Should().Be(HttpStatusCode.OK);
        capture.All().Should().BeEmpty();

        (await host.Send(HttpMethod.Post, "/api/v2/users", host.AdminId, new { name = "Persoon 4", color = "#16a34a" })).StatusCode.Should().Be(HttpStatusCode.Created);
        capture.All().Should().BeEquivalentTo([new CapturedWrite("users", "insert", 1), new CapturedWrite("auditLog", "insert", 1)]);
        capture.SchemaChanges().Should().BeEmpty();
    }

    [Fact]
    public async Task ABypassingWrite_isCaughtByTheCapture()
    {
        var capture = new WriteCapture();
        await using var host = await Start(mongo, capture);
        capture.Clear();

        var response = await host.Send(HttpMethod.Post, "/test/bypass", host.AdminId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        capture.Writes().Should().Equal(new CapturedWrite("rooms", "insert", 1));
        capture.AuditInserts().Should().Be(0, "the bypass wrote a state change without an audit entry, which is exactly what the coverage tests fail on");
        (await host.Database.GetCollection<BsonDocument>("rooms").CountDocumentsAsync(new BsonDocument("name", "sneaky"), cancellationToken: Ct)).Should().Be(1);
    }

    [Fact]
    public async Task AnAbortedTransaction_leavesNoWritesInTheCapture()
    {
        var capture = new WriteCapture();
        await using var host = await Start(mongo, capture);
        capture.Clear();

        // The last admin cannot demote or deactivate themselves: rejected before or inside the transaction, so nothing may remain.
        var response = await host.Send(HttpMethod.Patch, $"/api/v2/users/{host.AdminId}", host.AdminId, new { role = "member" });

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        capture.Writes().Should().BeEmpty();
        capture.AuditInserts().Should().Be(0);
    }
}
