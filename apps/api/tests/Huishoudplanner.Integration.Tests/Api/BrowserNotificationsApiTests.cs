#pragma warning disable CA1861 // inline arrays in request bodies of tests
using System.Net;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Ports apps/server/test/browser-notifications.test.ts to <c>PUT /api/v2/users/{id}/browser-notifications</c>.
/// The Node "documents that predate the field" case went through the JSON import; here the document is written without
/// the field directly (the import is a later slice).
/// </summary>
public sealed class BrowserNotificationsApiTests(MongoContainerFixture mongo)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> Put(UsersHost host, string actorId, string targetId, object body) =>
        host.Send(HttpMethod.Put, $"/api/v2/users/{targetId}/browser-notifications", actorId, body);

    private static async Task<(bool Enabled, string Times)> Stored(UsersHost host, string id)
    {
        var users = (await UsersHost.Json(await host.Send(HttpMethod.Get, "/api/v2/users"))).GetProperty("items").EnumerateArray();
        var notifications = users.Single(u => u.GetProperty("id").GetString() == id).GetProperty("browserNotifications");
        return (notifications.GetProperty("enabled").GetBoolean(), string.Join(",", notifications.GetProperty("times").EnumerateArray().Select(t => t.GetString()!)));
    }

    [Fact]
    public async Task Read_aUserThatNeverConfiguredThem_isDisabledWithoutTimes()
    {
        await using var host = await UsersHost.StartAsync(mongo);

        (await Stored(host, host.MemberId)).Should().Be((false, ""));
    }

    [Fact]
    public async Task Read_documentsThatPredateTheField_areDisabledWithoutTimes()
    {
        await using var host = await UsersHost.StartAsync(mongo);
        await host.Users.UpdateManyAsync(
            FilterDefinition<BsonDocument>.Empty, Builders<BsonDocument>.Update.Unset("browserNotifications"), cancellationToken: Ct);

        (await Stored(host, host.AdminId)).Should().Be((false, ""));
        (await Stored(host, host.MemberId)).Should().Be((false, ""));
    }

    [Fact]
    public async Task Put_aPersonSetsTheirOwnMoments_sorted_andItIsAudited()
    {
        await using var host = await UsersHost.StartAsync(mongo);
        var before = await host.AuditCount();

        var response = await Put(host, host.MemberId, host.MemberId, new { enabled = true, times = new[] { "18:30", "08:00" } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var notifications = (await UsersHost.Json(response)).GetProperty("browserNotifications");
        notifications.GetProperty("enabled").GetBoolean().Should().BeTrue();
        notifications.GetProperty("times").EnumerateArray().Select(t => t.GetString()).Should().Equal("08:00", "18:30");
        var entry = (await host.AuditSince(before)).Should().ContainSingle().Subject;
        entry["entity"].AsString.Should().Be("user");
        entry["action"].AsString.Should().Be("update");
        entry["source"].AsString.Should().Be("ui");
        entry["actorId"].AsObjectId.ToString().Should().Be(host.MemberId);
        entry["entityId"].AsObjectId.ToString().Should().Be(host.MemberId);
        entry["before"].ToJson().Should().Be(BsonDocument.Parse("{ browserNotifications: { enabled: false, times: [] } }").ToJson());
        entry["after"].ToJson().Should().Be(BsonDocument.Parse("{ browserNotifications: { enabled: true, times: ['08:00', '18:30'] } }").ToJson());
        (await Stored(host, host.MemberId)).Should().Be((true, "08:00,18:30"));
    }

    [Fact]
    public async Task Put_auditsOnlyTheChangedFieldOfTheSetting()
    {
        await using var host = await UsersHost.StartAsync(mongo);
        await Put(host, host.MemberId, host.MemberId, new { enabled = true, times = new[] { "18:30", "08:00" } });
        var before = await host.AuditCount();

        await Put(host, host.MemberId, host.MemberId, new { enabled = false, times = new[] { "18:30", "08:00" } });

        var entry = (await host.AuditSince(before)).Should().ContainSingle().Subject;
        entry["before"].ToJson().Should().Be(BsonDocument.Parse("{ browserNotifications: { enabled: true } }").ToJson());
        entry["after"].ToJson().Should().Be(BsonDocument.Parse("{ browserNotifications: { enabled: false } }").ToJson());
    }

    [Fact]
    public async Task Put_aSettingThatDoesNotChange_writesAndAuditsNothing_andAnswers200()
    {
        await using var host = await UsersHost.StartAsync(mongo);
        await Put(host, host.MemberId, host.MemberId, new { enabled = false, times = new[] { "08:00", "18:30" } });
        var audit = await host.AuditCount();
        var stored = await host.StoredUser(host.MemberId);

        var response = await Put(host, host.MemberId, host.MemberId, new { enabled = false, times = new[] { "08:00", "18:30" } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.AuditCount()).Should().Be(audit);
        (await host.StoredUser(host.MemberId)).ToJson().Should().Be(stored.ToJson());
    }

    [Fact]
    public async Task Put_aMemberCannotChangeSomeoneElsesMoments_403PermissionDenied()
    {
        await using var host = await UsersHost.StartAsync(mongo);
        var before = await Stored(host, host.AdminId);

        var response = await Put(host, host.MemberId, host.AdminId, new { enabled = true, times = new[] { "07:00" } });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await UsersHost.Json(response)).GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:permission_denied");
        (await Stored(host, host.AdminId)).Should().Be(before);
    }

    [Fact]
    public async Task Put_anAdministratorChangesAnotherPersonsMoments_attributedToTheAdministrator()
    {
        await using var host = await UsersHost.StartAsync(mongo);
        var before = await host.AuditCount();

        var response = await Put(host, host.AdminId, host.MemberId, new { enabled = true, times = new[] { "07:15" } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = (await host.AuditSince(before)).Should().ContainSingle().Subject;
        entry["actorId"].AsObjectId.ToString().Should().Be(host.AdminId);
        entry["entityId"].AsObjectId.ToString().Should().Be(host.MemberId);
        (await Stored(host, host.MemberId)).Should().Be((true, "07:15"));
    }

    [Fact]
    public async Task Put_withoutAProfile_is400ProfileRequired()
    {
        await using var host = await UsersHost.StartAsync(mongo);

        var response = await host.Send(HttpMethod.Put, $"/api/v2/users/{host.MemberId}/browser-notifications", body: new { enabled = true, times = Array.Empty<string>() });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await UsersHost.Json(response)).GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:profile_required");
    }

    [Theory]
    [InlineData("{\"enabled\":true,\"times\":[\"25:00\"]}", "times.0")]
    [InlineData("{\"enabled\":true,\"times\":[\"8:00\"]}", "times.0")]
    [InlineData("{\"enabled\":true,\"times\":[\"08:00\",\"08:00\"]}", "times")]
    [InlineData("{\"enabled\":true,\"times\":[\"01:00\",\"02:00\",\"03:00\",\"04:00\",\"05:00\",\"06:00\",\"07:00\"]}", "times")]
    [InlineData("{\"times\":[\"08:00\"]}", "enabled")]
    [InlineData("{\"enabled\":true}", "times")]
    public async Task Put_invalidMoments_are400WithFieldDetails_andStoreNothing(string json, string field)
    {
        await using var host = await UsersHost.StartAsync(mongo);
        var before = await Stored(host, host.MemberId);

        var response = await Put(host, host.MemberId, host.MemberId, json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await UsersHost.Json(response);
        problem.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:validation_error");
        problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().Contain(field);
        (await Stored(host, host.MemberId)).Should().Be(before);
    }

    [Fact]
    public async Task Put_unknownPerson_is404_malformedId_is400()
    {
        await using var host = await UsersHost.StartAsync(mongo);
        var body = new { enabled = true, times = Array.Empty<string>() };

        var unknown = await Put(host, host.AdminId, "0123456789abcdef01234567", body);
        var malformed = await Put(host, host.AdminId, "nope", body);

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
