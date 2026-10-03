using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Domain.Bonuses;

/// <summary>The four derived ledger kinds that join <c>execution</c> (ADR-0012).</summary>
public enum BonusKind
{
    WeekDone,
    WeekOnTime,
    CycleDone,
    CycleOnTime,
}

/// <summary>Ledger names and amounts of the bonus kinds (TS <c>BONUS_KINDS</c>, <c>isBonusKind</c>, <c>AMOUNT_OF_KIND</c>).</summary>
public static class BonusKinds
{
    public static IReadOnlyList<BonusKind> All { get; } =
        [BonusKind.WeekDone, BonusKind.WeekOnTime, BonusKind.CycleDone, BonusKind.CycleOnTime];

    /// <summary>The ledger kind as stored and published: <c>bonus_week_done</c>, <c>bonus_week_ontime</c>, <c>bonus_cycle_done</c>, <c>bonus_cycle_ontime</c>.</summary>
    public static string ToLedgerKind(this BonusKind kind) => kind switch
    {
        BonusKind.WeekDone => "bonus_week_done",
        BonusKind.WeekOnTime => "bonus_week_ontime",
        BonusKind.CycleDone => "bonus_cycle_done",
        BonusKind.CycleOnTime => "bonus_cycle_ontime",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown bonus kind."),
    };

    /// <summary>TS <c>isBonusKind</c>: an exact, case-sensitive match of a ledger kind name.</summary>
    public static bool TryParse(string? ledgerKind, out BonusKind kind)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.ToLedgerKind(), ledgerKind, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }

    public static bool IsBonusKind(string? ledgerKind) => TryParse(ledgerKind, out _);

    /// <summary>The amount of one kind in a set of amounts.</summary>
    public static int AmountOf(this BonusAmounts amounts, BonusKind kind)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        return kind switch
        {
            BonusKind.WeekDone => amounts.WeekDone,
            BonusKind.WeekOnTime => amounts.WeekOnTime,
            BonusKind.CycleDone => amounts.CycleDone,
            BonusKind.CycleOnTime => amounts.CycleOnTime,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown bonus kind."),
        };
    }

    public static bool IsOnTimeKind(this BonusKind kind) => kind is BonusKind.WeekOnTime or BonusKind.CycleOnTime;
}
