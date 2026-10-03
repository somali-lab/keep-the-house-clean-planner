using Huishoudplanner.Domain.Due;

namespace Huishoudplanner.Domain.Settings;

/// <summary>The fields a settings write sets; a <see langword="null"/> property is left as it is. What a client may not send (the schedule) is here for the service.</summary>
public sealed record SettingsChanges
{
    public DateOnly? CycleAnchorDate { get; init; }

    public IReadOnlyList<VacationRange>? VacationRanges { get; init; }

    public IReadOnlyList<Interval>? Intervals { get; init; }

    public AiProviderSettings? AiProvider { get; init; }

    public AiPrompts? AiPrompts { get; init; }

    public AiPromptTemplates? AiPromptTemplates { get; init; }

    public CompletionControl? CompletionControl { get; init; }

    public int? PromoteThreshold { get; init; }

    public IReadOnlyList<BonusScheduleRow>? BonusSchedule { get; init; }

    public string? CurrencyCode { get; init; }

    public int? CentsPerPoint { get; init; }

    public RewardGoals? RewardGoals { get; init; }

    /// <summary>The settings after these changes (the modification time is the store's business).</summary>
    public HouseholdSettings ApplyTo(HouseholdSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with
        {
            CycleAnchorDate = CycleAnchorDate ?? current.CycleAnchorDate,
            VacationRanges = VacationRanges ?? current.VacationRanges,
            Intervals = Intervals ?? current.Intervals,
            AiProvider = AiProvider ?? current.AiProvider,
            AiPrompts = AiPrompts ?? current.AiPrompts,
            AiPromptTemplates = AiPromptTemplates ?? current.AiPromptTemplates,
            CompletionControl = CompletionControl ?? current.CompletionControl,
            PromoteThreshold = PromoteThreshold ?? current.PromoteThreshold,
            BonusSchedule = BonusSchedule ?? current.BonusSchedule,
            CurrencyCode = CurrencyCode ?? current.CurrencyCode,
            CentsPerPoint = CentsPerPoint ?? current.CentsPerPoint,
            RewardGoals = RewardGoals ?? current.RewardGoals,
        };
    }
}
