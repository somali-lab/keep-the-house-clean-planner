using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Due;

namespace Huishoudplanner.Domain.Settings;

/// <summary>What a missing optional value means, and the settings of a fresh installation (apps/server/src/domain/seed.ts).</summary>
public static class SettingsDefaults
{
    public const string CurrencyCode = "EUR";

    public const int CentsPerPoint = 0;

    public const int PromoteThreshold = 2;

    /// <summary>The shipped interval that older installations get added (before <c>2w</c>).</summary>
    public const string ThreePerWeekKey = "3w";

    private const string ThreePerWeekBefore = "2w";

    /// <summary>The household instructions of <c>DEFAULT_AI_PROMPTS</c> (Dutch, as shipped).</summary>
    public static AiPrompts AiPrompts { get; } = new(
        PlanProposal: "Maak een praktisch vierwekenplan voor alle taken. Verdeel de minuten zo eerlijk mogelijk, houd rekening met beschikbaarheid en daglimieten en spreid herhalingen logisch.",
        PlanRebalance: "Herverdeel het actieve plan alleen waar dat de balans, spreiding of ingestelde limieten verbetert. Behoud bestaande dagen en uitvoerders wanneer wijzigen geen duidelijke verbetering geeft.",
        TaskSuggestions: "Stel alleen nuttige huishoudelijke taken voor die voor deze ruimte nog ontbreken. Gebruik korte, concrete Nederlandse taaknamen en realistische tijdsduren.",
        PlanExplanation: "Leg het plan in eenvoudig Nederlands uit. Benoem per week de verdeling, opvallend drukke momenten en waarom de planning redelijk verdeeld is.");

    /// <summary>The settings written when none exist: the cycle starts on the Monday of this week.</summary>
    public static HouseholdSettings ForNewInstallation(string timezone, DateOnly today, DateTimeOffset now) => new(
        CycleAnchorDate: DayKeys.MondayOf(today),
        WeekStartsOn: 1,
        Timezone: timezone,
        VacationRanges: [],
        Intervals: DueCalculator.DefaultIntervals,
        AiProvider: new AiProviderSettings(AiProviderType.None),
        AiPrompts: AiPrompts,
        AiPromptTemplates: null,
        CompletionControl: Settings.CompletionControl.Circle,
        PromoteThreshold: PromoteThreshold,
        DismissedPromotions: [],
        BonusSchedule: null,
        BonusFloor: null,
        CurrencyCode: null,
        CentsPerPoint: null,
        RewardGoals: null,
        CreatedAt: now,
        UpdatedAt: now);

    /// <summary>
    /// The intervals with the shipped <c>3w</c> added before <c>2w</c> (at the end without <c>2w</c>) when missing; otherwise the
    /// same list (<c>addIntervalIfMissing</c>).
    /// </summary>
    public static IReadOnlyList<Interval> WithThreePerWeek(IReadOnlyList<Interval> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        if (intervals.Any(i => i.Key == ThreePerWeekKey))
        {
            return intervals;
        }

        var shipped = DueCalculator.DefaultIntervals.First(i => i.Key == ThreePerWeekKey);
        var list = intervals.ToList();
        var index = list.FindIndex(i => i.Key == ThreePerWeekBefore);
        list.Insert(index == -1 ? list.Count : index, shipped);
        return list;
    }
}
