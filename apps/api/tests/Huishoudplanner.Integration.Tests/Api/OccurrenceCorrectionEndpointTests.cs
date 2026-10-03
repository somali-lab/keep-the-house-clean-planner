using System.Net;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// An administrator's correction and deletion of a completion on the real host (<c>occurrences.test.ts</c>, "lets an administrator correct and
/// permanently delete a completion"): <c>PATCH {action: edit_completion}</c> is <c>POST .../completion</c> and needs the administrator role,
/// <c>DELETE .../{id}</c> too. The ad-hoc occurrence that test created is slice 3.3; here a planned occurrence is checked off instead.
/// </summary>
public sealed class OccurrenceCorrectionEndpointTests(OccurrenceHarness h) : IClassFixture<OccurrenceHarness>
{
    private static string Type(JsonElement problem) => problem.GetProperty("type").GetString()!;

    private static string Url(string id, string action) => $"/api/v2/occurrences/{id}/{action}";

    private static ObjectId Oid(string id) => ObjectId.Parse(id);

    private object Correction(string day = "2026-09-18", string at = "2026-09-20T12:30:00.000Z", string? by = null) =>
        new { date = day, completedAt = at, completedBy = by ?? h.P2.Id };

    [Fact]
    public async Task EditCompletion_isForbiddenForAMemberAndAdministratorsCorrectTheCompletion()
    {
        var id = await h.IdOfAsync(h.Weekly, "2026-09-21");
        (await h.SendAsync(HttpMethod.Post, Url(id, "complete"), null, h.P1)).Status.Should().Be(HttpStatusCode.OK);

        var forbidden = await h.SendAsync(HttpMethod.Post, Url(id, "completion"), Correction("2026-09-22"), h.P2);
        var edited = await h.SendAsync(HttpMethod.Post, Url(id, "completion"), Correction("2026-09-22"), h.Admin);

        forbidden.Status.Should().Be(HttpStatusCode.Forbidden);
        Type(forbidden.Body).Should().Be("urn:huishoudplanner:problem:permission_denied");
        edited.Status.Should().Be(HttpStatusCode.OK, edited.Body.ToString());
        var o = edited.Body;
        (o.GetProperty("date").GetString(), o.GetProperty("status").GetString(), o.GetProperty("completedBy").GetString()).Should().Be(("2026-09-22", "done", h.P2.Id));
        o.GetProperty("completedAt").GetDateTimeOffset().Should().Be(new DateTimeOffset(2026, 9, 20, 12, 30, 0, TimeSpan.Zero));
        var entry = (await h.AuditOfAsync("occurrence", id, "update")).Should().ContainSingle().Subject;
        entry["meta"]["correction"].AsString.Should().Be("completion");
        entry["actorId"].Should().Be(Oid(h.Admin.Id));
        entry["before"]["completedBy"].Should().Be(Oid(h.P1.Id));
        entry["after"]["completedBy"].Should().Be(Oid(h.P2.Id));
        (await h.LastCompletedAtAsync(h.Weekly)).ToUniversalTime().Should().Be(new DateTime(2026, 9, 20, 12, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Delete_isForbiddenForAMemberAndAdministratorsDeleteACompletionAndLastCompletedAtFallsBack()
    {
        var earlier = await h.IdOfAsync(h.Twice, "2026-09-23");
        var id = await h.IdOfAsync(h.Twice, "2026-09-30");
        h.Clock.Set("2026-09-17T08:00:00.000Z");
        (await h.SendAsync(HttpMethod.Post, Url(earlier, "complete"), null, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        h.Clock.Set("2026-09-18T08:00:00.000Z");
        (await h.SendAsync(HttpMethod.Post, Url(id, "complete"), null, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        (await h.LastCompletedAtAsync(h.Twice)).ToUniversalTime().Should().Be(new DateTime(2026, 9, 18, 8, 0, 0, DateTimeKind.Utc));
        h.Clock.Set(OccurrenceHarness.Wednesday);

        var forbidden = await h.SendAsync(HttpMethod.Delete, $"/api/v2/occurrences/{id}", null, h.P2);
        var deleted = await h.SendAsync(HttpMethod.Delete, $"/api/v2/occurrences/{id}", null, h.Admin);
        var gone = await h.SendAsync(HttpMethod.Get, $"/api/v2/occurrences/{id}", null, null);

        forbidden.Status.Should().Be(HttpStatusCode.Forbidden);
        deleted.Status.Should().Be(HttpStatusCode.OK, deleted.Body.ToString());
        deleted.Body.GetProperty("deleted").GetBoolean().Should().BeTrue();
        gone.Status.Should().Be(HttpStatusCode.NotFound);
        var entry = (await h.AuditOfAsync("occurrence", id, "delete")).Should().ContainSingle().Subject;
        entry["meta"]["correction"].AsString.Should().Be("completion");
        entry["before"]["status"].AsString.Should().Be("done");
        (await h.LastCompletedAtAsync(h.Twice)).ToUniversalTime().Should().Be(new DateTime(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task EditCompletion_refusesWhatItCannotCorrect()
    {
        var open = await h.IdOfAsync(h.Weekly, "2026-10-05");
        var done = await h.IdOfAsync(h.Weekly, "2026-10-12");
        (await h.SendAsync(HttpMethod.Post, Url(done, "complete"), null, h.P1)).Status.Should().Be(HttpStatusCode.OK);

        var notDone = await h.SendAsync(HttpMethod.Post, Url(open, "completion"), Correction(), h.Admin);
        var notGenerated = await h.SendAsync(HttpMethod.Post, Url(done, "completion"), Correction("2026-12-01"), h.Admin);
        var unknownUser = await h.SendAsync(HttpMethod.Post, Url(done, "completion"), Correction(by: "0123456789abcdef01234567"), h.Admin);
        var missing = await h.SendAsync(HttpMethod.Post, Url(done, "completion"), new { date = "2026-10-13" }, h.Admin);
        var badInstant = await h.SendAsync(HttpMethod.Post, Url(done, "completion"), Correction(at: "2026-09-20 12:30"), h.Admin);
        var notFound = await h.SendAsync(HttpMethod.Post, Url("0123456789abcdef01234567", "completion"), Correction(), h.Admin);

        notDone.Status.Should().Be(HttpStatusCode.Conflict);
        (notDone.Body.GetProperty("status").GetString(), notDone.Body.GetProperty("action").GetString()).Should().Be(("open", "edit completion of"));
        Type(notGenerated.Body).Should().Be("urn:huishoudplanner:problem:cycle_not_generated");
        notGenerated.Body.GetProperty("date").GetString().Should().Be("2026-12-01");
        unknownUser.Body.GetProperty("errors").GetProperty("completedBy")[0].GetString().Should().Be("unknown_user");
        missing.Body.GetProperty("errors").GetProperty("completedAt")[0].GetString().Should().Be("is required");
        missing.Body.GetProperty("errors").GetProperty("completedBy")[0].GetString().Should().Be("is required");
        badInstant.Body.GetProperty("errors").TryGetProperty("completedAt", out _).Should().BeTrue();
        notFound.Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_onlyDoneWorkAndOnlyWhatExists()
    {
        var open = await h.IdOfAsync(h.Weekly, "2026-10-19");

        var notDone = await h.SendAsync(HttpMethod.Delete, $"/api/v2/occurrences/{open}", null, h.Admin);
        var unknown = await h.SendAsync(HttpMethod.Delete, "/api/v2/occurrences/0123456789abcdef01234567", null, h.Admin);
        var anonymous = await h.SendAsync(HttpMethod.Delete, $"/api/v2/occurrences/{open}", null, null);

        notDone.Status.Should().Be(HttpStatusCode.Conflict);
        Type(notDone.Body).Should().Be("urn:huishoudplanner:problem:invalid_transition");
        unknown.Status.Should().Be(HttpStatusCode.NotFound);
        anonymous.Status.Should().Be(HttpStatusCode.BadRequest);
        (await h.StoredAsync(open))["status"].AsString.Should().Be("open");
    }
}
