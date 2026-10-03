#pragma warning disable CA1861 // inline arrays in request bodies of tests
using System.Net;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// The request bodies of the users writes are read like the Node Zod schemas did: malformed JSON, an empty body, a wrong type
/// and an explicit JSON null are each a <c>400 validation_error</c> keyed by field, and nothing is stored.
/// </summary>
public sealed class UserRequestBodyTests(MongoContainerFixture mongo)
{
    private const string Valid = """{"name":"Anna","color":"#16a34a"}""";

    public static TheoryData<string, string> BadBodies() => new()
    {
        { "{not json", "body" },
        { "", "body" },
        { "[]", "body" },
        { """{"name":5,"color":"#000000"}""", "name" },
        { """{"name":null,"color":"#000000"}""", "name" },
        { """{"name":"A","color":null}""", "color" },
        { """{"name":"A","color":7}""", "color" },
        { """{"name":"A","color":"#000000","role":null}""", "role" },
        { """{"name":"A","color":"#000000","role":3}""", "role" },
        { """{"name":"A","color":"#000000","unavailableWeekdays":"x"}""", "unavailableWeekdays" },
        { """{"name":"A","color":"#000000","unavailableWeekdays":null}""", "unavailableWeekdays" },
        { """{"name":"A","color":"#000000","unavailableWeekdays":[1.5]}""", "unavailableWeekdays.0" },
        { """{"name":"A","color":"#000000","unavailableWeekdays":["1"]}""", "unavailableWeekdays.0" },
        { """{"name":"A","color":"#000000","dailyBudgetMinutes":null}""", "dailyBudgetMinutes" },
        { """{"name":"A","color":"#000000","dailyBudgetMinutes":{"weekday":"a","weekend":1}}""", "dailyBudgetMinutes.weekday" },
        { """{"name":"A","color":"#000000","maxDailyMinutes":{"weekday":1,"weekend":null}}""", "maxDailyMinutes.weekend" },
    };

    public static TheoryData<string, string> BadPatchBodies() => new()
    {
        { """{"active":null}""", "active" },
        { """{"active":"yes"}""", "active" },
        { """{"browserNotifications":null}""", "browserNotifications" },
        { """{"browserNotifications":{"enabled":"yes","times":[]}}""", "browserNotifications.enabled" },
        { """{"browserNotifications":{"enabled":true,"times":[5]}}""", "browserNotifications.times.0" },
    };

    public static TheoryData<string, string> BadNotificationBodies() => new()
    {
        { "{not json", "body" },
        { "", "body" },
        { "null", "body" },
        { """{"enabled":"yes","times":[]}""", "enabled" },
        { """{"enabled":null,"times":[]}""", "enabled" },
        { """{"enabled":true,"times":null}""", "times" },
        { """{"enabled":true,"times":"08:00"}""", "times" },
        { """{"enabled":true,"times":[5]}""", "times.0" },
        { """{"enabled":true,"times":[null]}""", "times.0" },
    };

    private static async Task AssertRejected(UsersHost host, HttpResponseMessage response, string field)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await UsersHost.Json(response);
        body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:validation_error");
        body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().Contain(field);
        (await host.Users.CountDocumentsAsync(MongoDB.Driver.FilterDefinition<BsonDocument>.Empty, cancellationToken: TestContext.Current.CancellationToken)).Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task Post_badBody_is400ValidationError(string json, string field)
    {
        await using var host = await UsersHost.StartAsync(mongo);

        var response = await host.Send(HttpMethod.Post, "/api/v2/users", host.AdminId, json);

        await AssertRejected(host, response, field);
    }

    [Theory]
    [MemberData(nameof(BadBodies))]
    [MemberData(nameof(BadPatchBodies))]
    public async Task Patch_badBody_is400ValidationError_andStoresNothing(string json, string field)
    {
        await using var host = await UsersHost.StartAsync(mongo);
        var stored = await host.StoredUser(host.MemberId);

        var response = await host.Send(HttpMethod.Patch, $"/api/v2/users/{host.MemberId}", host.AdminId, json);

        await AssertRejected(host, response, field);
        (await host.StoredUser(host.MemberId)).ToJson().Should().Be(stored.ToJson());
    }

    [Theory]
    [MemberData(nameof(BadNotificationBodies))]
    public async Task Put_badBody_is400ValidationError_andStoresNothing(string json, string field)
    {
        await using var host = await UsersHost.StartAsync(mongo);
        var stored = await host.StoredUser(host.MemberId);

        var response = await host.Send(HttpMethod.Put, $"/api/v2/users/{host.MemberId}/browser-notifications", host.MemberId, json);

        await AssertRejected(host, response, field);
        (await host.StoredUser(host.MemberId)).ToJson().Should().Be(stored.ToJson());
    }

    [Fact]
    public async Task Post_unknownFields_areIgnored()
    {
        await using var host = await UsersHost.StartAsync(mongo);

        var response = await host.Send(HttpMethod.Post, "/api/v2/users", host.AdminId, Valid.Replace("}", ",\"extra\":1}", StringComparison.Ordinal));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
