using System.Net;
using System.Text.Json;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// The last scenario of <c>one-off-occurrences.test.ts</c>: activating another plan lists the open and the done one-off tasks under the preserved ad-hoc group and keeps them,
/// through the real activation endpoints. A class of its own, because an activation changes the active plan and replaces the open generated occurrences of the household.
/// </summary>
public sealed class AdhocOneOffActivationTests(OccurrenceHarness h) : IClassFixture<OccurrenceHarness>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<string> Ids(JsonElement list) => [.. list.EnumerateArray().Select(i => i.GetProperty("occurrenceId").GetString()!)];

    [Fact]
    public async Task Activating_anotherPlan_listsOpenAndDoneOneOffTasksAsPreservedAdhocAndKeepsThem()
    {
        h.Clock.Set("2026-09-16T17:00:00.000Z");
        var open = (await h.SendAsync(HttpMethod.Post, "/api/v2/occurrences/one-off", new { name = "Open eenmalig", roomId = h.Room, durationMinutes = 10, date = "2026-09-25" }, h.P1)).Body.GetProperty("id").GetString()!;
        var done = (await h.SendAsync(HttpMethod.Post, "/api/v2/occurrences/one-off", new { name = "Klaar eenmalig", durationMinutes = 10, date = "2026-09-16", done = true, requestId = "one-off-request-key-0060" }, h.P1)).Body.GetProperty("id").GetString()!;
        var active = (await h.SendAsync(HttpMethod.Get, "/api/v2/cycle-plans/active", null, null)).Body.GetProperty("id").GetString()!;
        var copy = await h.SendAsync(HttpMethod.Post, "/api/v2/cycle-plans", new { name = "Zelfde slots", copyFromId = active }, h.Planner);
        copy.Status.Should().Be(HttpStatusCode.Created, copy.Body.ToString());
        var planId = copy.Body.GetProperty("id").GetString()!;

        var previewResponse = await h.SendAsync(HttpMethod.Get, $"/api/v2/cycle-plans/{planId}/activation-preview", null, h.Planner);

        previewResponse.Status.Should().Be(HttpStatusCode.OK, previewResponse.Body.ToString());
        var preview = previewResponse.Body;
        var listed = preview.GetProperty("preserved").GetProperty("adhoc").EnumerateArray().Where(i => i.GetProperty("taskId").ValueKind == JsonValueKind.Null).ToList();
        listed.Select(i => i.GetProperty("occurrenceId").GetString()).Should().Contain([open, done]);
        listed.Single(i => i.GetProperty("occurrenceId").GetString() == open).GetProperty("taskName").GetString().Should().Be("Open eenmalig");
        Ids(preview.GetProperty("removed")).Should().NotContain(open);

        var activated = await h.SendAsync(HttpMethod.Post, $"/api/v2/cycle-plans/{planId}/activation", new { previewToken = preview.GetProperty("previewToken").GetString() }, h.Planner);
        activated.Status.Should().Be(HttpStatusCode.OK, activated.Body.ToString());
        using (var scope = h.Services.CreateScope())
        {
            var run = await scope.ServiceProvider.GetRequiredService<IGenerationService>().GenerateUpcomingAsync(AuditActor.System, "one-off-activation", Ct);
            run.IsT0.Should().BeTrue();
        }

        var remaining = await h.Occurrences.Find(new BsonDocument("taskId", BsonNull.Value)).ToListAsync(Ct);
        remaining.Select(o => o["_id"].AsObjectId.ToString()).Should().Contain([open, done]);
        var stillOpen = remaining.Single(o => o["_id"].AsObjectId.ToString() == open);
        (stillOpen["status"].AsString, stillOpen["origin"].AsString).Should().Be(("open", "adhoc"));
    }
}
