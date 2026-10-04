using System.Globalization;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Notifications;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Users;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Application.Notifications;

/// <summary>
/// The morning notification (requirements 4.10; <c>runMorningNotify</c> of the Node server). The open occurrences of today and the active people are
/// read once, the overdue number comes from the due engine, and one message per person goes out; a person with nothing to report is skipped. Nothing is
/// written, so no audit entry. Failures never escape: a read that fails ends the run as <see cref="MorningStatus.Error"/>, a refused delivery is counted
/// and logged without its text (the port error names the notifier and the status only).
/// </summary>
public sealed partial class MorningNotifyService(
    ForSendingNotifications notifier,
    ForStoringUsers users,
    ForStoringOccurrences occurrences,
    ForStoringSettings settings,
    IDueService due,
    TimeProvider time,
    ILogger<MorningNotifyService> logger) : IMorningNotifyService
{
    private const int UserPageSize = 200;

    public async Task<MorningResult> RunAsync(CancellationToken cancellationToken)
    {
        if (!notifier.IsEnabled)
        {
            return new MorningResult(MorningStatus.Disabled, null, 0, 0, 0);
        }

        var read = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (read is null)
        {
            return new MorningResult(MorningStatus.Error, null, 0, 0, 0);
        }

        var dayKey = read.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var sent = 0;
        var failed = 0;
        var quiet = 0;
        foreach (var person in read.People)
        {
            var counts = new MorningCounts(person.Name, read.Open.Count(o => o.AssigneeId == person.Id), read.Anyone, read.Overdue);
            if (MorningMessage.Compose(counts) is not { } body)
            {
                quiet++;
                continue;
            }

            var message = new NotifyMessage(MorningMessage.Title, body, new Dictionary<string, object?>
            {
                ["kind"] = "morning",
                ["date"] = dayKey,
                ["userId"] = person.Id,
                ["userName"] = person.Name,
                ["openToday"] = counts.Mine,
                ["openTodayAnyone"] = counts.Anyone,
                ["overdue"] = counts.Overdue,
            });
            var delivery = await notifier.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (delivery.IsT0)
            {
                sent++;
            }
            else
            {
                failed++;
                LogDeliveryFailed(logger, person.Id);
            }
        }

        LogCompleted(logger, sent, failed, quiet);
        return new MorningResult(MorningStatus.Done, read.Today, sent, failed, quiet);
    }

    private sealed record Read(DateOnly Today, IReadOnlyList<User> People, IReadOnlyList<Occurrence> Open, int Anyone, int Overdue);

    /// <summary>Reads what the messages are made from; <see langword="null"/> (logged) when any read fails.</summary>
    private async Task<Read?> ReadAsync(CancellationToken ct)
    {
        var current = await settings.GetAsync(ct).ConfigureAwait(false);
        if (!current.TryPickT0(out var household, out _))
        {
            LogReadFailed(logger, "settings");
            return null;
        }

        var zone = DayKeys.FindZone(household.Timezone);
        var today = DayKeys.Today(zone, time.GetUtcNow());

        var people = new List<User>();
        UserCursor? after = null;
        do
        {
            var page = await users.ListAsync(new UserQuery(true, after, UserPageSize), ct).ConfigureAwait(false);
            if (!page.TryPickT0(out var found, out _))
            {
                LogReadFailed(logger, "users");
                return null;
            }

            people.AddRange(found.Items);
            after = found.NextCursor is { } next && UserCursor.TryParse(next, out var cursor) ? cursor : null;
        }
        while (after is not null);

        var open = new List<Occurrence>();
        var dayStart = DayKeys.FromDayKey(today, zone);
        var dayEnd = DayKeys.FromDayKey(DayKeys.AddDays(today, 1), zone);
        OccurrenceCursor? position = null;
        while (true)
        {
            var page = await occurrences.ListAsync(new OccurrenceQuery(dayStart, dayEnd, null, OccurrenceStatus.Open, position, OccurrenceListQuery.MaxLimit), ct).ConfigureAwait(false);
            if (!page.TryPickT0(out var found, out _))
            {
                LogReadFailed(logger, "occurrences");
                return null;
            }

            open.AddRange(found);
            if (found.Count < OccurrenceListQuery.MaxLimit)
            {
                break;
            }

            position = OccurrenceCursor.After(found[^1]);
        }

        var summary = await due.GetSummaryAsync(ct).ConfigureAwait(false);
        if (!summary.TryPickT0(out var counts, out _))
        {
            LogReadFailed(logger, "due");
            return null;
        }

        return new Read(today, people, open, open.Count(o => o.AssigneeId is null), counts.Overdue);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Morning notification failed: could not read the {What}")]
    private static partial void LogReadFailed(ILogger logger, string what);

    [LoggerMessage(Level = LogLevel.Error, Message = "Morning notification to user {UserId} failed")]
    private static partial void LogDeliveryFailed(ILogger logger, string userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Morning notification completed: sent {Sent}, failed {Failed}, quiet {Quiet}")]
    private static partial void LogCompleted(ILogger logger, int sent, int failed, int quiet);
}
