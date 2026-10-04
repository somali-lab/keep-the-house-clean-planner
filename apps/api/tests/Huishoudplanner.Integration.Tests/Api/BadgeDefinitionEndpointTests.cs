#pragma warning disable CA1861 // inline arrays in request bodies of tests
using System.Net;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// <c>badges.test.ts</c> on the real host and a real replica set (ADR-0014): the badge definitions (administrators only, audited, a no-op writes nothing),
/// their pictures (checked bytes, cached for good under a versioned address) and the limits. Every scenario has a household of its own. The awards are
/// in <c>BadgeAwardEndpointTests</c>, the examples in <c>BadgeExampleEndpointTests</c>.
/// </summary>
public sealed class BadgeDefinitionEndpointTests(MongoContainerFixture mongo)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static object Executions(string[] tasks, int threshold) => new { type = "executions", taskIds = tasks, threshold };

    internal static object Minutes(string[] tasks, int threshold) => new { type = "minutes", taskIds = tasks, threshold };

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString()!;

    private static string[] Messages(JsonElement problem, string field) =>
        [.. problem.GetProperty("errors").GetProperty(field).EnumerateArray().Select(m => m.GetString()!)];

    private static string Type(JsonElement problem) => problem.GetProperty("type").GetString()!;

    private static async Task<List<BsonDocument>> UpdatesAsync(BadgeHarness h) =>
        [.. (await h.AuditAsync("badge")).Where(e => e["action"].AsString == "update")];

    // ---- definitions

    [Fact]
    public async Task AnAdministrator_createsChangesAndDeletesABadge_withOneAuditEntryPerRealChange()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var created = await h.SendAsync(HttpMethod.Post, "/api/v2/badges", new
        {
            name = "  Toiletjuffrouw  ",
            description = "Het toilet vaak schoongemaakt",
            rule = Executions([h.Toilet], 10),
            image = SampleImages.Input(SampleImages.Png, "image/png"),
        }, h.P1);

        created.Status.Should().Be(HttpStatusCode.Created, created.Body.ToString());
        var badge = created.Body;
        var id = Str(badge, "id");
        (Str(badge, "name"), Str(badge, "description"), badge.GetProperty("active").GetBoolean(), badge.GetProperty("exampleKey").ValueKind).Should()
            .Be(("Toiletjuffrouw", "Het toilet vaak schoongemaakt", true, JsonValueKind.Null));
        badge.GetProperty("rule").GetProperty("taskIds").EnumerateArray().Select(t => t.GetString()).Should().Equal(h.Toilet);
        var image = badge.GetProperty("image");
        (Str(image, "contentType"), image.GetProperty("size").GetInt32()).Should().Be(("image/png", SampleImages.Png.Length));
        Str(image, "url").Should().MatchRegex($"^/api/v2/badges/{id}/image\\?v=[0-9a-f]{{12}}$");
        image.TryGetProperty("data", out _).Should().BeFalse("the bytes never travel in a badge");

        var creates = await h.AuditAsync("badge");
        var create = creates.Should().ContainSingle().Subject;
        (create["action"].AsString, create["source"].AsString).Should().Be(("create", "ui"));
        create["after"]["name"].AsString.Should().Be("Toiletjuffrouw");
        create["after"]["image"]["size"].AsInt32.Should().Be(SampleImages.Png.Length);
        create.ToJson().Should().NotContain(Convert.ToBase64String(SampleImages.Png), "an image is audited by its hash, never by its bytes");

        var renamed = await h.SendAsync(HttpMethod.Patch, $"/api/v2/badges/{id}", new { name = "Toiletkoningin", rule = Executions([h.Toilet], 12) }, h.P1);
        renamed.Status.Should().Be(HttpStatusCode.OK, renamed.Body.ToString());
        (Str(renamed.Body, "name"), renamed.Body.GetProperty("rule").GetProperty("threshold").GetInt32()).Should().Be(("Toiletkoningin", 12));
        var update = (await UpdatesAsync(h)).Should().ContainSingle().Subject;
        update["before"]["name"].AsString.Should().Be("Toiletjuffrouw");
        update["before"]["rule"]["threshold"].AsInt32.Should().Be(10);
        update["after"]["rule"]["threshold"].AsInt32.Should().Be(12);

        // Equal values are a no-op: nothing is written and nothing is audited.
        var same = await h.CapturedAsync(HttpMethod.Patch, $"/api/v2/badges/{id}", new { name = "Toiletkoningin", rule = Executions([h.Toilet], 12), active = true }, h.P1);
        same.Status.Should().Be(HttpStatusCode.OK);
        same.Writes.Should().BeEmpty();
        same.AuditInserts.Should().Be(0);

        var list = await h.GetAsync("/api/v2/badges");
        list.Body.GetProperty("items").EnumerateArray().Select(b => Str(b, "name")).Should().Equal("Toiletkoningin");
        list.Body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);

        var removed = await h.SendAsync(HttpMethod.Delete, $"/api/v2/badges/{id}", null, h.P1);
        removed.Body.GetProperty("deleted").GetBoolean().Should().BeTrue();
        (await h.AuditAsync("badge")).Count(e => e["action"].AsString == "delete").Should().Be(1);
        (await h.SendAsync(HttpMethod.Delete, $"/api/v2/badges/{id}", null, h.P1)).Status.Should().Be(HttpStatusCode.NotFound);
        (await h.SendAsync(HttpMethod.Patch, $"/api/v2/badges/{id}", new { name = "Weg" }, h.P1)).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Writes_areForAdministrators_aMemberAndARequestWithoutAProfileAreRefused_readingNeedsNoProfile()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = await h.AddBadgeAsync(new { name = "Alles", rule = Executions([], 5) });
        var id = Str(badge, "id");
        var writes = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Post, "/api/v2/badges", new { name = "Nieuw", rule = Executions([], 1) }),
            (HttpMethod.Post, "/api/v2/badges/examples", new { language = "nl" }),
            (HttpMethod.Patch, $"/api/v2/badges/{id}", new { name = "Anders" }),
            (HttpMethod.Delete, $"/api/v2/badges/{id}", null),
        };

        foreach (var (method, url, body) in writes)
        {
            var member = await h.SendAsync(method, url, body, h.P2);
            member.Status.Should().Be(HttpStatusCode.Forbidden, $"{method} {url}");
            Type(member.Body).Should().EndWith(":permission_denied");
            var anonymous = await h.SendAsync(method, url, body, null);
            anonymous.Status.Should().Be(HttpStatusCode.BadRequest, $"{method} {url}");
            Type(anonymous.Body).Should().EndWith(":profile_required");
        }

        (await h.GetAsync("/api/v2/badges")).Status.Should().Be(HttpStatusCode.OK);
        (await h.GetAsync("/api/v2/badges/awards")).Status.Should().Be(HttpStatusCode.OK);
        (await h.GetAsync($"/api/v2/badges/progress?personId={h.P1.Id}")).Status.Should().Be(HttpStatusCode.OK);
        (await h.Badges.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(1);
    }

    [Theory]
    [InlineData("""{ "name": "   ", "rule": { "type": "executions", "taskIds": [], "threshold": 1 } }""", "name")]
    [InlineData("""{ "name": "Naam", "rule": { "type": "executions", "taskIds": [], "threshold": 0 } }""", "rule.threshold")]
    [InlineData("""{ "name": "Naam", "rule": { "type": "minutes", "taskIds": [], "threshold": 1.5 } }""", "rule.threshold")]
    [InlineData("""{ "name": "Naam", "rule": { "type": "executions", "taskIds": [], "threshold": 100001 } }""", "rule.threshold")]
    [InlineData("""{ "name": "Naam", "rule": { "type": "onTimeWeeks", "threshold": 1001 } }""", "rule.threshold")]
    [InlineData("""{ "name": "Naam", "rule": { "type": "streak", "threshold": 3 } }""", "rule.type")]
    [InlineData("""{ "name": "Naam", "rule": { "type": "executions", "taskIds": ["nope"], "threshold": 1 } }""", "rule.taskIds")]
    [InlineData("""{ "name": "Naam" }""", "rule")]
    [InlineData("""{ "rule": { "type": "executions", "taskIds": [], "threshold": 1 } }""", "name")]
    [InlineData("""{ "name": "Naam", "active": "yes", "rule": { "type": "executions", "taskIds": [], "threshold": 1 } }""", "active")]
    public async Task ABadFieldIsAValidationError_keyedByField_andNothingIsStored(string body, string field)
    {
        await using var h = await BadgeHarness.StartAsync(mongo);

        var response = await h.SendAsync(HttpMethod.Post, "/api/v2/badges", body, h.P1);

        response.Status.Should().Be(HttpStatusCode.BadRequest, response.Body.ToString());
        Type(response.Body).Should().EndWith(":validation_error");
        response.Body.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue(response.Body.ToString());
        (await h.Badges.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(0);
    }

    [Fact]
    public async Task ANameOf61Characters_isRefused_and60IsAccepted()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);

        var long61 = await h.SendAsync(HttpMethod.Post, "/api/v2/badges", new { name = new string('x', 61), rule = Executions([], 1) }, h.P1);
        var exact = await h.SendAsync(HttpMethod.Post, "/api/v2/badges", new { name = new string('x', 60), rule = Executions([], 1) }, h.P1);

        long61.Status.Should().Be(HttpStatusCode.BadRequest);
        long61.Body.GetProperty("errors").TryGetProperty("name", out _).Should().BeTrue();
        exact.Status.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ADescriptionOf201Characters_isRefused_and200IsAccepted()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);

        var long201 = await h.SendAsync(HttpMethod.Post, "/api/v2/badges", new { name = "Naam", description = new string('x', 201), rule = Executions([], 1) }, h.P1);
        var exact = await h.SendAsync(HttpMethod.Post, "/api/v2/badges", new { name = "Naam", description = new string('x', 200), rule = Executions([], 1) }, h.P1);

        long201.Status.Should().Be(HttpStatusCode.BadRequest);
        long201.Body.GetProperty("errors").TryGetProperty("description", out _).Should().BeTrue();
        exact.Status.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task MalformedJsonAnEmptyBodyAndANullImage_areValidationErrorsToo()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);

        var malformed = await h.SendAsync(HttpMethod.Post, "/api/v2/badges", "{ not json", h.P1);
        var empty = await h.SendAsync(HttpMethod.Post, "/api/v2/badges", null, h.P1);
        var nullImage = await h.SendAsync(HttpMethod.Post, "/api/v2/badges", """{ "name": "Naam", "rule": { "type": "executions", "taskIds": [], "threshold": 1 }, "image": null }""", h.P1);

        malformed.Status.Should().Be(HttpStatusCode.BadRequest);
        empty.Status.Should().Be(HttpStatusCode.BadRequest);
        nullImage.Status.Should().Be(HttpStatusCode.BadRequest);
        Messages(malformed.Body, "body").Should().NotBeEmpty();
        nullImage.Body.GetProperty("errors").TryGetProperty("image", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ARuleThatNamesATaskThatDoesNotExist_isRefused_andAMalformedTaskIdToo()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);

        var unknown = await h.SendAsync(HttpMethod.Post, "/api/v2/badges", new { name = "Naam", rule = Executions([ObjectId.GenerateNewId().ToString()], 1) }, h.P1);
        var malformed = await h.SendAsync(HttpMethod.Post, "/api/v2/badges", new { name = "Naam", rule = Executions(["nope"], 1) }, h.P1);

        unknown.Status.Should().Be(HttpStatusCode.BadRequest);
        Messages(unknown.Body, "rule.taskIds").Should().Equal("unknown_task");
        malformed.Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TheTasksOfARule_areStoredOnceAndInAStableOrder()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);

        var badge = await h.AddBadgeAsync(new { name = "Dubbel", rule = Executions([h.Mop, h.Toilet, h.Mop], 3) });

        badge.GetProperty("rule").GetProperty("taskIds").EnumerateArray().Select(t => t.GetString()).Should().Equal(new[] { h.Toilet, h.Mop }.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TasksThatDoNotExist_areDroppedFromARule_butARuleOfOnlySuchTasksIsRefused()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var gone = ObjectId.GenerateNewId().ToString();

        var badge = await h.AddBadgeAsync(new { name = "Half", rule = Executions([h.Toilet, gone], 2) });
        var refused = await h.SendAsync(HttpMethod.Patch, $"/api/v2/badges/{Str(badge, "id")}", new { rule = Executions([gone], 2) }, h.P1);

        badge.GetProperty("rule").GetProperty("taskIds").EnumerateArray().Select(t => t.GetString()).Should().Equal(h.Toilet);
        refused.Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task OnTimeWeeksRule_hasNoTasks()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);

        var badge = await h.AddBadgeAsync(new { name = "Op tijd", rule = new { type = "onTimeWeeks", threshold = 4, taskIds = new[] { h.Toilet } } });

        badge.GetProperty("rule").TryGetProperty("taskIds", out _).Should().BeFalse();
        (await h.Badges.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Ct))["rule"].AsBsonDocument.Contains("taskIds").Should().BeFalse();
    }

    [Fact]
    public async Task TheList_isPagedOldestFirst_canBeFilteredByActive_andRefusesABadQuery()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        h.Clock.Set("2026-09-16T08:00:00.000Z");
        await h.AddBadgeAsync(new { name = "Een", rule = Executions([], 1) });
        h.Clock.Set("2026-09-16T08:01:00.000Z");
        await h.AddBadgeAsync(new { name = "Twee", rule = Executions([], 1), active = false });
        h.Clock.Set("2026-09-16T08:02:00.000Z");
        await h.AddBadgeAsync(new { name = "Drie", rule = Executions([], 1) });

        var first = await h.GetAsync("/api/v2/badges?limit=2");
        var second = await h.GetAsync($"/api/v2/badges?limit=2&cursor={Uri.EscapeDataString(Str(first.Body, "nextCursor"))}");
        var inactive = await h.GetAsync("/api/v2/badges?active=false");

        first.Body.GetProperty("items").EnumerateArray().Select(b => Str(b, "name")).Should().Equal("Een", "Twee");
        second.Body.GetProperty("items").EnumerateArray().Select(b => Str(b, "name")).Should().Equal("Drie");
        second.Body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        inactive.Body.GetProperty("items").EnumerateArray().Select(b => Str(b, "name")).Should().Equal("Twee");
        (await h.GetAsync("/api/v2/badges?limit=0")).Status.Should().Be(HttpStatusCode.BadRequest);
        (await h.GetAsync("/api/v2/badges?limit=abc")).Status.Should().Be(HttpStatusCode.BadRequest);
        (await h.GetAsync("/api/v2/badges?cursor=garbage")).Status.Should().Be(HttpStatusCode.BadRequest);
        (await h.GetAsync("/api/v2/badges?active=maybe")).Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ABadgeIsCappedAt100_andTheLimitIsAConflictWithTheLimit()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var now = new BsonDateTime(DateTime.Parse("2026-09-16T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime());
        await h.Badges.InsertManyAsync(
            Enumerable.Range(0, 100).Select(i => new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "name", $"Badge {i}" }, { "description", "" },
                { "rule", new BsonDocument { { "type", "onTimeWeeks" }, { "threshold", 1 } } }, { "active", false },
                { "exampleKey", BsonNull.Value }, { "image", BsonNull.Value }, { "createdAt", now }, { "updatedAt", now },
            }),
            cancellationToken: Ct);

        var refused = await h.SendAsync(HttpMethod.Post, "/api/v2/badges", new { name = "Te veel", rule = Executions([], 1) }, h.P1);
        var examples = await h.SendAsync(HttpMethod.Post, "/api/v2/badges/examples", new { language = "nl" }, h.P1);

        refused.Status.Should().Be(HttpStatusCode.Conflict);
        Type(refused.Body).Should().EndWith(":badge_limit");
        refused.Body.GetProperty("limit").GetInt32().Should().Be(100);
        examples.Status.Should().Be(HttpStatusCode.Conflict);
        (await h.Badges.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(100);
    }

    [Fact]
    public async Task SavingTheTasksOfARuleAgainInAnotherOrder_writesNothing_alsoForABadgeStoredUnordered()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = await h.AddBadgeAsync(new { name = "Orde", rule = Executions([h.Toilet, h.Mop], 3) });
        var unordered = new[] { h.Toilet, h.Mop }.Select(ObjectId.Parse).OrderByDescending(id => id.ToString(), StringComparer.Ordinal).ToArray();
        await h.Badges.UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(Str(badge, "id"))), new BsonDocument("$set", new BsonDocument("rule.taskIds", new BsonArray(unordered))), cancellationToken: Ct);

        var same = await h.CapturedAsync(HttpMethod.Patch, $"/api/v2/badges/{Str(badge, "id")}", new { rule = Executions([h.Mop, h.Toilet], 3) }, h.P1);

        same.Status.Should().Be(HttpStatusCode.OK, same.Body.ToString());
        same.Writes.Should().BeEmpty();
        same.AuditInserts.Should().Be(0);
    }

    [Fact]
    public async Task AnAuditEntryOfAChangeCarriesTheRuleNested_andADeactivationIsAnUpdate()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = await h.AddBadgeAsync(new { name = "Toilet", rule = Executions([h.Toilet], 5) });

        await h.SendAsync(HttpMethod.Patch, $"/api/v2/badges/{Str(badge, "id")}", new { active = false }, h.P1);

        var update = (await UpdatesAsync(h)).Should().ContainSingle().Subject;
        update["before"].AsBsonDocument.Names.Should().Equal("active");
        update["after"]["active"].AsBoolean.Should().BeFalse();
    }

    // ---- images

    [Fact]
    public async Task TheImage_isServedWithItsType_aCacheHeaderThatLasts_andAnETag_andAMatchingETagAnswers304()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = await h.AddBadgeAsync(new { name = "Met plaatje", rule = Executions([], 1), image = SampleImages.Input(SampleImages.Png, "image/png") });
        var image = badge.GetProperty("image");
        var url = Str(image, "url");
        var hash = Str(image, "hash");

        var response = await h.Client.GetAsync(url, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        response.Headers.CacheControl!.ToString().Should().Be("public, max-age=31536000, immutable");
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        response.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("default-src 'none'");
        response.Headers.ETag!.Tag.Should().Be($"\"{hash}\"");
        (await response.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(SampleImages.Png);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", $"\"{hash}\"");
        using var cached = await h.Client.SendAsync(conditional, Ct);
        cached.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await cached.Content.ReadAsByteArrayAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task ABadgeWithoutAnImageAndAnUnknownBadge_answer404_andAMalformedIdToo()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = await h.AddBadgeAsync(new { name = "Zonder", rule = Executions([], 1) });

        badge.GetProperty("image").ValueKind.Should().Be(JsonValueKind.Null);
        (await h.GetAsync($"/api/v2/badges/{Str(badge, "id")}/image")).Status.Should().Be(HttpStatusCode.NotFound);
        (await h.GetAsync($"/api/v2/badges/{ObjectId.GenerateNewId()}/image")).Status.Should().Be(HttpStatusCode.NotFound);
        (await h.GetAsync("/api/v2/badges/nope/image")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PngJpegAndWebp_areAccepted_andAnImageOfExactly256KB()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);

        foreach (var (bytes, type) in new[]
        {
            (SampleImages.Png, "image/png"),
            (SampleImages.Jpeg, "image/jpeg"),
            (SampleImages.Webp, "image/webp"),
            (SampleImages.Padded(SampleImages.Png, 256 * 1024), "image/png"),
        })
        {
            var response = await h.SendAsync(HttpMethod.Post, "/api/v2/badges", new { name = $"Plaatje {type}", rule = Executions([], 1), image = SampleImages.Input(bytes, type) }, h.P1);

            response.Status.Should().Be(HttpStatusCode.Created, $"{type} {bytes.Length}: {response.Body}");
            (Str(response.Body.GetProperty("image"), "contentType"), response.Body.GetProperty("image").GetProperty("size").GetInt32()).Should().Be((type, bytes.Length));
        }
    }

    public static TheoryData<string, string, string> BadImages => new()
    {
        { "over256KB", "image.data", "image_too_large" },
        { "svg", "image.data", "unsupported_image_type" },
        { "text", "image.data", "unsupported_image_type" },
        { "mismatch", "image.contentType", "image_type_mismatch" },
        { "notBase64", "image.data", "invalid_base64" },
    };

    [Theory]
    [MemberData(nameof(BadImages))]
    public async Task ABadImage_isAValidationError_withItsMessage_andNothingIsStored(string kind, string field, string message)
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        object image = kind switch
        {
            "over256KB" => SampleImages.Input(SampleImages.Padded(SampleImages.Png, 256 * 1024 + 1), "image/png"),
            "svg" => SampleImages.Input(SampleImages.Svg, "image/png"),
            "text" => SampleImages.Input("hello world, not an image"u8.ToArray(), "image/png"),
            "mismatch" => SampleImages.Input(SampleImages.Png, "image/jpeg"),
            _ => new { contentType = "image/png", data = "not base64!" },
        };

        var response = await h.SendAsync(HttpMethod.Post, "/api/v2/badges", new { name = "Slecht plaatje", rule = Executions([], 1), image }, h.P1);

        response.Status.Should().Be(HttpStatusCode.BadRequest, response.Body.ToString());
        Messages(response.Body, field).Should().Equal(message);
        (await h.Badges.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(0);
    }

    [Fact]
    public async Task AContentTypeOutsidePngJpegAndWebp_isRefused()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);

        var response = await h.SendAsync(
            HttpMethod.Post,
            "/api/v2/badges",
            new { name = "SVG", rule = Executions([], 1), image = new { contentType = "image/svg+xml", data = Convert.ToBase64String(SampleImages.Svg) } },
            h.P1);

        response.Status.Should().Be(HttpStatusCode.BadRequest);
        response.Body.GetProperty("errors").TryGetProperty("image.contentType", out _).Should().BeTrue();
    }

    [Fact]
    public async Task AnImageChange_isAuditedByItsHash_neverByItsBytes_andRemovingItIsAChange()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = await h.AddBadgeAsync(new { name = "Plaatje", rule = Executions([], 1), image = SampleImages.Input(SampleImages.Png, "image/png") });
        var id = Str(badge, "id");
        var before = badge.GetProperty("image");

        var replaced = await h.SendAsync(HttpMethod.Patch, $"/api/v2/badges/{id}", new { image = SampleImages.Input(SampleImages.Jpeg, "image/jpeg") }, h.P1);

        replaced.Status.Should().Be(HttpStatusCode.OK, replaced.Body.ToString());
        var after = replaced.Body.GetProperty("image");
        Str(after, "hash").Should().NotBe(Str(before, "hash"));
        Str(after, "url").Should().NotBe(Str(before, "url"));
        var update = (await UpdatesAsync(h)).Should().ContainSingle().Subject;
        update["before"]["image"].ToJson().Should().Contain(Str(before, "hash")).And.NotContain("data");
        update["after"]["image"]["contentType"].AsString.Should().Be("image/jpeg");
        update.ToJson().Should().NotContain(Convert.ToBase64String(SampleImages.Jpeg));

        // The same picture again is nothing; removing it is a change.
        var again = await h.CapturedAsync(HttpMethod.Patch, $"/api/v2/badges/{id}", new { image = SampleImages.Input(SampleImages.Jpeg, "image/jpeg") }, h.P1);
        again.Writes.Should().BeEmpty();
        again.AuditInserts.Should().Be(0);
        var removed = await h.SendAsync(HttpMethod.Patch, $"/api/v2/badges/{id}", """{ "image": null }""", h.P1);
        removed.Body.GetProperty("image").ValueKind.Should().Be(JsonValueKind.Null);
        (await h.GetAsync($"/api/v2/badges/{id}/image")).Status.Should().Be(HttpStatusCode.NotFound);
        (await UpdatesAsync(h)).Should().HaveCount(2);
    }

    [Fact]
    public async Task OnlyAnAddressWithTheHashOfTheBytes_isCachedForGood_anyOtherRevalidates()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = await h.AddBadgeAsync(new { name = "Plaatje", rule = Executions([], 1), image = SampleImages.Input(SampleImages.Png, "image/png") });
        var b = badge.GetProperty("image");
        var baseUrl = $"/api/v2/badges/{Str(badge, "id")}/image";
        var prefix = Str(b, "hash")[..12];

        async Task<string?> Cache(string url) => (await h.Client.GetAsync(url, Ct)).Headers.CacheControl?.ToString();

        (await Cache($"{baseUrl}?v={prefix}")).Should().Be("public, max-age=31536000, immutable");
        (await Cache(baseUrl)).Should().Be("no-cache");
        (await Cache($"{baseUrl}?v=000000000000")).Should().Be("no-cache");
        (await Cache($"{baseUrl}?v=abc")).Should().Be("no-cache");
        var plain = await h.Client.GetAsync(baseUrl, Ct);
        plain.StatusCode.Should().Be(HttpStatusCode.OK);
        plain.Headers.ETag!.Tag.Should().Be($"\"{Str(b, "hash")}\"");
    }

    [Fact]
    public async Task IfNoneMatch_isReadAsAListOfTags_weakOnesAndStarIncluded()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = await h.AddBadgeAsync(new { name = "Plaatje", rule = Executions([], 1), image = SampleImages.Input(SampleImages.Png, "image/png") });
        var image = badge.GetProperty("image");
        var etag = $"\"{Str(image, "hash")}\"";

        async Task<HttpStatusCode> StatusFor(string header)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Str(image, "url"));
            request.Headers.TryAddWithoutValidation("If-None-Match", header);
            using var response = await h.Client.SendAsync(request, Ct);
            return response.StatusCode;
        }

        (await StatusFor(etag)).Should().Be(HttpStatusCode.NotModified);
        (await StatusFor($"W/{etag}")).Should().Be(HttpStatusCode.NotModified);
        (await StatusFor($"\"other\", {etag}")).Should().Be(HttpStatusCode.NotModified);
        (await StatusFor($"\"other\",W/{etag} , \"third\"")).Should().Be(HttpStatusCode.NotModified);
        (await StatusFor("*")).Should().Be(HttpStatusCode.NotModified);
        (await StatusFor("\"other\"")).Should().Be(HttpStatusCode.OK);
        (await StatusFor($"\"{Str(image, "hash")}x\"")).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheImage_isStoredAsBinaryInTheBadgeDocument_asTheNodeServerStoresIt()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badge = await h.AddBadgeAsync(new { name = "Plaatje", rule = Executions([], 1), image = SampleImages.Input(SampleImages.Png, "image/png") });

        var stored = await h.Badges.Find(new BsonDocument("_id", ObjectId.Parse(Str(badge, "id")))).SingleAsync(Ct);

        var image = stored["image"].AsBsonDocument;
        image["data"].IsBsonBinaryData.Should().BeTrue();
        image["data"].AsBsonBinaryData.Bytes.Should().Equal(SampleImages.Png);
        (image["contentType"].AsString, image["size"].AsInt32, image["hash"].AsString).Should().Be(("image/png", SampleImages.Png.Length, Str(badge.GetProperty("image"), "hash")));
        stored["exampleKey"].IsBsonNull.Should().BeTrue();
        stored["rule"]["taskIds"].AsBsonArray.Should().BeEmpty();
    }
}
