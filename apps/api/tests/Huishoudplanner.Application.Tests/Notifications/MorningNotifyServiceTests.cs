using Huishoudplanner.Application.Notifications;
using Huishoudplanner.Application.Tests.Occurrences;
using Huishoudplanner.Application.Tests.Users;
using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Notifications;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.Logging.Abstractions;
using OneOf;

namespace Huishoudplanner.Application.Tests.Notifications;

/// <summary>
/// The morning message use case against fakes (the <c>morning notification job</c> cases of notify.test.ts): one message per active person, a
/// person with nothing to report is skipped, delivery trouble is counted and a read failure is reported as an error, never thrown.
/// </summary>
public sealed class MorningNotifyServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeNotifier : ForSendingNotifications
    {
        public bool IsEnabled { get; set; } = true;

        public List<NotifyMessage> Sent { get; } = [];

        public Func<NotifyMessage, OneOf<Success, PortError>>? Outcome { get; set; }

        public Task<OneOf<Success, PortError>> SendAsync(NotifyMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.FromResult(Outcome?.Invoke(message) ?? new Success());
        }
    }

    private sealed class FakeDue(OneOf<DueSummary, SettingsMissing, PortError> summary) : IDueService
    {
        public Task<OneOf<DueList, ValidationErrors, SettingsMissing, PortError>> GetDueAsync(int? limit, string? cursor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OneOf<DueSummary, SettingsMissing, PortError>> GetSummaryAsync(CancellationToken cancellationToken) => Task.FromResult(summary);
    }

    /// <summary>Wednesday 16 September 2026: one task for p1 today, one for anyone today, one for p2 tomorrow, two overdue tasks.</summary>
    private sealed class World
    {
        public OccurrenceWorld Core { get; } = new();

        public FakeNotifier Notifier { get; } = new();

        public OneOf<DueSummary, SettingsMissing, PortError> Due { get; set; } = new DueSummary(3, 2);

        public World()
        {
            Core.Seed(Core.Weekly, "2026-09-16", Core.P1);
            Core.Seed(Core.Twice, "2026-09-16");
            Core.Seed(Core.Weekly, "2026-09-17", Core.P2);
            Core.People.Add("Inactief", active: false);
        }

        public MorningNotifyService Service => new(Notifier, new FakeUserStore(Core.People), Core.Occurrences, Core.SettingsStore, new FakeDue(Due), Core.Clock, NullLogger<MorningNotifyService>.Instance);
    }

    [Fact]
    public async Task One_message_goes_to_every_active_person_with_their_counts_and_the_household_overdue_number()
    {
        var w = new World();

        var result = await w.Service.RunAsync(Ct);

        result.Should().Be(new MorningResult(MorningStatus.Done, new DateOnly(2026, 9, 16), 3, 0, 0));
        w.Notifier.Sent.Should().HaveCount(3);
        var first = w.Notifier.Sent.Single(m => (string)m.Data["userId"]! == w.Core.P1.Id);
        first.Title.Should().Be("Keep the House Clean");
        first.Body.Should().StartWith("Goedemorgen Persoon 1! Vandaag staan er 1 taak voor je klaar en 1 taak voor wie dan ook.");
        first.Data.Should().Contain(new Dictionary<string, object?>
        {
            ["kind"] = "morning", ["date"] = "2026-09-16", ["userName"] = "Persoon 1", ["openToday"] = 1, ["openTodayAnyone"] = 1, ["overdue"] = 2,
        });
        var second = w.Notifier.Sent.Single(m => (string)m.Data["userId"]! == w.Core.P2.Id);
        second.Data.Should().Contain(new Dictionary<string, object?> { ["openToday"] = 0, ["openTodayAnyone"] = 1, ["overdue"] = 2 });
        w.Notifier.Sent.Select(m => (string)m.Data["userId"]!).Should().NotContain(w.Core.People.Users.Single(u => !u.Active).Id);
    }

    [Fact]
    public async Task Nothing_is_sent_and_nothing_is_read_when_notifications_are_off()
    {
        var w = new World();
        w.Notifier.IsEnabled = false;

        var result = await w.Service.RunAsync(Ct);

        result.Should().Be(new MorningResult(MorningStatus.Disabled, null, 0, 0, 0));
        w.Notifier.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_person_with_nothing_to_report_is_skipped_and_counted_as_quiet()
    {
        var w = new World();
        w.Core.Occurrences.Items.Clear();
        w.Due = new DueSummary(0, 0);

        var result = await w.Service.RunAsync(Ct);

        result.Should().Be(new MorningResult(MorningStatus.Done, new DateOnly(2026, 9, 16), 0, 0, 3));
        w.Notifier.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Occurrences_that_are_not_open_or_not_today_are_not_counted()
    {
        var w = new World();
        w.Core.Occurrences.Items.Clear();
        w.Core.Seed(w.Core.Weekly, "2026-09-16", w.Core.P1, OccurrenceStatus.Done);
        w.Core.Seed(w.Core.Weekly, "2026-09-15", w.Core.P1);
        w.Due = new DueSummary(0, 0);

        var result = await w.Service.RunAsync(Ct);

        result.Sent.Should().Be(0);
        result.Quiet.Should().Be(3);
    }

    [Fact]
    public async Task A_refused_delivery_is_counted_per_person_and_the_run_still_ends_done()
    {
        var w = new World();
        w.Notifier.Outcome = _ => new PortError("ntfy answered 500");

        var result = await w.Service.RunAsync(Ct);

        result.Should().Be(new MorningResult(MorningStatus.Done, new DateOnly(2026, 9, 16), 0, 3, 0));
        w.Notifier.Sent.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_failing_read_is_reported_as_error_without_sending_and_without_throwing()
    {
        var w = new World();
        w.Due = new PortError("db down");

        var result = await w.Service.RunAsync(Ct);

        result.Should().Be(new MorningResult(MorningStatus.Error, null, 0, 0, 0));
        w.Notifier.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_settings_are_reported_as_error()
    {
        var w = new World();
        w.Core.SettingsStore.Document = null;

        var result = await w.Service.RunAsync(Ct);

        result.Status.Should().Be(MorningStatus.Error);
        w.Notifier.Sent.Should().BeEmpty();
    }
}
