using System.Net;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Rescheduling, assigning and claiming on the real host and a real replica set: <c>reschedule.test.ts</c> (the reschedule and assign
/// scenarios) and the claim scenarios of <c>occurrences.test.ts</c>, including the claim race. <c>PATCH {action: reschedule}</c> is
/// <c>POST .../reschedule</c>, <c>assign</c> is <c>POST .../assignment</c>. Persoon 2 cannot do Tuesdays.
/// </summary>
public sealed class OccurrenceMoveEndpointTests(OccurrenceHarness h) : IClassFixture<OccurrenceHarness>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Type(JsonElement problem) => problem.GetProperty("type").GetString()!;

    private static string Url(string id, string action) => $"/api/v2/occurrences/{id}/{action}";

    private static ObjectId Oid(string id) => ObjectId.Parse(id);

    private async Task<(HttpStatusCode Status, JsonElement Body)> Reschedule(string id, string day) =>
        await h.SendAsync(HttpMethod.Post, Url(id, "reschedule"), new { date = day }, h.P1);

    private async Task<(HttpStatusCode Status, JsonElement Body)> Assign(string id, string? who) =>
        await h.SendAsync(HttpMethod.Post, Url(id, "assignment"), new { assigneeId = who }, h.P1);

    // ---- reschedule

    [Fact]
    public async Task Reschedule_movesTheOccurrenceKeepsThePlannedDayAndAuditsFromAndToDayKeys()
    {
        var id = await h.IdOfAsync(h.Weekly, "2026-09-14");

        var response = await Reschedule(id, "2026-09-16");

        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        var o = response.Body;
        (o.GetProperty("date").GetString(), o.GetProperty("plannedDate").GetString(), o.GetProperty("movedFrom").GetString()).Should().Be(("2026-09-16", "2026-09-14", "2026-09-14"));
        o.GetProperty("warnings").GetArrayLength().Should().Be(0);
        var entry = (await h.AuditOfAsync("occurrence", id, "reschedule")).Should().ContainSingle().Subject;
        entry["source"].AsString.Should().Be("ui");
        (entry["meta"]["from"].AsString, entry["meta"]["to"].AsString).Should().Be(("2026-09-14", "2026-09-16"));
        entry["meta"]["occurrence"]["taskNameSnapshot"].AsString.Should().Be("Badkamer schoonmaken");
        entry["meta"]["occurrence"]["roomNameSnapshot"].AsString.Should().Be("Badkamer");
        entry["before"]["date"].ToUniversalTime().Should().Be(new DateTime(2026, 9, 13, 22, 0, 0, DateTimeKind.Utc));
        entry["after"]["date"].ToUniversalTime().Should().Be(new DateTime(2026, 9, 15, 22, 0, 0, DateTimeKind.Utc));
        entry["before"].AsBsonDocument.Names.Should().Equal("date");
    }

    [Fact]
    public async Task Reschedule_toADayTheAssigneeCannotDoIsAllowedWithAWarning()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-10-22"); // Persoon 2

        var response = await Reschedule(id, "2026-10-20"); // a Tuesday

        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        response.Body.GetProperty("date").GetString().Should().Be("2026-10-20");
        var warning = response.Body.GetProperty("warnings").EnumerateArray().Should().ContainSingle().Subject;
        warning.GetProperty("code").GetString().Should().Be("assignee_unavailable");
        (warning.GetProperty("details").GetProperty("userId").GetString(), warning.GetProperty("details").GetProperty("weekday").GetInt32()).Should().Be((h.P2.Id, 2));
    }

    [Fact]
    public async Task Reschedule_toTheOtherCycleMovesTheOccurrenceToThatCycle()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-10-08");
        var cycles = await h.Database.GetCollection<BsonDocument>("cycles").Find(FilterDefinition<BsonDocument>.Empty).Sort(Builders<BsonDocument>.Sort.Ascending("index")).ToListAsync(Ct);
        (await h.StoredAsync(id))["cycleId"].Should().Be(cycles[0]["_id"]);

        var response = await Reschedule(id, "2026-10-12");

        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        (await h.StoredAsync(id))["cycleId"].Should().Be(cycles[1]["_id"]);
        (response.Body.GetProperty("date").GetString(), response.Body.GetProperty("plannedDate").GetString(), response.Body.GetProperty("cycleIndex").GetInt32()).Should().Be(("2026-10-12", "2026-10-08", 1));
    }

    [Fact]
    public async Task Reschedule_backToThePlannedDayClearsMovedFrom()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-10-15");
        (await Reschedule(id, "2026-10-16")).Status.Should().Be(HttpStatusCode.OK);

        var back = await Reschedule(id, "2026-10-15");

        back.Body.GetProperty("date").GetString().Should().Be("2026-10-15");
        back.Body.GetProperty("movedFrom").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Reschedule_toTheSameDayWritesAndAuditsNothing()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-10-29");
        var before = await h.AuditCountAsync();
        var updatedAt = (await h.StoredAsync(id))["updatedAt"];

        var response = await Reschedule(id, "2026-10-29");

        response.Status.Should().Be(HttpStatusCode.OK);
        (await h.AuditCountAsync()).Should().Be(before);
        (await h.StoredAsync(id))["updatedAt"].Should().Be(updatedAt);
    }

    [Fact]
    public async Task Reschedule_refusesDaysThatAreNotGeneratedFinishedOccurrencesAndBadDates()
    {
        var id = await h.IdOfAsync(h.Weekly, "2026-10-26");

        var outside = await Reschedule(id, "2026-12-01");
        var badDate = await h.SendAsync(HttpMethod.Post, Url(id, "reschedule"), new { date = "1-12-2026" }, h.P1);
        var noDate = await h.SendAsync(HttpMethod.Post, Url(id, "reschedule"), new { }, h.P1);
        (await h.SendAsync(HttpMethod.Post, Url(id, "complete"), null, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        var done = await Reschedule(id, "2026-10-27");

        outside.Status.Should().Be(HttpStatusCode.Conflict);
        Type(outside.Body).Should().Be("urn:huishoudplanner:problem:cycle_not_generated");
        outside.Body.GetProperty("date").GetString().Should().Be("2026-12-01");
        badDate.Status.Should().Be(HttpStatusCode.BadRequest);
        badDate.Body.GetProperty("errors").GetProperty("date")[0].GetString().Should().Be("invalid_day_key");
        noDate.Body.GetProperty("errors").GetProperty("date")[0].GetString().Should().Be("is required");
        done.Status.Should().Be(HttpStatusCode.Conflict);
        Type(done.Body).Should().Be("urn:huishoudplanner:problem:invalid_transition");
    }

    // ---- assign

    [Fact]
    public async Task Assign_reassignsWithAnAuditEntryAndWarnsWhenTheNewAssigneeIsUnavailableThatDay()
    {
        var id = await h.IdOfAsync(h.Weekly, "2026-09-28"); // Monday, Persoon 1

        var first = await Assign(id, h.P2.Id);

        first.Status.Should().Be(HttpStatusCode.OK, first.Body.ToString());
        (first.Body.GetProperty("assigneeId").GetString(), first.Body.GetProperty("warnings").GetArrayLength()).Should().Be((h.P2.Id, 0));
        var entry = (await h.AuditOfAsync("occurrence", id, "assign")).Should().ContainSingle().Subject;
        entry["before"].Should().Be(new BsonDocument("assigneeId", Oid(h.P1.Id)));
        entry["after"].Should().Be(new BsonDocument("assigneeId", Oid(h.P2.Id)));

        // move it to a Tuesday, then give it back to Persoon 1 and once more to Persoon 2
        (await Reschedule(id, "2026-09-29")).Status.Should().Be(HttpStatusCode.OK);
        (await Assign(id, h.P1.Id)).Body.GetProperty("warnings").GetArrayLength().Should().Be(0);
        var back = await Assign(id, h.P2.Id);
        back.Body.GetProperty("warnings").EnumerateArray().Should().ContainSingle().Which.GetProperty("code").GetString().Should().Be("assignee_unavailable");
    }

    [Fact]
    public async Task Assign_anyoneIsNullAndAnUnknownOrMissingAssigneeIsRefused()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-11-05"); // Persoon 2

        var unknown = await Assign(id, "0123456789abcdef01234567");
        var missing = await h.SendAsync(HttpMethod.Post, Url(id, "assignment"), new { }, h.P1);
        var malformed = await Assign(id, "nope");
        var anyone = await Assign(id, null);
        var anonymous = await h.SendAsync(HttpMethod.Post, Url(id, "assignment"), new { assigneeId = h.P1.Id }, null);

        unknown.Status.Should().Be(HttpStatusCode.BadRequest);
        unknown.Body.GetProperty("errors").GetProperty("assigneeId")[0].GetString().Should().Be("unknown_user");
        missing.Body.GetProperty("errors").GetProperty("assigneeId")[0].GetString().Should().Be("is required");
        malformed.Body.GetProperty("errors").GetProperty("assigneeId")[0].GetString().Should().Be("invalid_object_id");
        anyone.Status.Should().Be(HttpStatusCode.OK);
        anyone.Body.GetProperty("assigneeId").ValueKind.Should().Be(JsonValueKind.Null);
        anonymous.Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Assign_thePersonWhoAlreadyHasItWritesNothing()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-11-04");
        (await Assign(id, h.P1.Id)).Status.Should().Be(HttpStatusCode.OK);
        var before = await h.AuditCountAsync();

        var again = await Assign(id, h.P1.Id);

        again.Status.Should().Be(HttpStatusCode.OK);
        (await h.AuditCountAsync()).Should().Be(before);
    }

    // ---- claim

    [Fact]
    public async Task Claim_takesAnUnassignedOccurrenceForTheActorAndASecondClaimIsAlreadyClaimed()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-09-30");

        var claimed = await h.SendAsync(HttpMethod.Post, Url(id, "claim"), null, h.P2);
        var again = await h.SendAsync(HttpMethod.Post, Url(id, "claim"), null, h.P1);

        claimed.Status.Should().Be(HttpStatusCode.OK, claimed.Body.ToString());
        claimed.Body.GetProperty("assigneeId").GetString().Should().Be(h.P2.Id);
        var entry = (await h.AuditOfAsync("occurrence", id, "assign")).Should().ContainSingle().Subject;
        entry["meta"]["claim"].AsBoolean.Should().BeTrue();
        entry["actorId"].Should().Be(Oid(h.P2.Id));
        again.Status.Should().Be(HttpStatusCode.Conflict);
        Type(again.Body).Should().Be("urn:huishoudplanner:problem:already_claimed");
    }

    [Fact]
    public async Task Claim_aSkippedOccurrenceIsInvalidTransitionAndNothingIsWritten()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-10-14");
        (await h.SendAsync(HttpMethod.Post, Url(id, "skip"), null, h.P1)).Status.Should().Be(HttpStatusCode.OK);

        var response = await h.SendAsync(HttpMethod.Post, Url(id, "claim"), null, h.P2);

        response.Status.Should().Be(HttpStatusCode.Conflict);
        Type(response.Body).Should().Be("urn:huishoudplanner:problem:invalid_transition");
        (response.Body.GetProperty("status").GetString(), response.Body.GetProperty("action").GetString()).Should().Be(("skipped", "claim"));
        (await h.AuditOfAsync("occurrence", id, "assign")).Should().BeEmpty();
        (await h.StoredAsync(id))["assigneeId"].IsBsonNull.Should().BeTrue();
    }

    [Fact]
    public async Task Claim_anOccurrenceThatHasAnAssigneeIsAlreadyClaimedAndAnUnknownOneIsNotFound()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-10-01");

        var taken = await h.SendAsync(HttpMethod.Post, Url(id, "claim"), null, h.P1);
        var unknown = await h.SendAsync(HttpMethod.Post, Url("0123456789abcdef01234567", "claim"), null, h.P1);
        var anonymous = await h.SendAsync(HttpMethod.Post, Url(id, "claim"), null, null);

        taken.Status.Should().Be(HttpStatusCode.Conflict);
        unknown.Status.Should().Be(HttpStatusCode.NotFound);
        anonymous.Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Claim_aRaceHasExactlyOneWinner()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-10-07");

        var results = await Task.WhenAll(
            h.SendAsync(HttpMethod.Post, Url(id, "claim"), null, h.P1),
            h.SendAsync(HttpMethod.Post, Url(id, "claim"), null, h.P2));

        results.Select(r => (int)r.Status).Order().Should().Equal(200, 409);
        var winner = results.Single(r => r.Status == HttpStatusCode.OK).Body.GetProperty("assigneeId").GetString();
        (await h.AuditOfAsync("occurrence", id, "assign")).Should().ContainSingle();
        (await h.StoredAsync(id))["assigneeId"].Should().Be(Oid(winner!));
        Type(results.Single(r => r.Status == HttpStatusCode.Conflict).Body).Should().Be("urn:huishoudplanner:problem:already_claimed");
    }
}
