#pragma warning disable CA1861 // inline arrays in request bodies of tests
using System.Net;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// <c>points-redemptions.test.ts</c> on <c>/api/v2/points/redemptions</c>, the real host and a real replica set (one database per test; today is
/// Wednesday 2026-09-16, 10:00 Amsterdam). Points are earned by writing execution entries straight into the ledger, so the tests do not depend on the
/// live paths. The scenarios of the statistics reset live in <c>StatisticsResetTests</c>, those of the conversion settings in
/// <c>SettingsEndpointTests</c>; the export and import scenarios wait for slice 6.5, the progress ones for slice 4.4.
/// </summary>
public sealed class RedemptionEndpointTests(MongoContainerFixture mongo)
{
    private const string Url = "/api/v2/points/redemptions";
    private const string KeyA = "redeem-key-aaaaaaaaaaaa";
    private const string KeyB = "redeem-key-bbbbbbbbbbbb";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Type(JsonElement problem) => problem.GetProperty("type").GetString()!;

    private static Task<(HttpStatusCode Status, JsonElement Body)> Redeem(PointsHarness h, ObjectId? actor, object body) => h.SendAsync(HttpMethod.Post, Url, actor, body);

    private static async Task Earn(PointsHarness h, ObjectId person, int points, string day = "2026-09-16") => await h.InsertExecutionEntryAsync(person, day, points);

    private static async Task<(long Entries, long Audit, long Guards)> State(PointsHarness h) => (
        await h.Ledger.CountDocumentsAsync(new BsonDocument(), cancellationToken: Ct),
        await h.AuditCountAsync(),
        await h.Database.GetCollection<BsonDocument>("pointGuards").CountDocumentsAsync(new BsonDocument(), cancellationToken: Ct));

    private static async Task<JsonElement> BalanceOf(PointsHarness h, ObjectId person)
    {
        var (_, body) = await h.GetAsync("/api/v2/points/balances");
        return body.GetProperty("balances").EnumerateArray().Single(b => b.GetProperty("personId").GetString() == person.ToString());
    }

    private static async Task<List<BsonDocument>> Redemptions(PointsHarness h) => await h.EntriesAsync("redemption");

    // ---- booking

    [Fact]
    public async Task Book_theActorsOwnRedemptionIsANegativeBookedEntryDatedTodayThatAuditsOnceAndLowersTheBalance()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 10);
        (await h.SendAsync(HttpMethod.Patch, "/api/v2/settings", h.Admin, new { centsPerPoint = 25 })).Status.Should().Be(HttpStatusCode.OK);
        var auditBefore = await h.AuditCountAsync();

        var (status, body) = await Redeem(h, h.P1, new { points = 4, note = "Pizza", requestId = KeyA });

        status.Should().Be(HttpStatusCode.Created, body.ToString());
        body.GetProperty("id").GetString().Should().MatchRegex("^[0-9a-f]{24}$");
        body.GetProperty("key").GetString().Should().MatchRegex("^redemption:[0-9a-f]{24}$");
        (body.GetProperty("kind").GetString(), body.GetProperty("personId").GetString(), body.GetProperty("amount").GetInt32()).Should().Be(("redemption", h.P1.ToString(), -4));
        (body.GetProperty("date").GetString(), body.GetProperty("weekStart").GetString()).Should().Be(("2026-09-16", "2026-09-14"));
        body.GetProperty("periodStart").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("occurrenceId").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("taskId").ValueKind.Should().Be(JsonValueKind.Null);
        (body.GetProperty("titleSnapshot").GetString(), body.GetProperty("note").GetString(), body.GetProperty("centsPerPointSnapshot").GetInt32(), body.GetProperty("currencyCodeSnapshot").GetString(), body.GetProperty("source").GetString())
            .Should().Be((string.Empty, "Pizza", 25, "EUR", "live"));
        body.GetProperty("createdAt").GetDateTimeOffset().Should().Be(DateTimeOffset.Parse(PointsHarness.Now, System.Globalization.CultureInfo.InvariantCulture));
        body.TryGetProperty("requestId", out _).Should().BeFalse("the request key stays server-side");

        var audit = (await h.PointsAuditAsync()).Should().ContainSingle().Subject;
        (await h.AuditCountAsync()).Should().Be(auditBefore + 1);
        (audit["action"].AsString, audit["source"].AsString, audit["actorId"], audit["entityId"].ToString()).Should().Be(("create", "ui", h.P1, body.GetProperty("id").GetString()));
        audit["meta"].Should().Be(new BsonDocument("reason", "redemption"));
        audit["before"].Should().Be(new BsonDocument());
        var after = audit["after"].AsBsonDocument;
        after["kind"].AsString.Should().Be("redemption");
        after["amount"].AsInt32.Should().Be(-4);
        after["note"].AsString.Should().Be("Pizza");
        after["centsPerPointSnapshot"].AsInt32.Should().Be(25);
        after["periodStart"].Should().Be(BsonNull.Value);
        after.Contains("requestId").Should().BeFalse("the request key is retry bookkeeping");
        audit.ToJson().Should().NotContain(KeyA);
        (await Redemptions(h)).Should().ContainSingle().Which["requestId"].AsString.Should().Be(KeyA);

        var balance = await BalanceOf(h, h.P1);
        (balance.GetProperty("points").GetInt64(), balance.GetProperty("earned").GetInt64(), balance.GetProperty("redeemed").GetInt64(), balance.GetProperty("executions").GetInt32()).Should().Be((6, 10, 4, 1));
        var money = balance.GetProperty("money");
        (money.GetProperty("earned").GetInt64(), money.GetProperty("redeemed").GetInt64(), money.GetProperty("balance").GetInt64()).Should().Be((250, 100, 150));
    }

    [Fact]
    public async Task Book_aBookingWithoutAPersonIsForTheActor()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P2, 5);

        var (status, body) = await Redeem(h, h.P2, new { points = 1 });

        status.Should().Be(HttpStatusCode.Created);
        body.GetProperty("personId").GetString().Should().Be(h.P2.ToString());
    }

    [Fact]
    public async Task Book_theNoteIsTrimmedAndAnEmptyNoteIsNone()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 10);

        var none = (await Redeem(h, h.P1, new { points = 1 })).Body.GetProperty("note");
        var blank = (await Redeem(h, h.P1, new { points = 1, note = "   " })).Body.GetProperty("note");
        var trimmed = (await Redeem(h, h.P1, new { points = 1, note = "  Ijsje  " })).Body.GetProperty("note");

        (none.ValueKind, blank.ValueKind, trimmed.GetString()).Should().Be((JsonValueKind.Null, JsonValueKind.Null, "Ijsje"));
        (await Redemptions(h)).Select(d => d["note"]).Should().BeEquivalentTo(new BsonValue[] { BsonNull.Value, BsonNull.Value, "Ijsje" });
    }

    [Fact]
    public async Task Book_aNoteOfExactly200CharactersIsAccepted()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 5);

        (await Redeem(h, h.P1, new { points = 1, note = new string('x', 200) })).Status.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Book_theRedemptionIsListedAmongTheEntriesOfThePersonAndAnExecutionKeepsNullsForTheRedemptionFields()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 5);
        await Redeem(h, h.P1, new { points = 2, note = "Koffie" });

        var (status, body) = await h.GetAsync($"/api/v2/points/entries?personId={h.P1}&from=2026-09-14&to=2026-09-20");

        status.Should().Be(HttpStatusCode.OK);
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => (i.GetProperty("kind").GetString(), i.GetProperty("amount").GetInt32())).Should().BeEquivalentTo([("execution", 5), ("redemption", -2)]);
        var redemption = items.Single(i => i.GetProperty("kind").GetString() == "redemption");
        (redemption.GetProperty("note").GetString(), redemption.GetProperty("centsPerPointSnapshot").GetInt32()).Should().Be(("Koffie", 0));
        var execution = items.Single(i => i.GetProperty("kind").GetString() == "execution");
        (execution.GetProperty("note").ValueKind, execution.GetProperty("centsPerPointSnapshot").ValueKind).Should().Be((JsonValueKind.Null, JsonValueKind.Null));
    }

    [Fact]
    public async Task Book_aFactorOf0IsKeptAsTheSnapshotAndTheBalanceThenShowsNoMoney()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 5);

        var (_, body) = await Redeem(h, h.P1, new { points = 1 });

        body.GetProperty("centsPerPointSnapshot").GetInt32().Should().Be(0);
        (await BalanceOf(h, h.P1)).GetProperty("money").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Book_aBookingKeepsTheFactorAndCurrencyOfItsMomentWhileTheBalancesUseTheFactorInForceNow()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 10);
        await h.SendAsync(HttpMethod.Patch, "/api/v2/settings", h.Admin, new { currencyCode = "EUR", centsPerPoint = 20 });
        var first = (await Redeem(h, h.P1, new { points = 2 })).Body;
        await h.SendAsync(HttpMethod.Patch, "/api/v2/settings", h.Admin, new { currencyCode = "USD", centsPerPoint = 30 });
        var second = (await Redeem(h, h.P1, new { points = 3 })).Body;

        (first.GetProperty("currencyCodeSnapshot").GetString(), first.GetProperty("centsPerPointSnapshot").GetInt32()).Should().Be(("EUR", 20));
        (second.GetProperty("currencyCodeSnapshot").GetString(), second.GetProperty("centsPerPointSnapshot").GetInt32()).Should().Be(("USD", 30));
        (await h.PointsAuditAsync("create")).Last()["after"]["currencyCodeSnapshot"].AsString.Should().Be("USD");
        var (_, balances) = await h.GetAsync("/api/v2/points/balances");
        balances.GetProperty("currencyCode").GetString().Should().Be("USD");
        var money = (await BalanceOf(h, h.P1)).GetProperty("money");
        (money.GetProperty("earned").GetInt64(), money.GetProperty("redeemed").GetInt64(), money.GetProperty("balance").GetInt64()).Should().Be((300, 150, 150));
    }

    // ---- who may book, who is booked

    [Fact]
    public async Task Book_anAdministratorBooksForAnyoneAndTheAuditEntryNamesTheAdministrator()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P2, 6);
        await Earn(h, h.P1, 6);

        var (status, body) = await Redeem(h, h.Admin, new { personId = h.P2.ToString(), points = 2 });

        status.Should().Be(HttpStatusCode.Created, body.ToString());
        (body.GetProperty("personId").GetString(), body.GetProperty("amount").GetInt32()).Should().Be((h.P2.ToString(), -2));
        (await h.PointsAuditAsync("create")).Single()["actorId"].Should().Be(h.Admin);
        (await BalanceOf(h, h.P2)).GetProperty("points").GetInt64().Should().Be(4);
        (await BalanceOf(h, h.P1)).GetProperty("points").GetInt64().Should().Be(6);
    }

    [Fact]
    public async Task Book_aMemberMayNameThemselvesExplicitly()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P2, 6);

        (await Redeem(h, h.P2, new { personId = h.P2.ToString(), points = 1 })).Status.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Book_aMemberForSomebodyElseIs403PermissionDeniedAndWritesNothing()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 6);
        var before = await State(h);

        var (status, body) = await Redeem(h, h.P2, new { personId = h.P1.ToString(), points = 1 });

        status.Should().Be(HttpStatusCode.Forbidden);
        Type(body).Should().EndWith("permission_denied");
        (await State(h)).Should().Be(before);
    }

    [Fact]
    public async Task Book_needsAProfile()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 5);
        var before = await State(h);

        var (status, body) = await Redeem(h, null, new { points = 1 });

        status.Should().Be(HttpStatusCode.BadRequest);
        Type(body).Should().EndWith("profile_required");
        (await State(h)).Should().Be(before);
    }

    [Fact]
    public async Task Book_anUnknownOrInactivePersonIsAValidationErrorOnPersonIdAndWritesNothing()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 5);
        var lodger = ObjectId.GenerateNewId();
        await h.AddUserAsync(lodger, "Logé", "member", false);
        var before = await State(h);

        var unknown = await Redeem(h, h.Admin, new { personId = ObjectId.GenerateNewId().ToString(), points = 1 });
        var inactive = await Redeem(h, h.Admin, new { personId = lodger.ToString(), points = 1 });

        unknown.Status.Should().Be(HttpStatusCode.BadRequest);
        Type(unknown.Body).Should().EndWith("validation_error");
        unknown.Body.GetProperty("errors").GetProperty("personId")[0].GetString().Should().Be("unknown_user");
        inactive.Body.GetProperty("errors").GetProperty("personId")[0].GetString().Should().Be("inactive_user");
        (await State(h)).Should().Be(before);
    }

    // ---- the balance

    [Fact]
    public async Task Book_aBookingAboveTheBalanceIs409InsufficientBalanceWithTheBalanceAndTheRequestedPointsAndWritesNothing()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 5);
        var before = await State(h);

        var (status, body) = await Redeem(h, h.P1, new { points = 6 });

        status.Should().Be(HttpStatusCode.Conflict);
        Type(body).Should().EndWith("insufficient_balance");
        (body.GetProperty("balance").GetInt64(), body.GetProperty("requested").GetInt32()).Should().Be((5, 6));
        (await State(h)).Should().Be(before, "the guard write of the refused attempt is rolled back with it");
    }

    [Fact]
    public async Task Book_theExactBalanceIsAllowedAndTheNextPointIsNot()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 5);

        (await Redeem(h, h.P1, new { points = 5 })).Status.Should().Be(HttpStatusCode.Created);
        var balance = await BalanceOf(h, h.P1);
        (balance.GetProperty("points").GetInt64(), balance.GetProperty("earned").GetInt64(), balance.GetProperty("redeemed").GetInt64()).Should().Be((0, 5, 5));
        var (status, body) = await Redeem(h, h.P1, new { points = 1 });
        status.Should().Be(HttpStatusCode.Conflict);
        (body.GetProperty("balance").GetInt64(), body.GetProperty("requested").GetInt32()).Should().Be((0, 1));
    }

    [Fact]
    public async Task Book_everyEarlierRedemptionAndBonusCountsAgainstTheAllTimeBalanceNotTheVisibleRange()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 5);
        await h.InsertOtherEntryAsync(h.P1, "2026-08-31", 2, "bonus_week_done");
        h.Clock.Set("2026-09-17T08:00:00.000Z");
        (await Redeem(h, h.P1, new { points = 4 })).Status.Should().Be(HttpStatusCode.Created);
        h.Clock.Set("2026-09-23T08:00:00.000Z");

        var tooMuch = await Redeem(h, h.P1, new { points = 4 });

        tooMuch.Status.Should().Be(HttpStatusCode.Conflict);
        tooMuch.Body.GetProperty("balance").GetInt64().Should().Be(3);
        (await Redeem(h, h.P1, new { points = 3 })).Status.Should().Be(HttpStatusCode.Created);
        (await Redemptions(h)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Book_threeSimultaneousBookingsOfOnePersonNeverOverdrawTheBalance()
    {
        await using var h = await PointsHarness.StartAsync(mongo);

        for (var round = 0; round < 5; round++)
        {
            var person = ObjectId.GenerateNewId();
            await h.AddUserAsync(person, $"Ronde {round}", "member", true);
            await Earn(h, person, 10);

            var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => Redeem(h, person, new { points = 6 }), Ct)));

            results.Count(r => r.Status == HttpStatusCode.Created).Should().Be(1, "round {0}: {1}", round, string.Join(" | ", results.Select(r => $"{(int)r.Status} {r.Body}")));
            results.Where(r => r.Status != HttpStatusCode.Created).Should().OnlyContain(r => r.Status == HttpStatusCode.Conflict && (Type(r.Body).EndsWith("insufficient_balance") || Type(r.Body).EndsWith("write_conflict")));
            var balance = await BalanceOf(h, person);
            (balance.GetProperty("points").GetInt64(), balance.GetProperty("redeemed").GetInt64()).Should().Be((4, 6));
        }
    }

    [Fact]
    public async Task Book_twoSimultaneousBookingsOfOnePersonThatTogetherFitBothSucceed()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 10);

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => Redeem(h, h.P1, new { points = 4 }), Ct)));

        results.Select(r => r.Status).Should().Equal(HttpStatusCode.Created, HttpStatusCode.Created);
        (await BalanceOf(h, h.P1)).GetProperty("points").GetInt64().Should().Be(2);
        (await h.PointsAuditAsync("create")).Should().HaveCount(2);
    }

    [Fact]
    public async Task Book_anAdministratorAndTheMemberBookingForTheSamePersonAtOnceShareOneBalance()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P2, 10);

        var results = await Task.WhenAll(
            Task.Run(() => Redeem(h, h.P2, new { points = 7 }), Ct),
            Task.Run(() => Redeem(h, h.Admin, new { personId = h.P2.ToString(), points = 7 }), Ct));

        results.Count(r => r.Status == HttpStatusCode.Created).Should().Be(1);
        (await BalanceOf(h, h.P2)).GetProperty("points").GetInt64().Should().Be(3);
    }

    // ---- the request key

    [Fact]
    public async Task Book_aRepeatedRequestKeyAnswers200WithTheStoredBookingAndWritesNothing()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 10);
        var first = await Redeem(h, h.P1, new { points = 3, note = "Film", requestId = KeyA });
        var before = await State(h);

        var replay = await Redeem(h, h.P1, new { points = 3, note = "Film", requestId = KeyA });

        replay.Status.Should().Be(HttpStatusCode.OK, replay.Body.ToString());
        replay.Body.ToString().Should().Be(first.Body.ToString());
        (await State(h)).Should().Be(before);
        (await Redemptions(h)).Should().ContainSingle();
    }

    [Fact]
    public async Task Book_theReplayStillWorksOnAnotherDay_theKeyIsTheIdentityNotTheDate()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 10);
        await Redeem(h, h.P1, new { points = 3, note = "Film", requestId = KeyA });
        h.Clock.Set("2026-09-17T08:00:00.000Z");

        (await Redeem(h, h.P1, new { points = 3, note = "Film", requestId = KeyA })).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Book_theSameKeyForAnotherRequestIs409IdempotencyKeyConflictAndWritesNothing()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 10);
        await Earn(h, h.P2, 5);
        await Redeem(h, h.P1, new { points = 3, note = "Film", requestId = KeyA });
        var before = await State(h);

        var conflicts = new[]
        {
            await Redeem(h, h.P1, new { points = 4, note = "Film", requestId = KeyA }),
            await Redeem(h, h.P1, new { points = 3, note = "Andere", requestId = KeyA }),
            await Redeem(h, h.P1, new { points = 3, requestId = KeyA }),
            await Redeem(h, h.P2, new { points = 3, note = "Film", requestId = KeyA }),
        };

        foreach (var conflict in conflicts)
        {
            conflict.Status.Should().Be(HttpStatusCode.Conflict, conflict.Body.ToString());
            Type(conflict.Body).Should().EndWith("idempotency_key_conflict");
        }

        (await State(h)).Should().Be(before);
        (await BalanceOf(h, h.P2)).GetProperty("points").GetInt64().Should().Be(5);
    }

    [Fact]
    public async Task Book_aReplayOfAnAcceptedRequestIsNotStuckOnTheBalanceItWouldExceedNow()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 10);

        (await Redeem(h, h.P1, new { points = 7, requestId = KeyB })).Status.Should().Be(HttpStatusCode.Created);
        (await Redeem(h, h.P1, new { points = 7, requestId = KeyB })).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Book_theSameRequestKeyArrivingTwiceAtOnceIsOneBooking()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 10);

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => Redeem(h, h.P1, new { points = 3, requestId = KeyA }), Ct)));

        results.Select(r => (int)r.Status).Order().Should().Equal(200, 201);
        (await Redemptions(h)).Should().ContainSingle();
        (await h.PointsAuditAsync("create")).Should().ContainSingle();
    }

    // ---- the shape of the request

    [Theory]
    [InlineData("""{"points":0}""", "points")]
    [InlineData("""{"points":-2}""", "points")]
    [InlineData("""{"points":1.5}""", "points")]
    [InlineData("""{"points":"2"}""", "points")]
    [InlineData("""{"points":null}""", "points")]
    [InlineData("""{}""", "points")]
    [InlineData("""{"points":1,"note":"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"}""", "note")]
    [InlineData("""{"points":1,"note":5}""", "note")]
    [InlineData("""{"points":1,"note":null}""", "note")]
    [InlineData("""{"points":1,"requestId":"kort"}""", "requestId")]
    [InlineData("""{"points":1,"requestId":7}""", "requestId")]
    [InlineData("""{"points":1,"personId":"nope"}""", "personId")]
    [InlineData("""{"points":1,"personId":3}""", "personId")]
    [InlineData("""{"points":""", "body")]
    [InlineData("""[1]""", "body")]
    public async Task Book_aMalformedRequestIsA400OnItsFieldAndWritesNothing(string json, string field)
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 5);
        var before = await State(h);

        var (status, body) = await h.SendAsync(HttpMethod.Post, Url, h.P1, json);

        status.Should().Be(HttpStatusCode.BadRequest, body.ToString());
        Type(body).Should().EndWith("validation_error");
        body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().Contain(field);
        (await State(h)).Should().Be(before);
    }

    [Fact]
    public async Task Book_unknownFieldsAreIgnored()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 5);

        (await h.SendAsync(HttpMethod.Post, Url, h.P1, """{"points":1,"extra":true}""")).Status.Should().Be(HttpStatusCode.Created);
    }

    // ---- undo

    private static async Task<string> BookedAsync(PointsHarness h, ObjectId actor, int points = 3, object? extra = null)
    {
        var (status, body) = await Redeem(h, actor, extra ?? new { points, note = "Taart" });
        status.Should().Be(HttpStatusCode.Created, body.ToString());
        return body.GetProperty("id").GetString()!;
    }

    private static Task<(HttpStatusCode Status, JsonElement Body)> Undo(PointsHarness h, ObjectId? actor, string id) => h.SendAsync(HttpMethod.Delete, $"{Url}/{id}", actor);

    [Fact]
    public async Task Undo_theOwnerUndoesOnTheSameDayTheEntryGoesOneDeleteIsAuditedThatKeepsTheFieldsAndTheBalanceReturns()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P2, 10);
        var id = await BookedAsync(h, h.P2);
        (await BalanceOf(h, h.P2)).GetProperty("points").GetInt64().Should().Be(7);

        var (status, body) = await Undo(h, h.P2, id);

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        body.GetProperty("deleted").GetBoolean().Should().BeTrue();
        (await Redemptions(h)).Should().BeEmpty();
        var audit = (await h.PointsAuditAsync("delete")).Should().ContainSingle().Subject;
        audit["meta"].Should().Be(new BsonDocument("reason", "redemption_undone"));
        audit["actorId"].Should().Be(h.P2);
        audit["entityId"].ToString().Should().Be(id);
        audit["after"].Should().Be(new BsonDocument());
        var before = audit["before"].AsBsonDocument;
        (before["kind"].AsString, before["amount"].AsInt32, before["note"].AsString).Should().Be(("redemption", -3, "Taart"));
        before.Contains("requestId").Should().BeFalse();
        var balance = await BalanceOf(h, h.P2);
        (balance.GetProperty("points").GetInt64(), balance.GetProperty("redeemed").GetInt64()).Should().Be((10, 0));
    }

    [Fact]
    public async Task Undo_theOwnerIsLockedOnALaterDayWith403RedemptionLockedAndAnAdministratorMayUndoAtAnyTime()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P2, 10);
        var id = await BookedAsync(h, h.P2);
        h.Clock.Set("2026-09-17T00:30:00.000Z"); // 02:30 on Thursday in Amsterdam
        var before = await State(h);

        var locked = await Undo(h, h.P2, id);

        locked.Status.Should().Be(HttpStatusCode.Forbidden);
        Type(locked.Body).Should().EndWith("redemption_locked");
        (await State(h)).Should().Be(before);
        h.Clock.Set("2027-01-05T09:00:00.000Z");
        (await Undo(h, h.Admin, id)).Status.Should().Be(HttpStatusCode.OK);
        (await Redemptions(h)).Should().BeEmpty();
    }

    [Fact]
    public async Task Undo_theOwnerKeepsTheSameDayUntilLocalMidnightInTheHouseholdTimezone()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P2, 10);
        var id = await BookedAsync(h, h.P2);
        h.Clock.Set("2026-09-16T21:59:00.000Z"); // 23:59 on Wednesday in Amsterdam

        (await Undo(h, h.P2, id)).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Undo_anotherMemberIs403PermissionDeniedAndSomeoneWithoutAProfileIs400()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 10);
        var id = await BookedAsync(h, h.P1);
        var before = await State(h);

        var other = await Undo(h, h.P2, id);
        var anonymous = await Undo(h, null, id);

        other.Status.Should().Be(HttpStatusCode.Forbidden);
        Type(other.Body).Should().EndWith("permission_denied");
        anonymous.Status.Should().Be(HttpStatusCode.BadRequest);
        Type(anonymous.Body).Should().EndWith("profile_required");
        (await State(h)).Should().Be(before);
    }

    [Fact]
    public async Task Undo_anUnknownIdAndADerivedEntryAre404AndAnIdThatIsNoIdIs400()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var execution = await h.InsertExecutionEntryAsync(h.P1, "2026-09-16", 10);
        var before = await State(h);

        (await Undo(h, h.Admin, ObjectId.GenerateNewId().ToString())).Status.Should().Be(HttpStatusCode.NotFound);
        var derived = await Undo(h, h.Admin, execution.ToString());
        var bad = await Undo(h, h.Admin, "nope");

        derived.Status.Should().Be(HttpStatusCode.NotFound);
        bad.Status.Should().Be(HttpStatusCode.BadRequest);
        (await State(h)).Should().Be(before);
        (await h.EntriesAsync("execution")).Should().ContainSingle("a derived entry is never removed by an undo");
    }

    [Fact]
    public async Task Undo_thePersonARedemptionWasBookedForMayUndoItEvenWhenAnAdministratorBookedIt()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P2, 10);
        var id = await BookedAsync(h, h.Admin, extra: new { personId = h.P2.ToString(), points = 2 });

        (await Undo(h, h.P2, id)).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Undo_aSecondUndoIs404AndWritesNothing()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 10);
        var id = await BookedAsync(h, h.P1);
        (await Undo(h, h.P1, id)).Status.Should().Be(HttpStatusCode.OK);
        var before = await State(h);

        (await Undo(h, h.P1, id)).Status.Should().Be(HttpStatusCode.NotFound);

        (await State(h)).Should().Be(before);
    }

    [Fact]
    public async Task Undo_theSameRequestKeyBooksAgainAfterTheUndo()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 10);
        var id = await BookedAsync(h, h.P1, extra: new { points = 3, requestId = KeyA });
        await Undo(h, h.P1, id);

        (await Redeem(h, h.P1, new { points = 3, requestId = KeyA })).Status.Should().Be(HttpStatusCode.Created);
    }

    // ---- the count

    [Fact]
    public async Task Count_countsTheRedemptionsWithoutAProfile()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await Earn(h, h.P1, 10);

        (await h.GetAsync($"{Url}/count")).Body.GetProperty("count").GetInt64().Should().Be(0);
        await Redeem(h, h.P1, new { points = 2 });
        await Redeem(h, h.P1, new { points = 3 });
        var (status, body) = await h.GetAsync($"{Url}/count");

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("count").GetInt64().Should().Be(2);
    }

    // ---- the reconciliation

    [Fact]
    public async Task Reconcile_keepsTheRedemptionsWhenTheLedgerIsRecomputedAlsoWhenTheEarnedPointsAreGone()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var occurrence = await h.InsertDoneOccurrenceAsync("2026-09-16", null, h.P1, h.P1, snapshot: 10);
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-16", 10, "Stofzuigen", occurrence);
        (await Redeem(h, h.P1, new { points = 4, note = "Pizza" })).Status.Should().Be(HttpStatusCode.Created);
        var before = await Redemptions(h);
        var auditBefore = await h.AuditCountAsync();

        var recompute = await h.SendAsync(HttpMethod.Post, "/api/v2/points/recompute", h.Admin);

        recompute.Status.Should().Be(HttpStatusCode.OK);
        (recompute.Body.GetProperty("created").GetInt32(), recompute.Body.GetProperty("updated").GetInt32(), recompute.Body.GetProperty("removed").GetInt32()).Should().Be((0, 0, 0));
        (await h.AuditCountAsync()).Should().Be(auditBefore, "a recompute that changes nothing audits nothing");
        (await Redemptions(h)).Should().Equal(before);

        // Drift repair removes an execution entry without an occurrence, never the redemption.
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-16", 2, "Verdwaald");
        (await h.SendAsync(HttpMethod.Post, "/api/v2/points/recompute", h.Admin)).Body.GetProperty("removed").GetInt32().Should().Be(1);
        (await Redemptions(h)).Should().Equal(before);

        // Undoing the work leaves the redemption standing: the balance is negative, nothing is rewritten.
        await h.Occurrences.DeleteOneAsync(new BsonDocument("_id", occurrence), Ct);
        (await h.SendAsync(HttpMethod.Post, "/api/v2/points/recompute", h.Admin)).Body.GetProperty("removed").GetInt32().Should().Be(1);
        (await Redemptions(h)).Should().Equal(before);
        var balance = await BalanceOf(h, h.P1);
        (balance.GetProperty("points").GetInt64(), balance.GetProperty("earned").GetInt64(), balance.GetProperty("redeemed").GetInt64()).Should().Be((-4, 0, 4));
    }

    [Fact]
    public async Task Reconcile_aRestartOfTheHostWithARedemptionPresentWritesAndAuditsNothing()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var occurrence = await h.InsertDoneOccurrenceAsync("2026-09-16", null, h.P1, h.P1, snapshot: 10);
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-16", 10, "Stofzuigen", occurrence);
        await Redeem(h, h.P1, new { points = 4 });
        var before = await State(h);

        using var restarted = h.Restart();
        (await restarted.GetAsync("/api/v2/points/redemptions/count", Ct)).EnsureSuccessStatusCode();

        (await State(h)).Should().Be(before);
    }
}
