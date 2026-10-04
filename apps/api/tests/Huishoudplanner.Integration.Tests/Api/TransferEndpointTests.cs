using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using static Huishoudplanner.Integration.Tests.Fixtures.TransferWorld;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Ports <c>apps/server/test/transfer.test.ts</c> against the real host and a real MongoDB replica set: the JSON export, the import with its
/// confirmation and acknowledgements, the validation of the whole file before anything is written, and the audit entry. The rebuild of the badge
/// awards after an import belongs to the badges slice, which owns the awards (the import clears them; see the notes at the bottom).
/// </summary>
public sealed class TransferEndpointTests(TransferWorld world) : IClassFixture<TransferWorld>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Code(string code) => $"urn:huishoudplanner:problem:{code}";

    private static readonly FilterDefinition<BsonDocument> All = FilterDefinition<BsonDocument>.Empty;

    private static IMongoCollection<BsonDocument> Of(IMongoDatabase database, string name) => database.GetCollection<BsonDocument>(name);

    private static async Task<long> CountAsync(IMongoDatabase database, string name) => await database.GetCollection<BsonDocument>(name).CountDocumentsAsync(All, cancellationToken: Ct);

    /// <summary>The documents as text without the concurrency <c>version</c>: an import gives its own versions (ADR-0022), everything else must come back identical.</summary>
    private static string Canonical(IEnumerable<BsonDocument> docs) => string.Join("\n", docs.Select(d => new BsonDocument(d.Elements.Where(e => e.Name != "version")).ToJson(new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.RelaxedExtendedJson })));

    /// <summary>The import entries and the ledger rebuild entry are the only new audit entries (ADR-0011).</summary>
    private static IEnumerable<BsonDocument> WithoutImports(IEnumerable<BsonDocument> entries) =>
        entries.Where(e => e["entity"].AsString != "import" && !(e["entity"].AsString is "points" or "badgeAward" && e["action"].AsString == "recompute"));

    private static async Task<List<BsonDocument>> AuditAsync(IMongoDatabase database) =>
        await database.GetCollection<BsonDocument>("auditLog").Find(All).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync(Ct);

    /// <summary>The same document with its top-level fields in name order (a field the rebuild adds comes last, wherever the source had it).</summary>
    private static BsonDocument Sorted(BsonDocument doc) => new(doc.Elements.OrderBy(e => e.Name, StringComparer.Ordinal));

    private static string Details(JsonElement body) => body.ToString();

    // ---- export

    [Fact]
    public void Export_hasEveryCollectionAsExtendedJson_withASchemaVersionAndADatedFilename()
    {
        var file = world.Export;

        ((int)file["schemaVersion"]!).Should().Be(6);
        ((string)file["exportedAt"]!).Should().Be("2026-09-16T08:00:00.000Z");
        file["collections"]!.AsObject().Select(p => p.Key).Should().Equal(Collections);
        Doc(file, "users", 0)["_id"]!["$oid"]!.GetValue<string>().Should().MatchRegex("^[0-9a-f]{24}$");
        Doc(file, "occurrences", 0)["date"]!["$date"]!.GetValue<string>().Should().NotBeEmpty();
        foreach (var name in Collections)
        {
            Docs(file, name).Count.Should().Be(world.Snapshot[name].Count, name);
        }

        world.Snapshot["occurrences"].Should().Contain(o => o["status"].AsString == "done");
        ((int)Doc(file, "settings", 0)["rewardGoals"]!["weekPoints"]!).Should().Be(12);
        Doc(file, "settings", 0)["rewardGoals"]!["cyclePoints"].Should().BeNull();
    }

    [Fact]
    public async Task Export_answersAJsonAttachmentNamedAfterTodayInTheHouseholdTimezone()
    {
        var response = await world.Source.Client.GetAsync("/api/v2/export/json", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Content.Headers.ContentDisposition!.ToString().Should().Be("attachment; filename=\"huishoudplanner-20260916.json\"");
    }

    [Fact]
    public void Export_includesTheBadgeDefinitionsWithTheirImagesAsBinary_andNoAwards()
    {
        var file = world.Export;

        Docs(file, "badges").Count.Should().Be(2);
        var first = Doc(file, "badges", 0);
        ((string)first["name"]!).Should().Be("Aanrechtheld");
        ((string)first["image"]!["contentType"]!).Should().Be("image/png");
        ((int)first["image"]!["size"]!).Should().Be(PngBytes.Length);
        ((string)first["image"]!["data"]!["$binary"]!["base64"]!).Should().Be(Convert.ToBase64String(PngBytes));
        file["collections"]!.AsObject().ContainsKey("badgeAwards").Should().BeFalse();
        world.Snapshot["badges"].Should().OnlyContain(b => b["image"]["data"].IsBsonBinaryData);
    }

    [Fact]
    public void Export_includesRecordedWork_requestKeys_andOneOffTasksWithoutATaskId()
    {
        var occurrences = Docs(world.Export, "occurrences").Select(o => o!.AsObject()).ToList();

        var extra = occurrences.Single(o => (string?)o["requestId"] == ExtraKey);
        ((string)extra["origin"]!).Should().Be("adhoc");
        ((bool)extra["recordedDone"]!).Should().BeTrue();
        extra["taskId"]!["$oid"].Should().NotBeNull();
        var oneOff = occurrences.Single(o => (string?)o["requestId"] == OneOffKey);
        oneOff["taskId"].Should().BeNull();
        ((string)oneOff["taskNameSnapshot"]!).Should().Be("Gordijnen ophangen");
        occurrences.Should().Contain(o => (string)o["origin"]! == "generated");
    }

    [Fact]
    public void Export_includesTheRedemptionsAsTheOnlyLedgerEntries()
    {
        var entries = Docs(world.Export, "pointEntries").Select(o => o!.AsObject()).ToList();

        entries.Should().ContainSingle().Which["kind"]!.GetValue<string>().Should().Be("redemption");
    }

    [Fact]
    public async Task Export_needsNoProfile_andIsOpenToEveryRole()
    {
        using var target = new TransferTarget(world.Mongo);
        using var asMember = new HttpRequestMessage(HttpMethod.Get, "/api/v2/export/json");
        asMember.Headers.Add("X-Profile-Id", target.Member.Id);

        (await target.Client.GetAsync("/api/v2/export/json", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await target.Client.SendAsync(asMember, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- import

    [Fact]
    public async Task Import_givesTheImportedDocumentsAVersionAboveTheHighestOfTheCollectionItReplaces()
    {
        using var target = new TransferTarget(world.Mongo);
        var (first, _) = await target.ImportAsync(world.Export);
        first.Should().Be(HttpStatusCode.OK);
        var afterFirst = await SnapshotOfAsync(target.Database);

        var (second, _) = await target.ImportAsync(world.Export);

        second.Should().Be(HttpStatusCode.OK);
        var afterSecond = await SnapshotOfAsync(target.Database);
        foreach (var name in new[] { "users", "rooms", "tasks", "cyclePlans", "badges", "settings" })
        {
            var before = afterFirst[name].Select(d => d.GetValue("version", 0).ToInt32()).ToList();
            var now = afterSecond[name].Select(d => d.GetValue("version", 0).ToInt32()).ToList();
            now.Should().OnlyContain(v => v == before.DefaultIfEmpty(0).Max() + 1, $"{name}: an ETag read before the second import never matches an imported document");
        }
    }

    [Fact]
    public async Task Import_roundTripsIntoAnEmptyDatabase_withIdenticalData()
    {
        using var target = new TransferTarget(world.Mongo);

        var (status, body) = await target.ImportAsync(world.Export);

        status.Should().Be(HttpStatusCode.OK, Details(body));
        var after = await SnapshotOfAsync(target.Database);
        foreach (var name in Collections.Where(n => n != "auditLog"))
        {
            Canonical(after[name]).Should().Be(Canonical(world.Snapshot[name]), name);
        }

        Canonical(WithoutImports(after["auditLog"])).Should().Be(Canonical(world.Snapshot["auditLog"]));
        after["auditLog"].Count(e => e["entity"].AsString == "import").Should().Be(1);
    }

    [Fact]
    public async Task Import_ofAFileThatWasExportedAgain_imports()
    {
        using var first = new TransferTarget(world.Mongo);
        (await first.ImportAsync(world.Export)).Status.Should().Be(HttpStatusCode.OK);
        var again = await first.ExportAsync();
        using var second = new TransferTarget(world.Mongo);

        var (status, body) = await second.ImportAsync(again);

        status.Should().Be(HttpStatusCode.OK, Details(body));
        Canonical((await SnapshotOfAsync(second.Database))["occurrences"]).Should().Be(Canonical(world.Snapshot["occurrences"]));
    }

    [Fact]
    public async Task Import_dropsTheLedger_keepsTheRedemptions_andRebuildsTheDerivedEntries()
    {
        using var target = new TransferTarget(world.Mongo);
        await Of(target.Database, "pointEntries").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "key", "execution:stray" }, { "kind", "execution" } }, cancellationToken: Ct);

        (await target.ImportAsync(world.Export)).Status.Should().Be(HttpStatusCode.OK);

        var ledger = await Of(target.Database, "pointEntries").Find(All).ToListAsync(Ct);
        ledger.Should().NotContain(e => e["key"].AsString == "execution:stray");
        ledger.Count(e => e["kind"].AsString == "redemption").Should().Be(1);
        ledger.Should().Contain(e => e["kind"].AsString == "execution", "the executions are rebuilt from the imported occurrences");
        (await Of(target.Database, "auditLog").CountDocumentsAsync(new BsonDocument { { "entity", "points" }, { "action", "recompute" }, { "meta.trigger", "import" } }, cancellationToken: Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Import_ofAVersion5File_removesTheBadgesAndAwardsItReplaces()
    {
        using var target = new TransferTarget(world.Mongo);
        (await target.ImportAsync(world.Export)).Status.Should().Be(HttpStatusCode.OK);

        var (status, body) = await target.ImportAsync(world.AsVersion5(), "&acknowledgeBadges=true");

        status.Should().Be(HttpStatusCode.OK, Details(body));
        body.GetProperty("replaced").GetProperty("badges").GetInt32().Should().Be(0);
        body.GetProperty("removedBadges").GetInt32().Should().Be(2);
        body.GetProperty("removedBadgeAwards").GetInt32().Should().Be(1, "the first import rebuilt the award of the done occurrence");
        (await CountAsync(target.Database, "badges")).Should().Be(0);
        (await CountAsync(target.Database, "badgeAwards")).Should().Be(0);
    }

    [Fact]
    public async Task Import_dropsTasksTheFileDoesNotHaveFromABadgeRule_ordersTheRest_andDeactivatesARuleLeftWithoutTasks()
    {
        var dangling = world.Clone();
        var taskId = Doc(dangling, "badges", 0)["rule"]!["taskIds"]![0]!["$oid"]!.GetValue<string>();
        var gone = new JsonObject { ["$oid"] = ObjectId.GenerateNewId().ToString() };
        Doc(dangling, "badges", 0)["rule"]!["taskIds"] = new JsonArray(gone.DeepClone(), new JsonObject { ["$oid"] = taskId }, new JsonObject { ["$oid"] = "00000000000000000000000a" });
        Doc(dangling, "badges", 1)["active"] = true;
        Doc(dangling, "badges", 1)["rule"] = new JsonObject { ["type"] = "executions", ["taskIds"] = new JsonArray(gone.DeepClone()), ["threshold"] = 1 };
        using var target = new TransferTarget(world.Mongo);

        var (status, body) = await target.ImportAsync(dangling);

        status.Should().Be(HttpStatusCode.OK, Details(body));
        var badges = await Of(target.Database, "badges").Find(All).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync(Ct);
        var byName = badges.ToDictionary(b => b["name"].AsString);
        var first = byName["Aanrechtheld"];
        first["rule"]["taskIds"].AsBsonArray.Select(v => v.AsObjectId.ToString()).Should().Equal(taskId);
        first["active"].AsBoolean.Should().BeTrue();
        var second = byName["Alles-doener"];
        second["rule"]["taskIds"].AsBsonArray.Should().BeEmpty();
        second["active"].AsBoolean.Should().BeFalse();
        // Exporting again gives a file that imports.
        using var next = new TransferTarget(world.Mongo);
        (await next.ImportAsync(await target.ExportAsync())).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Import_ofAFileOlderThanVersion6_whileBadgesExist_needsTheAcknowledgement_andRecordsHowManyWereRemoved()
    {
        using var target = new TransferTarget(world.Mongo);
        (await target.ImportAsync(world.Export)).Status.Should().Be(HttpStatusCode.OK);
        var old = world.AsVersion5();
        target.Capture.Clear();

        var (refused, refusal) = await target.ImportAsync(old);

        refused.Should().Be(HttpStatusCode.Conflict);
        refusal.GetProperty("type").GetString().Should().Be(Code("badges_would_be_removed"));
        refusal.GetProperty("count").GetInt32().Should().Be(2);
        target.Capture.All().Should().BeEmpty();
        (await CountAsync(target.Database, "badges")).Should().Be(2);

        var (accepted, result) = await target.ImportAsync(old, "&acknowledgeBadges=true");
        accepted.Should().Be(HttpStatusCode.OK, Details(result));
        result.GetProperty("removedBadges").GetInt32().Should().Be(2);
        var import = (await AuditAsync(target.Database)).Last(e => e["entity"].AsString == "import");
        import["after"]["removedBadges"].ToInt32().Should().Be(2);
        (await CountAsync(target.Database, "badges")).Should().Be(0);
    }

    [Fact]
    public async Task Import_ofAFileOlderThanVersion5_whileRedemptionsExist_needsTheAcknowledgement_andRecordsHowManyWereRemoved()
    {
        using var target = new TransferTarget(world.Mongo);
        (await target.ImportAsync(world.Export)).Status.Should().Be(HttpStatusCode.OK);
        var old = world.AsVersion4();
        target.Capture.Clear();

        var (refused, refusal) = await target.ImportAsync(old);

        refused.Should().Be(HttpStatusCode.Conflict);
        refusal.GetProperty("type").GetString().Should().Be(Code("redemptions_would_be_removed"));
        refusal.GetProperty("count").GetInt32().Should().Be(1);
        target.Capture.All().Should().BeEmpty();

        // The redemptions are acknowledged, so the badges that would be removed need their own acknowledgement.
        var (badges, badgeRefusal) = await target.ImportAsync(old, "&acknowledgeRedemptions=true");
        badges.Should().Be(HttpStatusCode.Conflict);
        badgeRefusal.GetProperty("type").GetString().Should().Be(Code("badges_would_be_removed"));

        var (accepted, result) = await target.ImportAsync(old, "&acknowledgeRedemptions=true&acknowledgeBadges=true");
        accepted.Should().Be(HttpStatusCode.OK, Details(result));
        result.GetProperty("removedRedemptions").GetInt32().Should().Be(1);
        var import = (await AuditAsync(target.Database)).Last(e => e["entity"].AsString == "import");
        import["after"]["removedRedemptions"].ToInt32().Should().Be(1);
        (await Of(target.Database, "pointEntries").CountDocumentsAsync(new BsonDocument("kind", "redemption"), cancellationToken: Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Import_needsNoAcknowledgement_forAVersion6File_orWhenThereIsNothingToLose()
    {
        using var target = new TransferTarget(world.Mongo);

        (await target.ImportAsync(world.AsVersion5())).Status.Should().Be(HttpStatusCode.OK); // no badges to lose
        (await target.ImportAsync(world.Export)).Status.Should().Be(HttpStatusCode.OK);
        (await target.ImportAsync(world.Export)).Status.Should().Be(HttpStatusCode.OK); // a version 6 file replaces its own badges and redemptions
    }

    [Fact]
    public async Task Import_ofAVersion1File_whichHasNoRecordedDoneOrRequestId_importsUnchanged_andTheRebuildFillsInThePoints()
    {
        var legacy = world.AsVersion1();
        using var target = new TransferTarget(world.Mongo);

        var (status, body) = await target.ImportAsync(legacy);

        status.Should().Be(HttpStatusCode.OK, Details(body));
        var after = await SnapshotOfAsync(target.Database);
        Canonical(after["tasks"].Select(Sorted)).Should().Be(Canonical(world.Snapshot["tasks"].Select(Sorted)), "the rebuild gives the tasks the points they had");
        var expected = world.Snapshot["occurrences"].Select(o =>
        {
            var copy = o.DeepClone().AsBsonDocument;
            copy.Remove("recordedDone");
            copy.Remove("requestId");
            if (copy["status"].AsString != "done")
            {
                copy.Remove("pointsSnapshot");
            }

            return copy;
        });
        Canonical(after["occurrences"].Select(StripUpdatedAt)).Should().Be(Canonical(expected.Select(StripUpdatedAt)));
        after["occurrences"].Should().NotContain(o => o.Contains("recordedDone") || o.Contains("requestId"));
        var imports = after["auditLog"].Where(e => e["entity"].AsString == "import").ToList();
        imports.Should().ContainSingle().Which["meta"]["schemaVersion"].ToInt32().Should().Be(1);

        static BsonDocument StripUpdatedAt(BsonDocument doc)
        {
            var copy = doc.DeepClone().AsBsonDocument;
            copy.Remove("updatedAt");
            copy.Remove("pointsSnapshot");
            return copy;
        }
    }

    [Fact]
    public async Task Import_replacesAllData_keepsTheExistingAuditLog_andAuditsTheImportOnce()
    {
        using var target = new TransferTarget(world.Mongo, seeded: true);
        (await target.Client.GetAsync("/api/v2/health", Ct)).EnsureSuccessStatusCode();
        var auditBefore = await CountAsync(target.Database, "auditLog");
        auditBefore.Should().BeGreaterThan(0, "the seeding of the target audited its people");

        var (status, body) = await target.ImportAsync(world.Export);

        status.Should().Be(HttpStatusCode.OK, Details(body));
        var replaced = body.GetProperty("replaced");
        replaced.GetProperty("settings").GetInt32().Should().Be(1);
        replaced.GetProperty("users").GetInt32().Should().Be(world.Snapshot["users"].Count);
        replaced.GetProperty("rooms").GetInt32().Should().Be(world.Snapshot["rooms"].Count);
        replaced.GetProperty("tasks").GetInt32().Should().Be(world.Snapshot["tasks"].Count);
        replaced.GetProperty("cyclePlans").GetInt32().Should().Be(world.Snapshot["cyclePlans"].Count);
        replaced.GetProperty("cycles").GetInt32().Should().Be(world.Snapshot["cycles"].Count);
        replaced.GetProperty("occurrences").GetInt32().Should().Be(world.Snapshot["occurrences"].Count);
        replaced.GetProperty("pointEntries").GetInt32().Should().Be(1);
        replaced.GetProperty("badges").GetInt32().Should().Be(2);
        body.GetProperty("auditAdded").GetInt32().Should().Be(world.Snapshot["auditLog"].Count);
        body.GetProperty("removedPointEntries").GetInt32().Should().Be(0);
        body.GetProperty("removedRedemptions").GetInt32().Should().Be(0);
        body.GetProperty("removedBadges").GetInt32().Should().Be(0);
        body.GetProperty("removedBadgeAwards").GetInt32().Should().Be(0);

        var after = await SnapshotOfAsync(target.Database);
        Canonical(after["users"]).Should().Be(Canonical(world.Snapshot["users"]));
        Canonical(after["occurrences"]).Should().Be(Canonical(world.Snapshot["occurrences"]));
        // The import entry and one summary of the ledger rebuild and one of the award rebuild (the derived ledger is not part of the file).
        after["auditLog"].Count.Should().Be((int)auditBefore + world.Snapshot["auditLog"].Count + 3);
        after["auditLog"].Count(e => e["entity"].AsString == "points" && e["action"].AsString == "recompute").Should().Be(1);
        var import = after["auditLog"].Single(e => e["entity"].AsString == "import");
        import["action"].AsString.Should().Be("create");
        import["source"].AsString.Should().Be("ui");
        import["actorId"].AsObjectId.ToString().Should().Be(target.Admin.Id);
        import["meta"]["mode"].AsString.Should().Be("replace");
        import["meta"]["schemaVersion"].ToInt32().Should().Be(6);
        import["meta"]["exportedAt"].AsString.Should().Be((string)world.Export["exportedAt"]!);
    }

    [Fact]
    public async Task Import_withoutTheConfirmation_isRefused_andWritesNothing()
    {
        using var target = new TransferTarget(world.Mongo);
        target.Capture.Clear();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/import/json?mode=replace") { Content = Json(world.Export) };
        request.Headers.Add("X-Profile-Id", target.Admin.Id);
        var (status, body) = await target.SendAsync(request);

        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("type").GetString().Should().Be(Code("confirmation_required"));
        target.Capture.All().Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("&mode=merge")]
    public async Task Import_needsTheReplaceMode(string wrong)
    {
        using var target = new TransferTarget(world.Mongo);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/import/json?confirm=true" + wrong) { Content = Json(world.Export) };
        request.Headers.Add("X-Profile-Id", target.Admin.Id);
        target.Capture.Clear();

        var (status, body) = await target.SendAsync(request);

        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("type").GetString().Should().Be(Code("validation_error"));
        body.GetProperty("errors").GetProperty("mode").EnumerateArray().Should().NotBeEmpty();
        target.Capture.All().Should().BeEmpty();
    }

    [Fact]
    public async Task Import_requiresAnAdministrator_andAProfile()
    {
        using var target = new TransferTarget(world.Mongo);
        target.Capture.Clear();

        using var anonymous = new HttpRequestMessage(HttpMethod.Post, TransferTarget.ImportUrl) { Content = Json(world.Export) };
        var (noProfile, noProfileBody) = await target.SendAsync(anonymous);
        noProfile.Should().Be(HttpStatusCode.BadRequest);
        noProfileBody.GetProperty("type").GetString().Should().Be(Code("profile_required"));

        foreach (var actor in new[] { target.Planner, target.Member })
        {
            var (forbidden, body) = await target.ImportAsync(world.Export, actor: actor);
            forbidden.Should().Be(HttpStatusCode.Forbidden, Details(body));
        }

        target.Capture.All().Should().BeEmpty();
    }

    [Fact]
    public void TheImportRoute_takesUpTo200Megabytes_andTheExportAndImportAreTheOnlyNewWriteRoutes()
    {
        using var factory = ApiFactory.WithoutDatabase();
        _ = factory.CreateClient();
        var import = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/api/v2/import/json");

        import.Metadata.GetMetadata<IRequestSizeLimitMetadata>()!.MaxRequestBodySize.Should().Be(200L * 1024 * 1024);
    }

    // ---- invalid files are rejected before anything is touched

    public static TheoryData<string, string, string, string> InvalidFiles => new()
    {
        { "an unknown schema version", "v7", "^schemaVersion$", "unsupported_version" },
        { "an invalid field value", "color", @"^collections\.users\.0\.color$", "invalid_color" },
        { "a plain string where an ObjectId belongs", "roomIdString", @"^collections\.tasks\.0\.roomId$", "expected_object_id" },
        { "a plain string where a date belongs", "dateString", @"^collections\.occurrences\.0\.date$", "expected_date" },
        { "a duplicate request id", "duplicateRequestId", @"^collections\.occurrences\.\d+\.requestId$", "duplicate_request_id" },
        { "a duplicate generated slot", "duplicateSlot", @"^collections\.occurrences\.\d+\.plannedDate$", "duplicate_slot" },
        { "a version 6 file without its badges", "noBadges", @"^collections\.badges$", "required" },
        { "a badge rule with a task id that is not an id", "ruleTaskIdString", @"^collections\.badges\.0\.rule\.taskIds\.0$", "expected_object_id" },
        { "a badge image whose bytes do not match its hash", "imageHash", @"^collections\.badges\.0\.image\.hash$", "image_hash_mismatch" },
        { "a badge image whose bytes do not match its size", "imageSize", @"^collections\.badges\.0\.image\.size$", "image_size_mismatch" },
        { "a badge image that is an SVG", "imageSvg", @"^collections\.badges\.0\.image\.data$", "unsupported_image_type" },
        { "a badge image of another type than declared", "imageType", @"^collections\.badges\.0\.image\.contentType$", "image_type_mismatch" },
        { "a duplicate example key", "exampleKey", @"^collections\.badges\.1\.exampleKey$", "duplicate_example_key" },
        { "a missing settings document", "noSettings", @"^collections\.settings$", "settings_singleton" },
        { "no active user", "noUsers", @"^collections\.users$", "no_active_user" },
        { "a redemption of a person who is not in the file", "redemptionPerson", @"^collections\.pointEntries\.0\.personId$", "unknown_user" },
        { "a duplicate redemption key", "redemptionKey", @"^collections\.pointEntries\.1\.key$", "duplicate_key" },
        { "a bonus schedule row that starts after today", "bonusFuture", @"^collections\.settings\.0\.bonusSchedule\.0\.from$", "bonus_schedule_in_future" },
        { "a bonus floor after today", "floorFuture", @"^collections\.settings\.0\.bonusFloor$", "bonus_floor_in_future" },
        { "a duplicate cycle index", "duplicateCycle", @"^collections\.cycles\.1\.index$", "duplicate_index" },
        { "a duplicate id", "duplicateId", @"^collections\.rooms\.1\._id$", "duplicate_id" },
        { "a missing required field", "noName", @"^collections\.rooms\.0\.name$", "required" },
        { "a collection that is not a list", "notAList", @"^collections\.rooms$", "expected_array" },
    };

    private static void Mutate(JsonObject file, string how)
    {
        switch (how)
        {
            case "v7": file["schemaVersion"] = 7; break;
            case "color": Doc(file, "users", 0)["color"] = "rood"; break;
            case "roomIdString": Doc(file, "tasks", 0)["roomId"] = "0123456789abcdef01234567"; break;
            case "dateString": Doc(file, "occurrences", 0)["date"] = "2026-09-16T00:00:00.000Z"; break;
            case "duplicateRequestId":
                Docs(file, "occurrences").Select(o => o!.AsObject()).Single(o => (string?)o["requestId"] == OneOffKey)["requestId"] = ExtraKey;
                break;
            case "duplicateSlot":
                var generated = (JsonObject)Docs(file, "occurrences").Select(o => o!.AsObject()).First(o => (string)o["origin"]! == "generated").DeepClone();
                generated["_id"] = new JsonObject { ["$oid"] = ObjectId.GenerateNewId().ToString() };
                Docs(file, "occurrences").Add(generated);
                break;
            case "noBadges": file["collections"]!.AsObject().Remove("badges"); break;
            case "ruleTaskIdString": Doc(file, "badges", 0)["rule"]!["taskIds"] = new JsonArray("0123456789abcdef01234567"); break;
            case "imageHash": Doc(file, "badges", 0)["image"]!["hash"] = new string('a', 64); break;
            case "imageSize": Doc(file, "badges", 0)["image"]!["size"] = PngBytes.Length + 1; break;
            case "imageSvg":
                var image = Doc(file, "badges", 0)["image"]!.AsObject();
                image["size"] = SvgBytes.Length;
                image["hash"] = Hex(SvgBytes);
                image["data"] = new JsonObject { ["$binary"] = new JsonObject { ["base64"] = Convert.ToBase64String(SvgBytes), ["subType"] = "00" } };
                break;
            case "imageType": Doc(file, "badges", 0)["image"]!["contentType"] = "image/webp"; break;
            case "exampleKey":
                foreach (var badge in Docs(file, "badges"))
                {
                    badge!["exampleKey"] = "example:toilet";
                }

                break;
            case "noSettings": file["collections"]!["settings"] = new JsonArray(); break;
            case "noUsers": file["collections"]!["users"] = new JsonArray(); break;
            case "redemptionPerson": Doc(file, "pointEntries", 0)["personId"] = new JsonObject { ["$oid"] = ObjectId.GenerateNewId().ToString() }; break;
            case "redemptionKey":
                var second = (JsonObject)Doc(file, "pointEntries", 0).DeepClone();
                second["_id"] = new JsonObject { ["$oid"] = ObjectId.GenerateNewId().ToString() };
                second["requestId"] = null;
                Docs(file, "pointEntries").Add(second);
                break;
            case "bonusFuture":
                Doc(file, "settings", 0)["bonusSchedule"] = new JsonArray(new JsonObject { ["from"] = "2026-09-21", ["weekDone"] = 1, ["weekOnTime"] = 0, ["cycleDone"] = 0, ["cycleOnTime"] = 0 });
                break;
            case "floorFuture": Doc(file, "settings", 0)["bonusFloor"] = "2026-09-17"; break;
            case "duplicateCycle":
                var cycle = (JsonObject)Doc(file, "cycles", 0).DeepClone();
                cycle["_id"] = new JsonObject { ["$oid"] = ObjectId.GenerateNewId().ToString() };
                Docs(file, "cycles").Insert(1, cycle);
                break;
            case "duplicateId":
                Docs(file, "rooms").Add(Doc(file, "rooms", 0).DeepClone());
                break;
            case "noName": Doc(file, "rooms", 0).Remove("name"); break;
            case "notAList": file["collections"]!["rooms"] = new JsonObject(); break;
            default: throw new ArgumentOutOfRangeException(nameof(how), how, null);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public async Task Import_rejectsAnInvalidFile_withTheFieldAndTheCode_andWritesNothing(string label, string how, string field, string message)
    {
        using var target = new TransferTarget(world.Mongo);
        (await target.ImportAsync(world.Export)).Status.Should().Be(HttpStatusCode.OK);
        var before = await SnapshotOfAsync(target.Database);
        var broken = world.Clone();
        Mutate(broken, how);
        target.Capture.Clear();

        var (status, body) = await target.ImportAsync(broken);

        status.Should().Be(HttpStatusCode.BadRequest, $"{label}: {Details(body)}");
        body.GetProperty("type").GetString().Should().Be(Code("validation_error"));
        var errors = body.GetProperty("errors").EnumerateObject().ToList();
        errors.Should().Contain(e => Regex.IsMatch(e.Name, field) && e.Value.EnumerateArray().Any(m => m.GetString() == message), $"{label}: {Details(body)}");
        target.Capture.All().Should().BeEmpty();
        var after = await SnapshotOfAsync(target.Database);
        foreach (var name in Collections.Where(n => n != "auditLog"))
        {
            Canonical(after[name]).Should().Be(Canonical(before[name]), name);
        }
    }

    [Fact]
    public async Task Import_ofAFileWithManyProblems_reportsABoundedNumberOfThem()
    {
        using var target = new TransferTarget(world.Mongo);
        var broken = world.Clone();
        for (var i = 0; i < 300; i++)
        {
            var user = (JsonObject)Doc(broken, "users", 0).DeepClone();
            user["_id"] = new JsonObject { ["$oid"] = ObjectId.GenerateNewId().ToString() };
            user["color"] = "rood";
            Docs(broken, "users").Add(user);
        }

        var (status, body) = await target.ImportAsync(broken);

        status.Should().Be(HttpStatusCode.BadRequest);
        var errors = body.GetProperty("errors").EnumerateObject().ToList();
        errors.Count.Should().BeInRange(1, 200);
    }

    [Theory]
    [InlineData("not json at all", "invalid_json")]
    [InlineData("[]", "expected_object")]
    [InlineData("", "invalid_json")]
    [InlineData("{\"schemaVersion\": 6}", "required")]
    public async Task Import_rejectsABodyThatIsNotAnExportFile(string text, string code)
    {
        using var target = new TransferTarget(world.Mongo);
        using var request = new HttpRequestMessage(HttpMethod.Post, TransferTarget.ImportUrl) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Profile-Id", target.Admin.Id);
        target.Capture.Clear();

        var (status, body) = await target.SendAsync(request);

        status.Should().Be(HttpStatusCode.BadRequest, Details(body));
        body.GetProperty("type").GetString().Should().Be(Code("validation_error"));
        body.GetProperty("errors").EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(m => m.GetString())).Should().Contain(code);
        target.Capture.All().Should().BeEmpty();
    }

    [Theory]
    [InlineData(10000, "[")]
    [InlineData(10000, "{\"a\":")]
    [InlineData(65, "[")]
    public async Task Import_refusesAFileNestedTooDeeply_asInvalidJson_withoutCrashing(int depth, string open)
    {
        using var target = new TransferTarget(world.Mongo);
        var text = string.Concat(Enumerable.Repeat(open, depth));
        using var request = new HttpRequestMessage(HttpMethod.Post, TransferTarget.ImportUrl) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Profile-Id", target.Admin.Id);
        target.Capture.Clear();

        var (status, body) = await target.SendAsync(request);

        status.Should().Be(HttpStatusCode.BadRequest, Details(body));
        body.GetProperty("errors").GetProperty("body").EnumerateArray().Select(m => m.GetString()).Should().Contain("invalid_json");
        target.Capture.All().Should().BeEmpty();
    }

    [Fact]
    public void TheExportedSettings_carryNoSecretLikeFieldNames()
    {
        var names = new List<string>();
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var p in o)
                    {
                        names.Add(p.Key);
                        Walk(p.Value);
                    }

                    break;
                case JsonArray a:
                    foreach (var i in a)
                    {
                        Walk(i);
                    }

                    break;
            }
        }

        Walk(Doc(world.Export, "settings", 0));

        names.Should().NotContain(n => Regex.IsMatch(n, "secret|token|password|api[-_]?key|credential", RegexOptions.IgnoreCase), "an export must never carry a credential");
    }

    [Fact]
    public async Task Import_whenTheReplacementFailsHalfway_rollsBackEverything()
    {
        using var target = new TransferTarget(world.Mongo);
        (await target.ImportAsync(world.Export)).Status.Should().Be(HttpStatusCode.OK);
        var before = await SnapshotOfAsync(target.Database);
        var auditBefore = await CountAsync(target.Database, "auditLog");
        // A name of 17 MB passes every check of the file but cannot be stored: the users are written after the settings, which are already replaced.
        var heavy = world.Clone();
        Doc(heavy, "users", 0)["name"] = new string('x', 17 * 1024 * 1024);
        Doc(heavy, "settings", 0)["promoteThreshold"] = 7;
        target.Capture.Clear();

        var (status, _) = await target.ImportAsync(heavy);

        status.Should().Be(HttpStatusCode.InternalServerError);
        var after = await SnapshotOfAsync(target.Database);
        foreach (var name in Collections.Where(n => n != "auditLog"))
        {
            Canonical(after[name]).Should().Be(Canonical(before[name]), name);
        }

        (await CountAsync(target.Database, "auditLog")).Should().Be(auditBefore);
    }

    // ---- the audit log is merged, not replaced

    [Fact]
    public async Task Import_addsOnlyTheAuditEntriesThatAreNotInTheLogYet()
    {
        using var target = new TransferTarget(world.Mongo);
        (await target.ImportAsync(world.Export)).Status.Should().Be(HttpStatusCode.OK);
        var count = await CountAsync(target.Database, "auditLog");

        var (status, body) = await target.ImportAsync(world.Export);

        status.Should().Be(HttpStatusCode.OK, Details(body));
        body.GetProperty("auditAdded").GetInt32().Should().Be(0);
        (await CountAsync(target.Database, "auditLog")).Should().BeGreaterThan(count, "the second import is audited itself");
        (await Of(target.Database, "auditLog").CountDocumentsAsync(new BsonDocument("entity", "import"), cancellationToken: Ct)).Should().Be(2);
    }

    [Fact]
    public async Task Import_rebuildsTheBadgeAwardsFromTheImportedData_withTheMomentTheyWereEarned_andSummarisesIt()
    {
        using var target = new TransferTarget(world.Mongo);

        (await target.ImportAsync(world.Export)).Status.Should().Be(HttpStatusCode.OK);

        var awards = await Of(target.Database, "badgeAwards").Find(All).ToListAsync(Ct);
        awards.Should().ContainSingle();
        awards[0]["personId"].AsObjectId.ToString().Should().Be(world.Source.P1.Id);
        var done = world.Snapshot["occurrences"].First(o => o["status"].AsString == "done" && o["completedBy"].AsObjectId.ToString() == world.Source.P1.Id);
        awards[0]["awardedAt"].ToUniversalTime().Should().BeOnOrAfter(done["completedAt"].ToUniversalTime().AddDays(-1));
        (await Of(target.Database, "auditLog").CountDocumentsAsync(new BsonDocument { { "entity", "badgeAward" }, { "action", "recompute" }, { "meta.trigger", "import" } }, cancellationToken: Ct)).Should().Be(1);

        // A second import of the same file brings the same awards back with the same moments.
        var moment = awards[0]["awardedAt"];
        (await target.ImportAsync(world.Export)).Status.Should().Be(HttpStatusCode.OK);
        (await Of(target.Database, "badgeAwards").Find(All).ToListAsync(Ct)).Should().ContainSingle().Which["awardedAt"].Should().Be(moment);
    }
}
