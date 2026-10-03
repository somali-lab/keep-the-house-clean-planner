using System.Net;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Completing and undoing on the real host and a real replica set: the complete, uncomplete and skip scenarios of <c>occurrences.test.ts</c> and
/// the whole of <c>completion-choice.test.ts</c>. <c>PATCH {action: complete}</c> is <c>POST .../complete</c>, <c>uncomplete</c> is
/// <c>POST .../uncomplete</c>. Every scenario has occurrences of its own; one harness serves the class.
/// </summary>
public sealed class OccurrenceCompleteEndpointTests(OccurrenceHarness h) : IClassFixture<OccurrenceHarness>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Type(JsonElement problem) => problem.GetProperty("type").GetString()!;

    private static string Url(string id, string action) => $"/api/v2/occurrences/{id}/{action}";

    private async Task<(HttpStatusCode Status, JsonElement Body)> CompleteAs(Huishoudplanner.Domain.Identity.UserIdentity actor, string id, object? body = null) =>
        await h.SendAsync(HttpMethod.Post, Url(id, "complete"), body, actor);

    private static ObjectId Oid(string id) => ObjectId.Parse(id);

    // ---- complete: credit, audit and lastCompletedAt (occurrences.test.ts)

    [Fact]
    public async Task Complete_creditsTheAssigneeWhenAnotherActorChecksOffForThemAndMaintainsLastCompletedAt()
    {
        // A moment later than any other test of this class uses, so the task's lastCompletedAt really changes whatever the test order is.
        h.Clock.Set("2026-09-16T09:00:00.000Z");
        var id = await h.IdOfAsync(h.Weekly, "2026-09-21");

        var response = await CompleteAs(h.P2, id, new { completedBy = h.P1.Id });

        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        var o = response.Body;
        (o.GetProperty("status").GetString(), o.GetProperty("statusBeforeCompletion").GetString(), o.GetProperty("completedBy").GetString()).Should().Be(("done", "open", h.P1.Id));
        o.GetProperty("completedAt").GetDateTimeOffset().Should().Be(new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero));
        o.GetProperty("pointsSnapshot").GetInt32().Should().Be(30);
        var entry = (await h.AuditOfAsync("occurrence", id, "complete")).Should().ContainSingle().Subject;
        entry["source"].AsString.Should().Be("ui");
        entry["actorId"].Should().Be(Oid(h.P2.Id));
        var meta = entry["meta"].AsBsonDocument;
        meta["completedBy"].Should().Be(Oid(h.P1.Id));
        meta["wasAssignee"].AsBoolean.Should().BeTrue();
        meta["occurrence"]["taskNameSnapshot"].AsString.Should().Be("Badkamer schoonmaken");
        meta["occurrence"]["roomNameSnapshot"].AsString.Should().Be("Badkamer");
        meta["occurrence"]["date"].IsValidDateTime.Should().BeTrue();
        (await h.LastCompletedAtAsync(h.Weekly)).ToUniversalTime().Should().Be(new DateTime(2026, 9, 16, 9, 0, 0, DateTimeKind.Utc));
        (await h.AuditOfAsync("task", h.Weekly, "update")).Should().Contain(e => e.Contains("meta") && e["meta"]["occurrenceId"] == Oid(id));
        h.Clock.Set(OccurrenceHarness.Wednesday);
    }

    [Fact]
    public async Task Complete_logsBothTheActorAndADifferentCompletedBy()
    {
        var id = await h.IdOfAsync(h.Weekly, "2026-09-28");

        var response = await CompleteAs(h.P1, id, new { completedBy = h.P2.Id });

        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        var entry = (await h.AuditOfAsync("occurrence", id, "complete")).Single();
        entry["actorId"].Should().Be(Oid(h.P1.Id));
        entry["meta"]["completedBy"].Should().Be(Oid(h.P2.Id));
        entry["meta"]["wasAssignee"].AsBoolean.Should().BeFalse();
        entry["after"]["completedBy"].Should().Be(Oid(h.P2.Id));
    }

    [Fact]
    public async Task Complete_takeOverTransfersAnAssignedTaskAtomically()
    {
        var id = await h.IdOfAsync(h.Weekly, "2026-10-05");

        var response = await CompleteAs(h.P2, id, new { takeOver = true });

        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        (response.Body.GetProperty("status").GetString(), response.Body.GetProperty("assigneeId").GetString(), response.Body.GetProperty("completedBy").GetString()).Should().Be(("done", h.P2.Id, h.P2.Id));
        var entry = (await h.AuditOfAsync("occurrence", id, "complete")).Single();
        entry["actorId"].Should().Be(Oid(h.P2.Id));
        var meta = entry["meta"].AsBsonDocument;
        (meta["wasAssignee"].AsBoolean, meta["takenOver"].AsBoolean).Should().Be((false, true));
        meta["completedBy"].Should().Be(Oid(h.P2.Id));
        meta["previousAssigneeId"].Should().Be(Oid(h.P1.Id));
    }

    [Fact]
    public async Task Complete_unassignedWorkIsClaimedImplicitlyForCompletedBy()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-09-16");

        var response = await CompleteAs(h.P1, id, new { completedBy = h.P2.Id });

        (response.Body.GetProperty("assigneeId").GetString(), response.Body.GetProperty("completedBy").GetString()).Should().Be((h.P2.Id, h.P2.Id));
        (await h.AuditOfAsync("occurrence", id, "complete")).Single()["meta"]["claimed"].AsBoolean.Should().BeTrue();
    }

    [Fact]
    public async Task Uncomplete_afterSkipThenDoneRestoresSkippedAsANewAuditEntry()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-09-24");
        (await h.SendAsync(HttpMethod.Post, Url(id, "skip"), new { reason = "geen tijd" }, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        var done = await CompleteAs(h.P1, id, new { takeOver = true });
        (done.Body.GetProperty("status").GetString(), done.Body.GetProperty("statusBeforeCompletion").GetString()).Should().Be(("done", "skipped"));

        var undone = await h.SendAsync(HttpMethod.Post, Url(id, "uncomplete"), null, h.P1);

        undone.Status.Should().Be(HttpStatusCode.OK, undone.Body.ToString());
        var o = undone.Body;
        (o.GetProperty("status").GetString(), o.GetProperty("skipReason").GetString()).Should().Be(("skipped", "geen tijd"));
        (o.GetProperty("statusBeforeCompletion").ValueKind, o.GetProperty("completedAt").ValueKind, o.GetProperty("completedBy").ValueKind).Should().Be((JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null));
        var history = await h.AuditOfAsync("occurrence", id);
        history.Select(e => e["action"].AsString).Should().Equal("create", "skip", "complete", "uncomplete");
        var uncomplete = history[^1];
        uncomplete["meta"]["occurrence"]["taskNameSnapshot"].AsString.Should().Be("Wastafel");
        uncomplete["after"]["pointsSnapshot"].IsBsonNull.Should().BeTrue();
    }

    [Fact]
    public async Task Uncomplete_keepsLastCompletedAtCorrectAcrossASequence()
    {
        var a = await h.IdOfAsync(h.Weekly, "2026-10-12");
        var b = await h.IdOfAsync(h.Weekly, "2026-10-19");
        var baseline = await h.LastCompletedAtAsync(h.Weekly);

        h.Clock.Set("2026-09-17T08:00:00.000Z");
        (await CompleteAs(h.P1, a)).Status.Should().Be(HttpStatusCode.OK);
        h.Clock.Set("2026-09-18T08:00:00.000Z");
        (await CompleteAs(h.P1, b)).Status.Should().Be(HttpStatusCode.OK);
        (await h.LastCompletedAtAsync(h.Weekly)).ToUniversalTime().Should().Be(new DateTime(2026, 9, 18, 8, 0, 0, DateTimeKind.Utc));

        (await h.SendAsync(HttpMethod.Post, Url(b, "uncomplete"), null, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        (await h.LastCompletedAtAsync(h.Weekly)).ToUniversalTime().Should().Be(new DateTime(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc));
        (await h.SendAsync(HttpMethod.Post, Url(a, "uncomplete"), null, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        (await h.LastCompletedAtAsync(h.Weekly)).Should().Be(baseline);

        var last = (await h.AuditOfAsync("task", h.Weekly, "update")).Last();
        last["after"]["lastCompletedAt"].Should().Be(baseline);
        h.Clock.Set(OccurrenceHarness.Wednesday);
    }

    [Fact]
    public async Task Complete_aSecondCompleteAndASkipOfDoneWorkAreInvalidTransitionsWithTheStatusAndAction()
    {
        var id = await h.IdOfAsync(h.Weekly, "2026-10-26");
        var early = await h.SendAsync(HttpMethod.Post, Url(id, "uncomplete"), null, h.P1);
        (await CompleteAs(h.P1, id)).Status.Should().Be(HttpStatusCode.OK);

        var again = await CompleteAs(h.P1, id);
        var skip = await h.SendAsync(HttpMethod.Post, Url(id, "skip"), null, h.P1);

        early.Status.Should().Be(HttpStatusCode.Conflict);
        Type(early.Body).Should().Be("urn:huishoudplanner:problem:invalid_transition");
        (early.Body.GetProperty("status").GetString(), early.Body.GetProperty("action").GetString()).Should().Be(("open", "uncomplete"));
        again.Status.Should().Be(HttpStatusCode.Conflict);
        (again.Body.GetProperty("status").GetString(), again.Body.GetProperty("action").GetString()).Should().Be(("done", "complete"));
        skip.Status.Should().Be(HttpStatusCode.Conflict);
        skip.Body.GetProperty("action").GetString().Should().Be("skip");
    }

    [Fact]
    public async Task Complete_validatesTheBodyTheCompletedByAndTheId()
    {
        var id = await h.IdOfAsync(h.Weekly, "2026-11-02");

        var unknownUser = await CompleteAs(h.P1, id, new { completedBy = "0123456789abcdef01234567" });
        var malformedUser = await CompleteAs(h.P1, id, new { completedBy = "nope" });
        var wrongType = await CompleteAs(h.P1, id, new { completedBy = 5 });
        var takeOverFalse = await CompleteAs(h.P1, id, new { takeOver = false });
        var notJson = await CompleteAs(h.P1, id, "{ nope");
        var unknownOccurrence = await CompleteAs(h.P1, "0123456789abcdef01234567");
        var badId = await CompleteAs(h.P1, "nope");
        var anonymous = await h.SendAsync(HttpMethod.Post, Url(id, "complete"), null, null);

        unknownUser.Status.Should().Be(HttpStatusCode.BadRequest);
        unknownUser.Body.GetProperty("errors").GetProperty("completedBy")[0].GetString().Should().Be("unknown_user");
        malformedUser.Body.GetProperty("errors").GetProperty("completedBy")[0].GetString().Should().Be("invalid_object_id");
        wrongType.Body.GetProperty("errors").GetProperty("completedBy")[0].GetString().Should().Be("must be a string");
        takeOverFalse.Body.GetProperty("errors").TryGetProperty("takeOver", out _).Should().BeTrue();
        notJson.Body.GetProperty("errors").GetProperty("body")[0].GetString().Should().Be("is not valid JSON");
        unknownOccurrence.Status.Should().Be(HttpStatusCode.NotFound);
        badId.Body.GetProperty("errors").GetProperty("id")[0].GetString().Should().Be("invalid_object_id");
        anonymous.Status.Should().Be(HttpStatusCode.BadRequest);
        Type(anonymous.Body).Should().Be("urn:huishoudplanner:problem:profile_required");
        (await h.StoredAsync(id))["status"].AsString.Should().Be("open");
    }

    // ---- completion-choice.test.ts

    [Fact]
    public async Task Choice_anImplicitCheckOffOfWorkOfSomeoneElseIsRefusedAndWritesNothing()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-10-08"); // Persoon 2
        var before = await h.AuditCountAsync();

        var response = await CompleteAs(h.P1, id);

        response.Status.Should().Be(HttpStatusCode.BadRequest);
        Type(response.Body).Should().Be("urn:huishoudplanner:problem:validation_error");
        response.Body.GetProperty("errors").GetProperty("completedBy")[0].GetString().Should().Be("completion_choice_required");
        (await h.AuditCountAsync()).Should().Be(before);
        (await h.StoredAsync(id))["status"].AsString.Should().Be("open");
    }

    [Fact]
    public async Task Choice_takeOverCreditsTheActorWhoBecomesTheAssignee()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-10-15");

        var response = await CompleteAs(h.P1, id, new { takeOver = true });

        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        (response.Body.GetProperty("completedBy").GetString(), response.Body.GetProperty("assigneeId").GetString()).Should().Be((h.P1.Id, h.P1.Id));
        var meta = (await h.AuditOfAsync("occurrence", id, "complete")).Single()["meta"];
        meta["takenOver"].AsBoolean.Should().BeTrue();
        meta["previousAssigneeId"].Should().Be(Oid(h.P2.Id));
    }

    [Fact]
    public async Task Choice_onBehalfCreditsTheNamedPersonAndLeavesTheAssignee()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-10-22");

        var response = await CompleteAs(h.P1, id, new { completedBy = h.P2.Id });

        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        (response.Body.GetProperty("completedBy").GetString(), response.Body.GetProperty("assigneeId").GetString()).Should().Be((h.P2.Id, h.P2.Id));
        var entry = (await h.AuditOfAsync("occurrence", id, "complete")).Single();
        entry["actorId"].Should().Be(Oid(h.P1.Id));
        entry["meta"]["completedBy"].Should().Be(Oid(h.P2.Id));
        entry["meta"]["wasAssignee"].AsBoolean.Should().BeTrue();
    }

    [Fact]
    public async Task Choice_completedByTogetherWithTakeOverIsAConflict()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-10-29");

        var response = await CompleteAs(h.P1, id, new { completedBy = h.P2.Id, takeOver = true });

        response.Status.Should().Be(HttpStatusCode.BadRequest);
        response.Body.GetProperty("errors").GetProperty("completedBy")[0].GetString().Should().Be("completion_choice_conflict");
        (await h.StoredAsync(id))["status"].AsString.Should().Be("open");
    }

    [Fact]
    public async Task Choice_workOfTheActorNeedsNoChoice()
    {
        var id = await h.IdOfAsync(h.Weekly, "2026-09-16");

        var response = await CompleteAs(h.P1, id);

        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        (response.Body.GetProperty("completedBy").GetString(), response.Body.GetProperty("assigneeId").GetString()).Should().Be((h.P1.Id, h.P1.Id));
    }

    [Fact]
    public async Task Choice_unassignedWorkNeedsNoChoiceTheActorIsCreditedAndClaimsIt()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-09-30");

        var response = await CompleteAs(h.P2, id);

        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
        (response.Body.GetProperty("completedBy").GetString(), response.Body.GetProperty("assigneeId").GetString()).Should().Be((h.P2.Id, h.P2.Id));
    }

    [Fact]
    public async Task Choice_aSecondCompleteIsInvalidTransitionBeforeAChoiceIsAsked()
    {
        var id = await h.IdOfAsync(h.Twice, "2026-11-05");
        (await CompleteAs(h.P1, id, new { takeOver = true })).Status.Should().Be(HttpStatusCode.OK);

        var again = await CompleteAs(h.P2, id);

        again.Status.Should().Be(HttpStatusCode.Conflict);
        Type(again.Body).Should().Be("urn:huishoudplanner:problem:invalid_transition");
    }

    [Fact]
    public async Task Skip_storesATrimmedReasonAndAnEmptyBodyIsAllowed()
    {
        var withReason = await h.IdOfAsync(h.Twice, "2026-11-04");
        var without = await h.IdOfAsync(h.Twice, "2026-10-21");

        var first = await h.SendAsync(HttpMethod.Post, Url(withReason, "skip"), new { reason = "  geen tijd " }, h.P1);
        var second = await h.SendAsync(HttpMethod.Post, Url(without, "skip"), null, h.P1);
        var tooLong = await h.SendAsync(HttpMethod.Post, Url(await h.IdOfAsync(h.Twice, "2026-11-05"), "skip"), new { reason = new string('x', 501) }, h.P1);

        first.Body.GetProperty("skipReason").GetString().Should().Be("geen tijd");
        second.Body.GetProperty("status").GetString().Should().Be("skipped");
        second.Body.GetProperty("skipReason").ValueKind.Should().Be(JsonValueKind.Null);
        tooLong.Status.Should().Be(HttpStatusCode.BadRequest);
    }
}
