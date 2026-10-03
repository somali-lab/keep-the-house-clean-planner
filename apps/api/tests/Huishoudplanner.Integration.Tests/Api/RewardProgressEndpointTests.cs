using System.Net;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// <c>points-progress.test.ts</c> on <c>GET /api/v2/points/progress</c>, real HTTP pipeline and real MongoDB replica set, one database per test: how far
/// one person is towards the goal of the current week or cycle, plus the <c>eggs</c> and <c>eggCount</c> of the reward meter (plan 4.3). The state is
/// arranged through the occurrence and settings endpoints. Deferred: the badge side effects (slice 4.5), booking the redemption through its endpoint
/// (slice 4.3; here the redemption is a ledger entry) and the <c>rewardGoals</c> settings scenarios (covered by the settings endpoint tests).
/// </summary>
public sealed class RewardProgressEndpointTests(MongoContainerFixture mongo)
{
    private static readonly object Complete = new { };

    private static (long Earned, int? Goal, int Percent) Of(JsonElement progress) => (
        progress.GetProperty("earnedPoints").GetInt64(),
        progress.GetProperty("goalPoints") is { ValueKind: JsonValueKind.Number } goal ? goal.GetInt32() : null,
        progress.GetProperty("percent").GetInt32());

    private static string Str(JsonElement progress, string name) => progress.GetProperty(name).GetString()!;

    [Fact]
    public async Task UsesThePointsPlannedForThePersonAsTheGoal_perWeekAndPerCycle_andCountsWhatTheyEarned()
    {
        await using var h = await ProgressHarness.StartAsync(mongo);

        // Week 14 to 20 September: person 1 has Stofzuigen (3) and Dweilen (1); the cycle adds Dweilen of week 2 (1).
        var week = await h.ProgressAsync(h.P1.Id, "week");
        week.GetRawText().Should().Be(
            $$"""{"personId":"{{h.P1.Id}}","period":"week","start":"2026-09-14","end":"2026-09-20","earnedPoints":0,"goalPoints":4,"goalSource":"automatic","percent":0,"eggs":0,"eggCount":10,"currencyCode":"EUR","centsPerPoint":0,"money":null}""");
        var cycle = await h.ProgressAsync(h.P1.Id, "cycle");
        (Str(cycle, "period"), Str(cycle, "start"), Str(cycle, "end"), Of(cycle)).Should().Be(("cycle", "2026-09-14", "2026-10-11", (0, 5, 0)));
        Of(await h.ProgressAsync(h.P2.Id, "week")).Goal.Should().Be(3);
        Of(await h.ProgressAsync(h.P2.Id, "cycle")).Goal.Should().Be(3);

        await h.ActAsync(await h.OccurrenceAsync("2026-09-14"), "complete", Complete);
        var done = await h.ProgressAsync(h.P1.Id, "week");
        (Of(done), done.GetProperty("eggs").GetInt32()).Should().Be(((3, 4, 75), 7));
        Of(await h.ProgressAsync(h.P1.Id, "cycle")).Should().Be((3, 5, 60));
        Of(await h.ProgressAsync(h.P2.Id, "week")).Should().Be((0, 3, 0));

        await h.ActAsync(await h.OccurrenceAsync("2026-09-17", "Dweilen"), "complete", Complete);
        var full = await h.ProgressAsync(h.P1.Id, "week");
        (Of(full), full.GetProperty("eggs").GetInt32()).Should().Be(((4, 4, 100), 10));
    }

    [Fact]
    public async Task SaysThereIsNoGoalWhenNothingIsPlannedForThePerson_andLeavesOutSkippedWork()
    {
        await using var h = await ProgressHarness.StartAsync(mongo);

        var week = await h.ProgressAsync(h.Guest, "week");
        (Of(week), Str(week, "goalSource"), week.GetProperty("money").ValueKind).Should().Be(((0, null, 0), "automatic", JsonValueKind.Null));
        Of(await h.ProgressAsync(h.Guest, "cycle")).Should().Be((0, null, 0));

        // Skipped work cannot be earned, so it leaves the goal: Dweilen of Thursday goes, Stofzuigen stays.
        await h.ActAsync(await h.OccurrenceAsync("2026-09-17", "Dweilen"), "skip", new { reason = "Geen tijd" });
        var after = await h.ProgressAsync(h.P1.Id, "week");
        (Of(after).Goal, Str(after, "goalSource")).Should().Be((3, "automatic"));
    }

    [Fact]
    public async Task FollowsTheOwnerOfTheWork_aTakeOverInsideTheWeekMovesIt()
    {
        await using var h = await ProgressHarness.StartAsync(mongo);

        // Inside the planned week the actor becomes the assignee and so the owner (ADR-0012).
        await h.ActAsync(await h.OccurrenceAsync("2026-09-15"), "complete", new { takeOver = true });

        Of(await h.ProgressAsync(h.P1.Id, "week")).Should().Be((3, 7, 42));
        Of(await h.ProgressAsync(h.P2.Id, "week")).Should().Be((0, null, 0));
    }

    [Fact]
    public async Task TheOwnerOfAFinishedWeekKeepsTheWork_whenSomebodyElseTakesItOver()
    {
        await using var h = await ProgressHarness.StartAsync(mongo);
        var tuesday = await h.OccurrenceAsync("2026-09-15");

        await h.ActAsync(tuesday, "complete", Complete, h.P2, "2026-09-15T08:00:00.000Z");
        await h.ActAsync(tuesday, "uncomplete", null, h.P2, "2026-09-22T08:00:00.000Z");
        await h.ActAsync(tuesday, "complete", new { takeOver = true }, h.P1, "2026-09-23T08:00:00.000Z");

        // The cycle still holds the work for person 2 (its owner); person 1 did it and earned its points, but it is not in their goal (3 + 1 + 1).
        Of(await h.ProgressAsync(h.P2.Id, "cycle")).Should().Be((0, 3, 0));
        Of(await h.ProgressAsync(h.P1.Id, "cycle")).Should().Be((3, 5, 60));
    }

    [Fact]
    public async Task KeepsWorkThatSomebodyElseDidInTheGoalOfItsOwner_andCreditsThePersonWhoDidIt()
    {
        await using var h = await ProgressHarness.StartAsync(mongo);

        await h.ActAsync(await h.OccurrenceAsync("2026-09-15"), "complete", new { completedBy = h.P1.Id });

        Of(await h.ProgressAsync(h.P2.Id, "week")).Should().Be((0, 3, 0));
        Of(await h.ProgressAsync(h.P1.Id, "week")).Should().Be((3, 4, 75));
    }

    [Fact]
    public async Task TakesTheExplicitGoalOver_switchesItOffWithZero_andKeepsTheOtherPeriodAutomatic()
    {
        await using var h = await ProgressHarness.StartAsync(mongo);
        await h.ActAsync(await h.OccurrenceAsync("2026-09-14"), "complete", Complete);

        await h.PatchSettingsAsync("""{ "rewardGoals": { "weekPoints": 6, "cyclePoints": null } }""");
        var week = await h.ProgressAsync(h.P1.Id, "week");
        (Of(week), Str(week, "goalSource")).Should().Be(((3, 6, 50), "explicit"));
        var cycle = await h.ProgressAsync(h.P1.Id, "cycle");
        (Of(cycle), Str(cycle, "goalSource")).Should().Be(((3, 5, 60), "automatic"));
        // An explicit goal also applies to a person nothing is planned for.
        var guest = await h.ProgressAsync(h.Guest, "week");
        (Of(guest), Str(guest, "goalSource")).Should().Be(((0, 6, 0), "explicit"));

        // A goal of 0 switches the meter off for the period.
        await h.PatchSettingsAsync("""{ "rewardGoals": { "weekPoints": 0, "cyclePoints": 2 } }""");
        var off = await h.ProgressAsync(h.P1.Id, "week");
        (Of(off), Str(off, "goalSource")).Should().Be(((3, null, 0), "explicit"));
        // More earned than the goal is capped at 100%, while the points stay exact.
        var capped = await h.ProgressAsync(h.P1.Id, "cycle");
        (Of(capped), Str(capped, "goalSource"), capped.GetProperty("eggs").GetInt32()).Should().Be(((3, 2, 100), "explicit", 10));
    }

    [Fact]
    public async Task ShowsMoneyWhenAPointIsWorthSomething_whatIsEarnedAndWhatTheGoalIsWorth()
    {
        await using var h = await ProgressHarness.StartAsync(mongo);
        await h.PatchSettingsAsync("""{ "currencyCode": "USD", "centsPerPoint": 25 }""");
        await h.ActAsync(await h.OccurrenceAsync("2026-09-14"), "complete", Complete);

        var week = await h.ProgressAsync(h.P1.Id, "week");

        (Of(week), Str(week, "currencyCode"), week.GetProperty("centsPerPoint").GetInt32()).Should().Be(((3, 4, 75), "USD", 25));
        var money = week.GetProperty("money");
        (money.GetProperty("earned").GetInt64(), money.GetProperty("goal").GetInt64()).Should().Be((75, 100));
        var guest = (await h.ProgressAsync(h.Guest, "week")).GetProperty("money");
        (guest.GetProperty("earned").GetInt64(), guest.GetProperty("goal").ValueKind).Should().Be((0, JsonValueKind.Null));
    }

    [Fact]
    public async Task CountsTheBonusesOfEndedWeeksInTheCycle_andCapsThePercentage()
    {
        await using var h = await ProgressHarness.StartAsync(mongo);
        await h.PatchSettingsAsync("""{ "periodBonuses": { "weekDone": 5, "weekOnTime": 0, "cycleDone": 0, "cycleOnTime": 0 } }""");
        await h.ActAsync(await h.OccurrenceAsync("2026-09-14"), "complete", Complete, null, "2026-09-14T07:00:00.000Z");
        await h.ActAsync(await h.OccurrenceAsync("2026-09-17", "Dweilen"), "complete", Complete, null, "2026-09-17T07:00:00.000Z");

        // While the week runs nothing is paid.
        Of(await h.ProgressAsync(h.P1.Id, "cycle")).Earned.Should().Be(4);

        // Monday 21 September, 03:00 local time: week 1 is finalised, and its bonus is dated on Sunday 20 September.
        await h.ReconcileAtAsync("2026-09-21T01:00:00.000Z");
        // The new week has earned nothing yet; the bonus belongs to the week that ended and to the cycle.
        var week = await h.ProgressAsync(h.P1.Id, "week");
        (Str(week, "start"), Str(week, "end"), Of(week)).Should().Be(("2026-09-21", "2026-09-27", (0, 1, 0)));
        // 3 + 1 + 5 bonus points against a goal of 5.
        Of(await h.ProgressAsync(h.P1.Id, "cycle")).Should().Be((9, 5, 100));
    }

    [Fact]
    public async Task DoesNotLetARedemptionLowerTheProgress()
    {
        await using var h = await ProgressHarness.StartAsync(mongo);
        await h.ActAsync(await h.OccurrenceAsync("2026-09-14"), "complete", Complete);
        await h.RedeemAsync(h.P1.Id, "2026-09-16", 2);

        Of(await h.ProgressAsync(h.P1.Id, "week")).Should().Be((3, 4, 75));
        var balances = await h.SendAsync(HttpMethod.Get, "/api/v2/points/balances", null, null);
        balances.Body.GetProperty("balances").EnumerateArray().First(b => b.GetProperty("personId").GetString() == h.P1.Id).GetProperty("points").GetInt64().Should().Be(1);
    }

    [Fact]
    public async Task PutsTheWeekAndTheCycleOnLocalMidnightAcrossTheEndOfDaylightSavingTime()
    {
        await using var h = await ProgressHarness.StartAsync(mongo);
        // Thursday 22 October 2026; clocks go back on Sunday 25 October, so the week of 19 to 25 October has 169 hours.
        h.Clock.Set("2026-10-22T08:00:00.000Z");
        await h.EarnAsync(h.P2.Id, "2026-10-18", 100); // the Sunday before: another week
        await h.EarnAsync(h.P2.Id, "2026-10-19", 1); // Monday, local midnight 22:00Z the day before
        await h.EarnAsync(h.P2.Id, "2026-10-25", 2); // Sunday, the last day of the week
        await h.EarnAsync(h.P2.Id, "2026-10-26", 4); // the next Monday, local midnight 23:00Z the day before: the next week

        var week = await h.ProgressAsync(h.P2.Id, "week");
        (Str(week, "start"), Str(week, "end"), Of(week).Earned).Should().Be(("2026-10-19", "2026-10-25", 3));
        var cycle = await h.ProgressAsync(h.P2.Id, "cycle");
        (Str(cycle, "start"), Str(cycle, "end"), Of(cycle).Earned).Should().Be(("2026-10-12", "2026-11-08", 107));
    }

    [Fact]
    public async Task ReadsThePeriodOfTodayInTheHouseholdTimezone_alsoAroundLocalMidnight()
    {
        await using var h = await ProgressHarness.StartAsync(mongo);

        // Sunday 20 September 22:30Z is already Monday 21 September 00:30 in Amsterdam: a new week.
        h.Clock.Set("2026-09-20T22:30:00.000Z");
        var next = await h.ProgressAsync(h.P1.Id, "week");
        (Str(next, "start"), Str(next, "end")).Should().Be(("2026-09-21", "2026-09-27"));
        h.Clock.Set("2026-09-20T21:30:00.000Z");
        var current = await h.ProgressAsync(h.P1.Id, "week");
        (Str(current, "start"), Str(current, "end")).Should().Be(("2026-09-14", "2026-09-20"));
    }

    [Theory]
    [InlineData("", new[] { "personId", "period" })]
    [InlineData("?period=week", new[] { "personId" })]
    [InlineData("?personId={guest}", new[] { "period" })]
    [InlineData("?personId={guest}&period=month", new[] { "period" })]
    [InlineData("?personId={guest}&period=Week", new[] { "period" })]
    [InlineData("?personId=nope&period=week", new[] { "personId" })]
    public async Task ValidatesThePersonAndThePeriod_withFieldKeyedErrors(string query, string[] fields)
    {
        await using var h = await ProgressHarness.StartAsync(mongo);

        var response = await h.SendAsync(HttpMethod.Get, "/api/v2/points/progress" + query.Replace("{guest}", h.Guest, StringComparison.Ordinal), null, null);

        response.Status.Should().Be(HttpStatusCode.BadRequest, response.Body.ToString());
        response.Body.GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:validation_error");
        response.Body.GetProperty("errors").EnumerateObject().Select(e => e.Name).Should().BeEquivalentTo(fields);
    }

    [Fact]
    public async Task NeedsNoProfile()
    {
        await using var h = await ProgressHarness.StartAsync(mongo);

        var response = await h.SendAsync(HttpMethod.Get, $"/api/v2/points/progress?personId={h.Guest}&period=cycle", null, null);

        response.Status.Should().Be(HttpStatusCode.OK, response.Body.ToString());
    }

    [Fact]
    public async Task WritesAndAuditsNothing()
    {
        await using var h = await ProgressHarness.StartAsync(mongo);
        await h.ActAsync(await h.OccurrenceAsync("2026-09-14"), "complete", Complete);
        var before = await h.FingerprintAsync();

        await h.ProgressAsync(h.P1.Id, "cycle");
        await h.ProgressAsync(h.P1.Id, "week");

        (await h.FingerprintAsync()).Should().Be(before);
    }
}
