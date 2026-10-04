#pragma warning disable CA1861 // inline arrays in request bodies of tests
using System.Net;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Ports apps/server/test/users.test.ts and the users cases of roles.test.ts (an administrator creates a planner, a
/// planner may not manage people, a member may not) to <c>/api/v2/users</c>, through the real host on a real replica set.
/// Differences from Node: ids are <c>id</c> (not <c>_id</c>), lists are <c>{ items, nextCursor }</c>, field errors are an
/// <c>errors</c> object on a <c>urn:huishoudplanner:problem:validation_error</c> problem.
/// </summary>
public sealed class UsersApiTests(MongoContainerFixture mongo)
{
    private const string Unknown = "0123456789abcdef01234567";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await UsersHost.Json(response);
        body.GetProperty("type").GetString().Should().Be($"urn:huishoudplanner:problem:{code}");
        body.GetProperty("status").GetInt32().Should().Be((int)status);
        body.TryGetProperty("traceId", out _).Should().BeTrue();
    }

    private static string[] ErrorFields(JsonElement problem) =>
        [.. problem.GetProperty("errors").EnumerateObject().Select(p => p.Name)];

    // ---- list -------------------------------------------------------------------------------

    [Fact]
    public async Task List_returnsTheSeededUsers_asJson_withStringIds_andNeedsNoProfile()
    {
        await using var host = await UsersHost.StartAsync(mongo);

        var response = await host.Send(HttpMethod.Get, "/api/v2/users", client: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await UsersHost.Json(response);
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items.Select(u => u.GetProperty("name").GetString()).Should().Equal("Persoon 1", "Persoon 2");
        items[0].GetProperty("id").GetString().Should().Be(host.AdminId);
        items.Select(u => u.GetProperty("role").GetString()).Should().Equal("admin", "member");
        DateTimeOffset.Parse(items[0].GetProperty("createdAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(UsersHost.Now);
        body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task List_filtersByActive_andDeactivatedUsersStayInTheFullList()
    {
        await using var host = await UsersHost.StartAsync(mongo);
        var created = await UsersHost.Json(await host.Send(HttpMethod.Post, "/api/v2/users", host.AdminId, new { name = "Tijdelijk", color = "#000000" }));
        var id = created.GetProperty("id").GetString()!;
        var deactivated = await host.Send(HttpMethod.Patch, $"/api/v2/users/{id}", host.AdminId, new { active = false });
        deactivated.StatusCode.Should().Be(HttpStatusCode.OK);

        var active = await UsersHost.Json(await host.Send(HttpMethod.Get, "/api/v2/users?active=true"));
        var all = await UsersHost.Json(await host.Send(HttpMethod.Get, "/api/v2/users"));
        var inactive = await UsersHost.Json(await host.Send(HttpMethod.Get, "/api/v2/users?active=false"));

        Ids(active).Should().NotContain(id);
        Ids(all).Should().Contain(id);
        Ids(inactive).Should().Equal(id);
    }

    [Fact]
    public async Task Delete_isNotARoute_aPersonIsDeactivatedInstead()
    {
        await using var host = await UsersHost.StartAsync(mongo);

        var response = await host.Send(HttpMethod.Delete, $"/api/v2/users/{host.MemberId}", host.AdminId);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task List_pagesWithLimitAndCursor_inCreationOrder()
    {
        await using var host = await UsersHost.StartAsync(mongo);
        await host.Send(HttpMethod.Post, "/api/v2/users", host.AdminId, new { name = "Derde", color = "#000000" });

        var first = await UsersHost.Json(await host.Send(HttpMethod.Get, "/api/v2/users?limit=2"));
        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await UsersHost.Json(await host.Send(HttpMethod.Get, $"/api/v2/users?limit=2&cursor={Uri.EscapeDataString(cursor!)}"));

        Names(first).Should().Equal("Persoon 1", "Persoon 2");
        Names(second).Should().Equal("Derde");
        second.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("active=maybe", "active")]
    [InlineData("limit=abc", "limit")]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=501", "limit")]
    [InlineData("cursor=garbage", "cursor")]
    public async Task List_badQuery_isAValidationError(string query, string field)
    {
        await using var host = await UsersHost.StartAsync(mongo);

        var response = await host.Send(HttpMethod.Get, $"/api/v2/users?{query}");

        await AssertProblem(response, HttpStatusCode.BadRequest, "validation_error");
        ErrorFields(await UsersHost.Json(response)).Should().Contain(field);
    }

    // ---- create -----------------------------------------------------------------------------

    [Fact]
    public async Task Create_returns201_normalisesWeekdays_andAuditsTheCreateWithSourceUi()
    {
        await using var host = await UsersHost.StartAsync(mongo);
        var before = await host.AuditCount();

        var response = await host.Send(HttpMethod.Post, "/api/v2/users", host.AdminId, new { name = "Logé", color = "#16a34a", unavailableWeekdays = new[] { 2, 2, 0 } });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await UsersHost.Json(response);
        body.GetProperty("unavailableWeekdays").EnumerateArray().Select(d => d.GetInt32()).Should().Equal(0, 2);
        body.GetProperty("active").GetBoolean().Should().BeTrue();
        body.GetProperty("role").GetString().Should().Be("member");
        body.GetProperty("dailyBudgetMinutes").GetProperty("weekday").GetInt32().Should().Be(60);
        body.GetProperty("maxDailyMinutes").GetProperty("weekend").GetInt32().Should().Be(120);
        body.GetProperty("browserNotifications").GetProperty("enabled").GetBoolean().Should().BeFalse();
        var entries = await host.AuditSince(before);
        var entry = entries.Should().ContainSingle().Subject;
        entry["entity"].AsString.Should().Be("user");
        entry["action"].AsString.Should().Be("create");
        entry["source"].AsString.Should().Be("ui");
        entry["entityId"].AsObjectId.ToString().Should().Be(body.GetProperty("id").GetString());
        entry["actorId"].AsObjectId.ToString().Should().Be(host.AdminId);
        entry["after"].AsBsonDocument["name"].AsString.Should().Be("Logé");
        entry["before"].AsBsonDocument.ElementCount.Should().Be(0);
    }

    [Fact]
    public async Task Create_withoutTheWebClientHeader_isAuditedWithSourceApi()
    {
        await using var host = await UsersHost.StartAsync(mongo);

        await host.Send(HttpMethod.Post, "/api/v2/users", host.AdminId, new { name = "X", color = "#000000" }, client: null);

        (await host.AuditSince(2))[0]["source"].AsString.Should().Be("api");
    }

    [Fact]
    public async Task Create_storesTheDocumentWithTheNodeFieldNamesAndTypes()
    {
        await using var host = await UsersHost.StartAsync(mongo);

        var body = await UsersHost.Json(await host.Send(HttpMethod.Post, "/api/v2/users", host.AdminId, new { name = "Anna", color = "#16a34a", role = "planner" }));

        var stored = await host.StoredUser(body.GetProperty("id").GetString()!);
        stored.Names.Should().Equal("_id", "name", "color", "active", "role", "unavailableWeekdays", "dailyBudgetMinutes", "maxDailyMinutes", "browserNotifications", "createdAt", "updatedAt", "version");
        stored["role"].AsString.Should().Be("planner");
        stored["dailyBudgetMinutes"]["weekday"].BsonType.Should().Be(BsonType.Int32);
        stored["createdAt"].BsonType.Should().Be(BsonType.DateTime);
    }

    [Fact]
    public async Task Create_invalidInput_is400WithFieldNames_andWritesNothing()
    {
        await using var host = await UsersHost.StartAsync(mongo);
        var audit = await host.AuditCount();

        var response = await host.Send(HttpMethod.Post, "/api/v2/users", host.AdminId, new { color = "blue", unavailableWeekdays = new[] { 7 } });

        await AssertProblem(response, HttpStatusCode.BadRequest, "validation_error");
        ErrorFields(await UsersHost.Json(response)).Should().Contain(["name", "color", "unavailableWeekdays.0"]);
        (await host.Users.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(2);
        (await host.AuditCount()).Should().Be(audit);
    }

    [Fact]
    public async Task Create_withoutAProfile_is400ProfileRequired()
    {
        await using var host = await UsersHost.StartAsync(mongo);

        var response = await host.Send(HttpMethod.Post, "/api/v2/users", body: new { name = "X", color = "#000000" });

        await AssertProblem(response, HttpStatusCode.BadRequest, "profile_required");
    }

    [Fact]
    public async Task Create_withAnInactiveProfile_is400ProfileRequired()
    {
        await using var host = await UsersHost.StartAsync(mongo);
        await host.Send(HttpMethod.Patch, $"/api/v2/users/{host.MemberId}", host.AdminId, new { active = false });

        var response = await host.Send(HttpMethod.Post, "/api/v2/users", host.MemberId, new { name = "X", color = "#000000" });

        await AssertProblem(response, HttpStatusCode.BadRequest, "profile_required");
    }

    [Fact]
    public async Task Create_anAdministratorCanAssignThePlannerRole()
    {
        await using var host = await UsersHost.StartAsync(mongo);

        var response = await host.Send(HttpMethod.Post, "/api/v2/users", host.AdminId, new { name = "Planner", color = "#16a34a", role = "planner" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await UsersHost.Json(response);
        body.GetProperty("name").GetString().Should().Be("Planner");
        body.GetProperty("role").GetString().Should().Be("planner");
    }

    [Fact]
    public async Task Create_aPlannerCannotManagePeople_403PermissionDenied()
    {
        await using var host = await UsersHost.StartAsync(mongo);
        var planner = await UsersHost.Json(await host.Send(HttpMethod.Post, "/api/v2/users", host.AdminId, new { name = "Planner", color = "#16a34a", role = "planner" }));
        var plannerId = planner.GetProperty("id").GetString()!;

        var create = await host.Send(HttpMethod.Post, "/api/v2/users", plannerId, new { name = "Niet toegestaan", color = "#000000" });
        var patch = await host.Send(HttpMethod.Patch, $"/api/v2/users/{host.MemberId}", plannerId, new { name = "Nee" });

        await AssertProblem(create, HttpStatusCode.Forbidden, "permission_denied");
        await AssertProblem(patch, HttpStatusCode.Forbidden, "permission_denied");
    }

    [Fact]
    public async Task Create_aMemberCannotManagePeople_403PermissionDenied()
    {
        await using var host = await UsersHost.StartAsync(mongo);

        var create = await host.Send(HttpMethod.Post, "/api/v2/users", host.MemberId, new { name = "Nee", color = "#000000" });
        var patch = await host.Send(HttpMethod.Patch, $"/api/v2/users/{host.AdminId}", host.MemberId, new { name = "Nee" });

        await AssertProblem(create, HttpStatusCode.Forbidden, "permission_denied");
        await AssertProblem(patch, HttpStatusCode.Forbidden, "permission_denied");
        (await host.Users.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(2);
    }

    [Fact]
    public async Task Create_malformedJson_isAProblemDetails400_notAnEmptyBody()
    {
        await using var host = await UsersHost.StartAsync(mongo);

        var response = await host.Send(HttpMethod.Post, "/api/v2/users", host.AdminId, "{not json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    // ---- patch ------------------------------------------------------------------------------

    [Fact]
    public async Task Patch_updatesAvailabilityAndBudget_auditHoldsOnlyTheChangedFields_sourceApi()
    {
        await using var host = await UsersHost.StartAsync(mongo);
        var before = await host.AuditCount();

        var response = await host.Send(
            HttpMethod.Patch, $"/api/v2/users/{host.MemberId}", host.AdminId,
            new { unavailableWeekdays = new[] { 2 }, dailyBudgetMinutes = new { weekday = 45, weekend = 120 } }, client: "api");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = (await host.AuditSince(before)).Should().ContainSingle().Subject;
        entry["action"].AsString.Should().Be("update");
        entry["source"].AsString.Should().Be("api");
        entry["before"].ToJson().Should().Be(BsonDocument.Parse("{ unavailableWeekdays: [], dailyBudgetMinutes: { weekday: 60 } }").ToJson());
        entry["after"].ToJson().Should().Be(BsonDocument.Parse("{ unavailableWeekdays: [2], dailyBudgetMinutes: { weekday: 45 } }").ToJson());
    }

    [Fact]
    public async Task Patch_setsOnlyTheChangedFieldsInTheDocument_andKeepsUnknownOnes()
    {
        await using var host = await UsersHost.StartAsync(mongo);
        await host.Users.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(host.MemberId)),
            Builders<BsonDocument>.Update.Set("nodeOnlyField", "kept").Unset("maxDailyMinutes"),
            cancellationToken: Ct);

        await host.Send(HttpMethod.Patch, $"/api/v2/users/{host.MemberId}", host.AdminId, new { name = "Anna" });

        var stored = await host.StoredUser(host.MemberId);
        stored["name"].AsString.Should().Be("Anna");
        stored["nodeOnlyField"].AsString.Should().Be("kept");
        stored.Contains("maxDailyMinutes").Should().BeFalse("the patch sets only the fields it changes");
        stored["updatedAt"].ToUniversalTime().Should().Be(UsersHost.Now.UtcDateTime);
    }

    [Fact]
    public async Task Patch_deactivates_insteadOfDeleting()
    {
        await using var host = await UsersHost.StartAsync(mongo);

        var response = await host.Send(HttpMethod.Patch, $"/api/v2/users/{host.MemberId}", host.AdminId, new { active = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await UsersHost.Json(response)).GetProperty("active").GetBoolean().Should().BeFalse();
        (await host.StoredUser(host.MemberId))["active"].AsBoolean.Should().BeFalse();
    }

    [Fact]
    public async Task Patch_thatChangesNothing_writesAndAuditsNothing_andAnswers200()
    {
        await using var host = await UsersHost.StartAsync(mongo);
        var audit = await host.AuditCount();
        var stored = await host.StoredUser(host.MemberId);

        var response = await host.Send(HttpMethod.Patch, $"/api/v2/users/{host.MemberId}", host.AdminId, new { name = "Persoon 2", unavailableWeekdays = Array.Empty<int>() });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.AuditCount()).Should().Be(audit);
        (await host.StoredUser(host.MemberId)).ToJson().Should().Be(stored.ToJson());
    }

    [Fact]
    public async Task Patch_theLastActiveAdministrator_cannotBeDemotedOrDeactivated_409LastAdmin()
    {
        await using var host = await UsersHost.StartAsync(mongo);
        var audit = await host.AuditCount();

        var demote = await host.Send(HttpMethod.Patch, $"/api/v2/users/{host.AdminId}", host.AdminId, new { role = "member" });
        var deactivate = await host.Send(HttpMethod.Patch, $"/api/v2/users/{host.AdminId}", host.AdminId, new { active = false });

        await AssertProblem(demote, HttpStatusCode.Conflict, "last_admin");
        await AssertProblem(deactivate, HttpStatusCode.Conflict, "last_admin");
        (await host.StoredUser(host.AdminId))["role"].AsString.Should().Be("admin");
        (await host.AuditCount()).Should().Be(audit);
    }

    [Fact]
    public async Task Patch_anAdministratorCanBeDemotedWhenAnotherActiveOneExists()
    {
        await using var host = await UsersHost.StartAsync(mongo);
        await host.Send(HttpMethod.Patch, $"/api/v2/users/{host.MemberId}", host.AdminId, new { role = "admin" });

        var response = await host.Send(HttpMethod.Patch, $"/api/v2/users/{host.AdminId}", host.MemberId, new { role = "member" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Patch_aUserStoredWithoutARole_countsAsAnotherAdministrator()
    {
        await using var host = await UsersHost.StartAsync(mongo);
        await host.Users.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(host.MemberId)),
            Builders<BsonDocument>.Update.Unset("role"),
            cancellationToken: Ct);

        var response = await host.Send(HttpMethod.Patch, $"/api/v2/users/{host.AdminId}", host.AdminId, new { active = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Patch_unknownId_is404NotFound_malformedId_is400()
    {
        await using var host = await UsersHost.StartAsync(mongo);

        var unknown = await host.Send(HttpMethod.Patch, $"/api/v2/users/{Unknown}", host.AdminId, new { name = "X" });
        var malformed = await host.Send(HttpMethod.Patch, "/api/v2/users/nope", host.AdminId, new { name = "X" });

        await AssertProblem(unknown, HttpStatusCode.NotFound, "not_found");
        await AssertProblem(malformed, HttpStatusCode.BadRequest, "validation_error");
        ErrorFields(await UsersHost.Json(malformed)).Should().Contain("id");
    }

    [Fact]
    public async Task Patch_invalidInput_is400_andStoresNothing()
    {
        await using var host = await UsersHost.StartAsync(mongo);
        var stored = await host.StoredUser(host.MemberId);

        var response = await host.Send(HttpMethod.Patch, $"/api/v2/users/{host.MemberId}", host.AdminId, new { name = " ", role = "boss", color = "x" });

        await AssertProblem(response, HttpStatusCode.BadRequest, "validation_error");
        ErrorFields(await UsersHost.Json(response)).Should().Contain(["name", "role", "color"]);
        (await host.StoredUser(host.MemberId)).ToJson().Should().Be(stored.ToJson());
    }

    [Fact]
    public async Task Patch_withoutAProfile_is400ProfileRequired()
    {
        await using var host = await UsersHost.StartAsync(mongo);

        var response = await host.Send(HttpMethod.Patch, $"/api/v2/users/{host.MemberId}", body: new { name = "X" });

        await AssertProblem(response, HttpStatusCode.BadRequest, "profile_required");
    }

    // ---- documents of older installations ---------------------------------------------------

    [Fact]
    public async Task List_readsDocumentsFromBeforeRolesAndLimits_withTheirDefaults()
    {
        await using var host = await UsersHost.StartAsync(mongo);
        await host.Users.InsertOneAsync(
            new BsonDocument
            {
                { "name", "Oud" },
                { "color", "#123456" },
                { "active", true },
                { "unavailableWeekdays", new BsonArray() },
                { "dailyBudgetMinutes", new BsonDocument { { "weekday", 60 }, { "weekend", 120 } } },
                { "createdAt", new BsonDateTime(UsersHost.Now.AddDays(1).UtcDateTime) },
                { "updatedAt", new BsonDateTime(UsersHost.Now.AddDays(1).UtcDateTime) },
            },
            cancellationToken: Ct);

        var users = (await UsersHost.Json(await host.Send(HttpMethod.Get, "/api/v2/users"))).GetProperty("items").EnumerateArray().ToList();

        var old = users.Single(u => u.GetProperty("name").GetString() == "Oud");
        old.GetProperty("role").GetString().Should().Be("admin");
        old.GetProperty("maxDailyMinutes").GetProperty("weekday").GetInt32().Should().Be(480);
        old.GetProperty("maxDailyMinutes").GetProperty("weekend").GetInt32().Should().Be(480);
        old.GetProperty("browserNotifications").GetProperty("enabled").GetBoolean().Should().BeFalse();
        old.GetProperty("browserNotifications").GetProperty("times").GetArrayLength().Should().Be(0);
    }

    private static List<string> Ids(JsonElement list) =>
        [.. list.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("id").GetString()!)];

    private static List<string> Names(JsonElement list) =>
        [.. list.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("name").GetString()!)];
}
