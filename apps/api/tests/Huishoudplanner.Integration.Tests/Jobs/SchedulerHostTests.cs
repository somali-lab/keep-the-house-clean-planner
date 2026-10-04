using Huishoudplanner.Adapters.Jobs;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Huishoudplanner.Integration.Tests.Jobs;

/// <summary>
/// The scheduler inside the real host (no database needed: the jobs never fire here). <c>DISABLE_SCHEDULER=true</c> leaves it without a job,
/// <c>false</c> schedules generation at 03:00 and retention at 03:45 in <c>TZ_APP</c>. The test host disables the scheduler by default.
/// </summary>
public sealed class SchedulerHostTests
{
    private static readonly DateTimeOffset Night = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero); // 02:00 in Amsterdam

    private static JobScheduler SchedulerOf(ApiFactory factory)
    {
        _ = factory.CreateClient();
        return factory.Services.GetRequiredService<JobScheduler>();
    }

    [Fact]
    public void The_test_host_and_DISABLE_SCHEDULER_true_schedule_no_job()
    {
        using var byDefault = ApiFactory.WithoutDatabase();
        using var explicitly = ApiFactory.WithoutDatabase().WithSetting("DISABLE_SCHEDULER", "true");

        SchedulerOf(byDefault).JobNames.Should().BeEmpty();
        SchedulerOf(explicitly).JobNames.Should().BeEmpty();
    }

    [Fact]
    public void An_enabled_scheduler_without_a_channel_has_both_nightly_jobs_at_their_times_in_the_household_timezone_and_one_hosted_service_for_all_of_them()
    {
        using var factory = ApiFactory.WithoutDatabase()
            .WithSetting("DISABLE_SCHEDULER", "false")
            .WithSetting("TZ_APP", "Europe/Amsterdam")
            .WithPort<TimeProvider>(new FakeTimeProvider(Night));

        var scheduler = SchedulerOf(factory);

        scheduler.JobNames.Should().Equal("nightly-generation", "audit-retention");
        scheduler.NextRunOf("nightly-generation").Should().Be(new DateTimeOffset(2026, 9, 14, 1, 0, 0, TimeSpan.Zero));
        scheduler.NextRunOf("audit-retention").Should().Be(new DateTimeOffset(2026, 9, 14, 1, 45, 0, TimeSpan.Zero));
        factory.Services.GetServices<IHostedService>().OfType<JobSchedulerService>().Should().ContainSingle();
    }

    [Fact]
    public void The_morning_message_is_only_scheduled_with_a_notification_channel_at_0730_household_time()
    {
        using var ntfy = ApiFactory.WithoutDatabase()
            .WithSetting("DISABLE_SCHEDULER", "false")
            .WithSetting("TZ_APP", "Europe/Amsterdam")
            .WithSetting("NOTIFY_TYPE", "ntfy")
            .WithSetting("NOTIFY_URL", "https://ntfy.example/huis")
            .WithPort<TimeProvider>(new FakeTimeProvider(Night));
        using var homeAssistant = ApiFactory.WithoutDatabase()
            .WithSetting("DISABLE_SCHEDULER", "false")
            .WithSetting("NOTIFY_TYPE", "homeassistant")
            .WithSetting("NOTIFY_URL", "http://ha.local/api/webhook/huis");
        using var none = ApiFactory.WithoutDatabase().WithSetting("DISABLE_SCHEDULER", "false").WithSetting("NOTIFY_TYPE", "none");

        var withNtfy = SchedulerOf(ntfy);

        withNtfy.JobNames.Should().Equal("nightly-generation", "audit-retention", "morning-notify");
        withNtfy.NextRunOf("morning-notify").Should().Be(new DateTimeOffset(2026, 9, 14, 5, 30, 0, TimeSpan.Zero));
        SchedulerOf(homeAssistant).JobNames.Should().Contain("morning-notify");
        SchedulerOf(none).JobNames.Should().NotContain("morning-notify");
    }

    [Fact]
    public void Another_household_timezone_moves_the_jobs()
    {
        using var factory = ApiFactory.WithoutDatabase()
            .WithSetting("DISABLE_SCHEDULER", "false")
            .WithSetting("TZ_APP", "UTC")
            .WithPort<TimeProvider>(new FakeTimeProvider(Night));

        SchedulerOf(factory).NextRunOf("nightly-generation").Should().Be(new DateTimeOffset(2026, 9, 14, 3, 0, 0, TimeSpan.Zero));
    }
}
