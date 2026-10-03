using System.Globalization;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Due;

namespace Huishoudplanner.Domain.Settings;

/// <summary>
/// The settings document as an audit value tree, with the fields and value types the Node server diffs (day keys are strings,
/// an interval without a count per cycle holds an explicit null). Timestamps are left out: they are not part of a diff.
/// An optional field that is not stored is absent, so a field that is set for the first time shows up only in <c>after</c>.
/// </summary>
public static class SettingsAudit
{
    /// <param name="scheduleAsEmptyWhenMissing">A <c>bonusSchedule</c> patch is audited against the schedule in force, <c>[]</c> when there was none.</param>
    public static AuditObject ToAudit(HouseholdSettings settings, bool scheduleAsEmptyWhenMissing = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var properties = new List<KeyValuePair<string, AuditValue>>
        {
            Pair("cycleAnchorDate", Day(settings.CycleAnchorDate)),
            Pair("weekStartsOn", settings.WeekStartsOn),
            Pair("timezone", settings.Timezone),
            Pair("vacationRanges", new AuditArray([.. settings.VacationRanges.Select(r => (AuditValue)AuditObject.Of(("from", Day(r.From)), ("to", Day(r.To))))])),
            Pair("intervals", new AuditArray([.. settings.Intervals.Select(Interval)])),
            Pair("aiProvider", Provider(settings.AiProvider)),
        };
        AddIf(properties, "aiPrompts", settings.AiPrompts is { } prompts ? Prompts(prompts) : null);
        AddIf(properties, "aiPromptTemplates", settings.AiPromptTemplates is { } templates ? Templates(templates) : null);
        AddIf(properties, "completionControl", settings.CompletionControl is { } control ? (AuditValue)ControlName(control) : null);
        properties.Add(Pair("promoteThreshold", settings.PromoteThreshold));
        properties.Add(Pair("dismissedPromotions", new AuditArray([.. settings.DismissedPromotions.Select(Promotion)])));
        AddIf(
            properties,
            "bonusSchedule",
            settings.BonusSchedule is { } schedule
                ? new AuditArray([.. schedule.Select(Row)])
                : scheduleAsEmptyWhenMissing ? new AuditArray([]) : null);
        AddIf(properties, "bonusFloor", settings.BonusFloor is { } floor ? Day(floor) : null);
        AddIf(properties, "currencyCode", settings.CurrencyCode is { } currency ? (AuditValue)currency : null);
        AddIf(properties, "centsPerPoint", settings.CentsPerPoint is { } cents ? (AuditValue)cents : null);
        AddIf(properties, "rewardGoals", settings.RewardGoals is { } goals ? Goals(goals) : null);
        return new AuditObject(properties);
    }

    public static string ControlName(CompletionControl control) => control switch
    {
        CompletionControl.Circle => "circle",
        CompletionControl.Thumb => "thumb",
        _ => throw new ArgumentOutOfRangeException(nameof(control)),
    };

    public static string ProviderName(AiProviderType type) => type switch
    {
        AiProviderType.None => "none",
        AiProviderType.Mock => "mock",
        AiProviderType.Anthropic => "anthropic",
        AiProviderType.OpenAiCompatible => "openai-compatible",
        AiProviderType.Ollama => "ollama",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static KeyValuePair<string, AuditValue> Pair(string key, AuditValue value) => new(key, value);

    private static void AddIf(List<KeyValuePair<string, AuditValue>> properties, string key, AuditValue? value)
    {
        if (value is not null)
        {
            properties.Add(new(key, value));
        }
    }

    private static AuditValue Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static AuditValue Nullable(int? value) => value is { } v ? v : AuditNull.Instance;

    private static AuditObject Interval(Interval interval) => AuditObject.Of(
        ("key", interval.Key),
        ("label", interval.Label),
        ("perCycle", Nullable(interval.PerCycle)),
        ("periodDays", interval.PeriodDays));

    private static AuditObject Provider(AiProviderSettings provider)
    {
        var properties = new List<KeyValuePair<string, AuditValue>> { Pair("type", ProviderName(provider.Type)) };
        AddIf(properties, "endpoint", provider.Endpoint is { } endpoint ? (AuditValue)endpoint : null);
        AddIf(properties, "model", provider.Model is { } model ? (AuditValue)model : null);
        AddIf(properties, "timeoutSeconds", provider.TimeoutSeconds is { } timeout ? (AuditValue)timeout : null);
        return new AuditObject(properties);
    }

    private static AuditObject Prompts(AiPrompts prompts) => AuditObject.Of(
        ("planProposal", prompts.PlanProposal),
        ("planRebalance", prompts.PlanRebalance),
        ("taskSuggestions", prompts.TaskSuggestions),
        ("planExplanation", prompts.PlanExplanation));

    private static AuditObject Template(AiPromptTemplate template) => AuditObject.Of(("system", template.System), ("user", template.User));

    private static AuditObject Templates(AiPromptTemplates templates) => AuditObject.Of(
        ("planProposal", Template(templates.PlanProposal)),
        ("planRebalance", Template(templates.PlanRebalance)),
        ("taskSuggestions", Template(templates.TaskSuggestions)),
        ("planExplanation", Template(templates.PlanExplanation)));

    private static AuditObject Promotion(DismissedPromotion promotion) => AuditObject.Of(
        ("planId", new AuditObjectId(promotion.PlanId)),
        ("taskId", new AuditObjectId(promotion.TaskId)),
        ("weekIndex", promotion.WeekIndex),
        ("weekday", promotion.Weekday),
        ("toWeekday", promotion.ToWeekday),
        ("toAssigneeId", promotion.ToAssigneeId is { } id ? new AuditObjectId(id) : AuditNull.Instance),
        ("lastEvidenceId", new AuditObjectId(promotion.LastEvidenceId)));

    private static AuditObject Row(BonusScheduleRow row) => AuditObject.Of(
        ("from", Day(row.From)),
        ("weekDone", row.Amounts.WeekDone),
        ("weekOnTime", row.Amounts.WeekOnTime),
        ("cycleDone", row.Amounts.CycleDone),
        ("cycleOnTime", row.Amounts.CycleOnTime));

    private static AuditObject Goals(RewardGoals goals) => AuditObject.Of(
        ("weekPoints", Nullable(goals.WeekPoints)),
        ("cyclePoints", Nullable(goals.CyclePoints)));
}
