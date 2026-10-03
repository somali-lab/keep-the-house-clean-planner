using Huishoudplanner.Domain.Due;

namespace Huishoudplanner.Domain.Settings;

/// <summary>
/// The fields <c>PATCH /settings</c> may change; a <see langword="null"/> property is not part of the patch. The timezone,
/// the week start, the schedule itself, the dismissed promotions and the bonus floor can never be set by a client.
/// </summary>
public sealed record SettingsPatch
{
    public DateOnly? CycleAnchorDate { get; init; }

    public IReadOnlyList<VacationRange>? VacationRanges { get; init; }

    public IReadOnlyList<Interval>? Intervals { get; init; }

    public AiProviderSettings? AiProvider { get; init; }

    public AiPrompts? AiPrompts { get; init; }

    public AiPromptTemplates? AiPromptTemplates { get; init; }

    public CompletionControl? CompletionControl { get; init; }

    public int? PromoteThreshold { get; init; }

    /// <summary>The amounts that apply from today on; the service turns them into a schedule row.</summary>
    public BonusAmounts? PeriodBonuses { get; init; }

    public string? CurrencyCode { get; init; }

    public int? CentsPerPoint { get; init; }

    public RewardGoals? RewardGoals { get; init; }
}

/// <summary>Removing an interval that tasks still use is refused (<c>409 interval_in_use</c>); <see cref="Keys"/> are the blocked keys.</summary>
public sealed record IntervalInUse(IReadOnlyList<string> Keys);
