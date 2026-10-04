using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// <c>APP_FAKE_NOW</c> (only allowed in the test environment) fixes the clock of the whole host, which is what the Playwright
/// journeys rely on to run on a known day (docs/plans/dotnet-rewrite.md section 7, slice 7.6).
/// </summary>
public sealed class FakeClockTests(MongoContainerFixture mongo)
{
    private const string Now = "2026-09-16T10:00:00+02:00";

    [Fact]
    public async Task TheHostClock_standsStillAtTheConfiguredInstant()
    {
        using var factory = ApiFactory.ForMongo(mongo).WithSetting("ASPNETCORE_ENVIRONMENT", "test").WithSetting("APP_FAKE_NOW", Now);
        using var client = factory.CreateClient();

        var clock = factory.Services.GetRequiredService<TimeProvider>();
        var first = clock.GetUtcNow();
        await Task.Delay(20, TestContext.Current.CancellationToken);

        first.Should().Be(DateTimeOffset.Parse(Now, System.Globalization.CultureInfo.InvariantCulture));
        clock.GetUtcNow().Should().Be(first);
    }

    [Fact]
    public async Task TheFirstStart_seedsTheCycleAnchorOnTheMondayOfTheFakeDay()
    {
        using var factory = ApiFactory.ForMongo(mongo).WithSetting("ASPNETCORE_ENVIRONMENT", "test").WithSetting("APP_FAKE_NOW", Now);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v2/settings", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("cycleAnchorDate").GetString().Should().Be("2026-09-14");
    }

    [Fact]
    public void WithoutAFakeNow_theHostUsesTheSystemClock()
    {
        using var factory = ApiFactory.ForMongo(mongo);
        using var client = factory.CreateClient();

        factory.Services.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
    }
}
