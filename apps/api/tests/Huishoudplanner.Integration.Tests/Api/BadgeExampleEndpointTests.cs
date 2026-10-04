#pragma warning disable CA1861 // inline arrays in request bodies of tests
using System.Net;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;
using static Huishoudplanner.Integration.Tests.Api.BadgeDefinitionEndpointTests;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// <c>badges.test.ts</c>, the example badges (ADR-0014): created once by their stable key, only on request, about the active tasks they are found on by
/// name, in Dutch or English, editable afterwards. Every scenario has a household of its own.
/// </summary>
public sealed class BadgeExampleEndpointTests(MongoContainerFixture mongo)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString()!;

    private static string[] TaskIds(JsonElement badge) =>
        [.. badge.GetProperty("rule").GetProperty("taskIds").EnumerateArray().Select(t => t.GetString()!)];

    [Fact]
    public async Task TheThreeExamples_areCreatedOnce_withTheTasksTheyAreAbout_andAgainChangesNothing()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);

        var first = await h.SendAsync(HttpMethod.Post, "/api/v2/badges/examples", new { language = "nl" }, h.P1);

        first.Status.Should().Be(HttpStatusCode.OK, first.Body.ToString());
        first.Body.GetProperty("skipped").GetInt32().Should().Be(0);
        var created = first.Body.GetProperty("created").EnumerateArray().ToList();
        created.Select(b => (Str(b, "name"), Str(b, "exampleKey"), b.GetProperty("active").GetBoolean(), b.GetProperty("image").ValueKind)).Should().Equal(
            ("Alles op tijd", "example:on_time", true, JsonValueKind.Null),
            ("Toiletjuffrouw", "example:toilet", true, JsonValueKind.Null),
            ("Dweilkampioen", "example:mop", true, JsonValueKind.Null));
        (Str(created[0].GetProperty("rule"), "type"), created[0].GetProperty("rule").GetProperty("threshold").GetInt32()).Should().Be(("onTimeWeeks", 4));
        created[0].GetProperty("rule").TryGetProperty("taskIds", out _).Should().BeFalse();
        (Str(created[1].GetProperty("rule"), "type"), created[1].GetProperty("rule").GetProperty("threshold").GetInt32()).Should().Be(("executions", 10));
        TaskIds(created[1]).Should().Equal(h.Toilet);
        (Str(created[2].GetProperty("rule"), "type"), created[2].GetProperty("rule").GetProperty("threshold").GetInt32()).Should().Be(("minutes", 300));
        TaskIds(created[2]).Should().Equal(h.Mop);
        var entries = (await h.AuditAsync("badge")).Where(e => e["action"].AsString == "create").ToList();
        entries.Should().HaveCount(3);
        entries[1]["meta"]["example"].AsString.Should().Be("example:toilet");

        var second = await h.CapturedAsync(HttpMethod.Post, "/api/v2/badges/examples", new { language = "en" }, h.P1);

        second.Body.GetProperty("created").GetArrayLength().Should().Be(0);
        second.Body.GetProperty("skipped").GetInt32().Should().Be(3);
        second.Writes.Should().BeEmpty();
        second.AuditInserts.Should().Be(0);
        (await h.Badges.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(3);
    }

    [Fact]
    public async Task AnExample_keepsItsKeyWhenRenamed_andIsNotBroughtBackAsADuplicate()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var created = (await h.SendAsync(HttpMethod.Post, "/api/v2/badges/examples", new { language = "nl" }, h.P1)).Body.GetProperty("created").EnumerateArray().ToList();
        var toilet = created.Single(b => Str(b, "exampleKey") == "example:toilet");

        var renamed = await h.SendAsync(HttpMethod.Patch, $"/api/v2/badges/{Str(toilet, "id")}", new { name = "Toiletkoningin", rule = Executions([h.Toilet], 2) }, h.P1);
        var again = await h.SendAsync(HttpMethod.Post, "/api/v2/badges/examples", new { language = "nl" }, h.P1);

        (Str(renamed.Body, "name"), Str(renamed.Body, "exampleKey"), renamed.Body.GetProperty("rule").GetProperty("threshold").GetInt32()).Should().Be(("Toiletkoningin", "example:toilet", 2));
        again.Body.GetProperty("created").GetArrayLength().Should().Be(0);
        again.Body.GetProperty("skipped").GetInt32().Should().Be(3);
        (await h.GetAsync("/api/v2/badges")).Body.GetProperty("items").EnumerateArray().Select(b => Str(b, "name")).Should().Equal("Alles op tijd", "Toiletkoningin", "Dweilkampioen");
    }

    [Fact]
    public async Task EnglishNames_areWrittenOnRequest_andOtherLanguagesAreRefused()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);

        var english = await h.SendAsync(HttpMethod.Post, "/api/v2/badges/examples", new { language = "en" }, h.P1);
        var french = await h.SendAsync(HttpMethod.Post, "/api/v2/badges/examples", new { language = "fr" }, h.P1);
        var number = await h.SendAsync(HttpMethod.Post, "/api/v2/badges/examples", new { language = 3 }, h.P1);

        english.Body.GetProperty("created").EnumerateArray().Select(b => Str(b, "name")).Should().Equal("Always on time", "Toilet Champion", "Mop Champion");
        french.Status.Should().Be(HttpStatusCode.BadRequest);
        french.Body.GetProperty("errors").TryGetProperty("language", out _).Should().BeTrue();
        number.Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WithoutABodyOrWithAnEmptyObject_theLanguageIsDutch()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);

        var none = await h.SendAsync(HttpMethod.Post, "/api/v2/badges/examples", null, h.P1);
        var empty = await h.SendAsync(HttpMethod.Post, "/api/v2/badges/examples", "{}", h.P1);

        none.Status.Should().Be(HttpStatusCode.OK, none.Body.ToString());
        none.Body.GetProperty("created").EnumerateArray().Select(b => Str(b, "name")).Should().Equal("Alles op tijd", "Toiletjuffrouw", "Dweilkampioen");
        empty.Body.GetProperty("skipped").GetInt32().Should().Be(3);
        (await h.SendAsync(HttpMethod.Post, "/api/v2/badges/examples", "[1]", h.P1)).Status.Should().Be(HttpStatusCode.BadRequest);
        (await h.SendAsync(HttpMethod.Post, "/api/v2/badges/examples", "{ nope", h.P1)).Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AnExampleAboutTasksThatAreNotActive_isInactive_soItCannotSilentlyCountEveryTask()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        foreach (var task in new[] { h.Toilet, h.Mop })
        {
            (await h.SendAsync(HttpMethod.Patch, $"/api/v2/tasks/{task}", new { active = false }, h.P1)).Status.Should().Be(HttpStatusCode.OK);
        }

        var created = (await h.SendAsync(HttpMethod.Post, "/api/v2/badges/examples", new { }, h.P1)).Body.GetProperty("created").EnumerateArray().ToList();

        created.Select(b => (Str(b, "exampleKey"), b.GetProperty("active").GetBoolean())).Should().Equal(
            ("example:on_time", true), ("example:toilet", false), ("example:mop", false));
        TaskIds(created[1]).Should().BeEmpty();
    }

    [Fact]
    public async Task TheTasksOfAnExample_areStoredInTheStableOrder()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var second = await h.SendAsync(HttpMethod.Post, "/api/v2/tasks", new { name = "WC poetsen", roomId = h.Room, intervalKey = "1w", durationMinutes = 5 }, h.P1);
        second.Status.Should().Be(HttpStatusCode.Created, second.Body.ToString());

        var created = (await h.SendAsync(HttpMethod.Post, "/api/v2/badges/examples", new { language = "nl" }, h.P1)).Body.GetProperty("created").EnumerateArray().ToList();

        TaskIds(created.Single(b => Str(b, "exampleKey") == "example:toilet")).Should().Equal(new[] { h.Toilet, Str(second.Body, "id") }.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnExample_isAwardedFromTheDataLikeAnyOtherBadge()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var created = (await h.SendAsync(HttpMethod.Post, "/api/v2/badges/examples", new { language = "nl" }, h.P1)).Body.GetProperty("created").EnumerateArray().ToList();
        var toilet = Str(created.Single(b => Str(b, "exampleKey") == "example:toilet"), "id");
        await h.SendAsync(HttpMethod.Patch, $"/api/v2/badges/{toilet}", new { rule = Executions([h.Toilet], 1) }, h.P1);

        var done = await h.CompleteAtAsync("2026-09-16T08:00:00.000Z", await h.OccurrenceAsync("2026-09-16", h.Toilet));

        done.Status.Should().Be(HttpStatusCode.OK, done.Body.ToString());
        (await h.HoldersAsync(toilet)).Should().Equal(new Dictionary<string, string> { ["p1"] = "2026-09-16T08:00:00.000Z" });
    }
}
