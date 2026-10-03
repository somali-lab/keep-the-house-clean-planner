using System.ComponentModel;
using System.Globalization;
using Huishoudplanner.Domain.Points;

namespace Huishoudplanner.Adapters.Http.Points;

/// <summary>The money of a balance in whole cents at the factor in force now; present only while a point is worth money.</summary>
public sealed record BalanceMoneyResponse(long Earned, long Redeemed, long Balance);

/// <summary>The balance of one person over the range.</summary>
public sealed record PersonBalanceResponse(
    string PersonId,
    [property: Description("The balance: the sum of all entries in the range, so earned minus redeemed. Negative when work that was redeemed against is undone.")] long Points,
    [property: Description("The points of executions and bonuses in the range.")] long Earned,
    [property: Description("The points redeemed in the range, as a positive number.")] long Redeemed,
    [property: Description("Earned, redeemed and the balance in whole cents; null while a point is worth nothing.")] BalanceMoneyResponse? Money,
    [property: Description("The number of entries of kind execution in the range.")] int Executions,
    [property: Description("The sum of the week and cycle bonus entries in the range; part of points.")] long BonusPoints);

/// <summary>The balances of the household over a range.</summary>
public sealed record PointsBalancesResponse(
    [property: Description("The first day of the range, YYYY-MM-DD; null when absent.")] string? From,
    [property: Description("The last day of the range, YYYY-MM-DD; null when absent.")] string? To,
    [property: Description("The ISO 4217 code of the household currency.")] string CurrencyCode,
    [property: Description("The cents one point is worth now; 0 means no money is shown.")] int CentsPerPoint,
    [property: Description("Every active person, also at 0, and every inactive person with entries in the range, in the order of the user list.")] IReadOnlyList<PersonBalanceResponse> Balances)
{
    internal static PointsBalancesResponse FromDomain(PointsBalances balances) => new(
        balances.From is { } from ? Day(from) : null,
        balances.To is { } to ? Day(to) : null,
        balances.CurrencyCode,
        balances.CentsPerPoint,
        [.. balances.Balances.Select(b => new PersonBalanceResponse(
            b.PersonId,
            b.Points,
            b.Earned,
            b.Redeemed,
            b.Money is { } money ? new BalanceMoneyResponse(money.Earned, money.Redeemed, money.Balance) : null,
            b.Executions,
            b.BonusPoints))]);

    internal static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>One entry of the points ledger: the points of an execution, a bonus or a redemption.</summary>
public sealed record PointEntryResponse(
    string Id,
    [property: Description("Unique: execution:<occurrenceId>, <kind>:<personId>:<periodStart> or redemption:<id>.")] string Key,
    [property: Description("execution, bonus_week_done, bonus_week_ontime, bonus_cycle_done, bonus_cycle_ontime or redemption.")] string Kind,
    string PersonId,
    [property: Description("Signed: an execution or a bonus is at least 1, a redemption at most -1.")] int Amount,
    [property: Description("The day of the execution (a bonus: the last day of its period; a redemption: the day it was booked), YYYY-MM-DD.")] string Date,
    [property: Description("The Monday of that week, YYYY-MM-DD.")] string WeekStart,
    [property: Description("The first day of the week or cycle of a bonus, YYYY-MM-DD; null for an execution.")] string? PeriodStart,
    string? OccurrenceId,
    [property: Description("Null for a one-off task, and for a bonus or a redemption.")] string? TaskId,
    [property: Description("The task name of the occurrence, so the entry stays readable; empty for a bonus or a redemption.")] string TitleSnapshot,
    [property: Description("Free text of a redemption; null otherwise.")] string? Note,
    [property: Description("The cents one point was worth when a redemption was booked; null otherwise.")] int? CentsPerPointSnapshot,
    [property: Description("The household currency when a redemption was booked; null otherwise.")] string? CurrencyCodeSnapshot,
    [property: Description("The path that wrote the current value: live, backfill or recompute.")] string Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static PointEntryResponse From(PointEntryView view)
    {
        var e = view.Entry;
        return new PointEntryResponse(
            e.Id,
            e.Key,
            PointNames.ToWire(e.Kind),
            e.PersonId,
            e.Amount,
            PointsBalancesResponse.Day(view.Date),
            PointsBalancesResponse.Day(view.WeekStart),
            view.PeriodStart is { } period ? PointsBalancesResponse.Day(period) : null,
            e.OccurrenceId,
            e.TaskId,
            e.TitleSnapshot,
            e.Note,
            e.CentsPerPointSnapshot,
            e.CurrencyCodeSnapshot,
            PointNames.ToWire(e.Source),
            e.CreatedAt,
            e.UpdatedAt);
    }
}

/// <summary>One page of the entries of a person, newest date first and then by id; <see cref="NextCursor"/> is null on the last page.</summary>
public sealed record PointEntriesResponse(IReadOnlyList<PointEntryResponse> Items, string? NextCursor);

/// <summary>The person and the points an entry held or holds.</summary>
public sealed record PointsHoldingResponse(string PersonId, int Amount);

/// <summary>An execution entry that was changed or removed because it had drifted from its occurrence.</summary>
public sealed record PointsCorrectionResponse(
    string Key,
    PointsHoldingResponse From,
    [property: Description("Null when the entry was removed.")] PointsHoldingResponse? To);

/// <summary>A week or cycle bonus that was created or removed.</summary>
public sealed record PointsBonusChangeResponse(string Key, string PersonId, int Amount, [property: Description("created or removed.")] string Change);

/// <summary>What one reconciliation of the ledger did.</summary>
public sealed record PointsRecomputeResponse(
    [property: Description("What started the run: admin for this endpoint.")] string Trigger,
    [property: Description("The tasks that gained the default points for their duration.")] int TasksDefaulted,
    [property: Description("The done occurrences that gained a points snapshot.")] int SnapshotsSet,
    int Created,
    int Updated,
    int Removed,
    [property: Description("Done occurrences with points but nobody to credit; they earn no entry.")] int Unattributed,
    [property: Description("Occurrences that could not be read and were left as they are.")] int Skipped,
    [property: Description("The entries that were changed or removed, at most 100.")] IReadOnlyList<PointsCorrectionResponse> Corrections,
    int CorrectionsTotal,
    bool CorrectionsTruncated,
    int BonusesCreated,
    int BonusesRemoved,
    [property: Description("The bonus entries that were created or removed, at most 100.")] IReadOnlyList<PointsBonusChangeResponse> BonusChanges,
    int BonusChangesTotal,
    bool BonusChangesTruncated)
{
    internal static PointsRecomputeResponse From(PointsRecomputeResult result) => new(
        PointNames.ToWire(result.Trigger),
        result.TasksDefaulted,
        result.SnapshotsSet,
        result.Created,
        result.Updated,
        result.Removed,
        result.Unattributed,
        result.Skipped,
        [.. result.Corrections.Select(c => new PointsCorrectionResponse(
            c.Key,
            new PointsHoldingResponse(c.From.PersonId, c.From.Amount),
            c.To is { } to ? new PointsHoldingResponse(to.PersonId, to.Amount) : null))],
        result.CorrectionsTotal,
        result.CorrectionsTruncated,
        result.BonusesCreated,
        result.BonusesRemoved,
        [.. result.BonusChanges.Select(b => new PointsBonusChangeResponse(b.Key, b.PersonId, b.Amount, b.Change))],
        result.BonusChangesTotal,
        result.BonusChangesTruncated);
}
