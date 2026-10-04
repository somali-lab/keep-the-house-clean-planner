using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Adapters.Http.Settings;

/// <summary>A vacation range, both days included.</summary>
public sealed record VacationRangeDto(DateOnly From, DateOnly To);

/// <summary>A task interval: how often it is planned in a 28-day cycle (<c>PerCycle</c>, null when it is not planned on the grid) and how many days it lasts.</summary>
public sealed record IntervalDto(string Key, string Label, int? PerCycle, int PeriodDays);

/// <summary>
/// The AI provider choice. <c>Type</c> is <c>none</c>, <c>mock</c>, <c>anthropic</c>, <c>openai-compatible</c> or <c>ollama</c>.
/// There is no key: <c>AI_API_KEY</c> is an environment variable and is never stored or returned.
/// </summary>
public sealed record AiProviderDto(string Type, string? Endpoint, string? Model, int? TimeoutSeconds);

/// <summary>Household instructions per AI use case, each at most 8000 characters; empty means none.</summary>
public sealed record AiPromptsDto(string PlanProposal, string PlanRebalance, string TaskSuggestions, string PlanExplanation);

/// <summary>A prompt template: <c>System</c> must contain <c>{{schema}}</c> and <c>User</c> must contain <c>{{input}}</c>.</summary>
public sealed record AiPromptTemplateDto(string System, string User);

public sealed record AiPromptTemplatesDto(
    AiPromptTemplateDto PlanProposal,
    AiPromptTemplateDto PlanRebalance,
    AiPromptTemplateDto TaskSuggestions,
    AiPromptTemplateDto PlanExplanation);

/// <summary>The four bonus amounts, integers from 0 to 1000; 0 switches that kind off.</summary>
public sealed record BonusAmountsDto(int WeekDone, int WeekOnTime, int CycleDone, int CycleOnTime);

/// <summary>A row of the bonus schedule. <c>StartsInFuture</c> is true while <c>From</c> is after today in the household timezone.</summary>
public sealed record BonusScheduleRowDto(DateOnly From, int WeekDone, int WeekOnTime, int CycleDone, int CycleOnTime, bool StartsInFuture);

/// <summary>The goals of the reward meter in points: an integer from 0 to 100000 (0 means no goal) or null for the automatic goal.</summary>
public sealed record RewardGoalsDto(int? WeekPoints, int? CyclePoints);

/// <summary>A dismissed promote suggestion (read only here).</summary>
public sealed record DismissedPromotionDto(
    string PlanId,
    string TaskId,
    int WeekIndex,
    int Weekday,
    int ToWeekday,
    string? ToAssigneeId,
    string LastEvidenceId);

/// <summary>
/// The household settings. Missing optional values are delivered as their defaults (no bonuses, EUR, 0 cents per point, automatic goals).
/// <c>BonusesInForce</c> are the amounts in force today.
/// </summary>
public sealed record SettingsResponse(
    string Id,
    DateOnly CycleAnchorDate,
    int WeekStartsOn,
    string Timezone,
    IReadOnlyList<VacationRangeDto> VacationRanges,
    IReadOnlyList<IntervalDto> Intervals,
    AiProviderDto AiProvider,
    AiPromptsDto? AiPrompts,
    AiPromptTemplatesDto? AiPromptTemplates,
    string? CompletionControl,
    int PromoteThreshold,
    IReadOnlyList<DismissedPromotionDto> DismissedPromotions,
    IReadOnlyList<BonusScheduleRowDto> BonusSchedule,
    BonusAmountsDto BonusesInForce,
    DateOnly? BonusFloor,
    string CurrencyCode,
    int CentsPerPoint,
    RewardGoalsDto RewardGoals,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    [property: System.ComponentModel.Description("The concurrency version of the settings (the number inside their ETag); send the ETag as If-Match when you change the settings.")] int Version)
{
    public static SettingsResponse From(SettingsView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var settings = view.Settings;
        return new SettingsResponse(
            view.Id,
            settings.CycleAnchorDate,
            settings.WeekStartsOn,
            settings.Timezone,
            [.. settings.VacationRanges.Select(r => new VacationRangeDto(r.From, r.To))],
            [.. settings.Intervals.Select(i => new IntervalDto(i.Key, i.Label, i.PerCycle, i.PeriodDays))],
            new AiProviderDto(SettingsWire.ProviderName(settings.AiProvider.Type), settings.AiProvider.Endpoint, settings.AiProvider.Model, settings.AiProvider.TimeoutSeconds),
            settings.AiPrompts is { } p ? new AiPromptsDto(p.PlanProposal, p.PlanRebalance, p.TaskSuggestions, p.PlanExplanation) : null,
            settings.AiPromptTemplates is { } t
                ? new AiPromptTemplatesDto(Template(t.PlanProposal), Template(t.PlanRebalance), Template(t.TaskSuggestions), Template(t.PlanExplanation))
                : null,
            settings.CompletionControl is { } control ? SettingsWire.ControlName(control) : null,
            settings.PromoteThreshold,
            [.. settings.DismissedPromotions.Select(d => new DismissedPromotionDto(d.PlanId, d.TaskId, d.WeekIndex, d.Weekday, d.ToWeekday, d.ToAssigneeId, d.LastEvidenceId))],
            [.. view.BonusSchedule.Select(r => new BonusScheduleRowDto(r.From, r.Amounts.WeekDone, r.Amounts.WeekOnTime, r.Amounts.CycleDone, r.Amounts.CycleOnTime, r.StartsInFuture))],
            new BonusAmountsDto(view.BonusesInForce.WeekDone, view.BonusesInForce.WeekOnTime, view.BonusesInForce.CycleDone, view.BonusesInForce.CycleOnTime),
            settings.BonusFloor,
            view.CurrencyCode,
            view.CentsPerPoint,
            new RewardGoalsDto(view.RewardGoals.WeekPoints, view.RewardGoals.CyclePoints),
            settings.CreatedAt,
            settings.UpdatedAt,
            settings.Version);
    }

    private static AiPromptTemplateDto Template(AiPromptTemplate template) => new(template.System, template.User);
}

/// <summary>
/// The body of <c>PATCH /settings</c>: every field is optional and only the given ones change. This type documents the body for
/// OpenAPI; the endpoint reads the JSON itself so that every problem, also a wrong type or a missing required part, is a
/// <c>400 validation_error</c> with <c>errors</c> naming the field. Unknown fields are ignored. The timezone, the week start and the
/// schedule itself cannot be set.
/// </summary>
public sealed record UpdateSettingsRequest(
    DateOnly? CycleAnchorDate,
    IReadOnlyList<VacationRangeDto>? VacationRanges,
    IReadOnlyList<IntervalDto>? Intervals,
    AiProviderDto? AiProvider,
    AiPromptsDto? AiPrompts,
    AiPromptTemplatesDto? AiPromptTemplates,
    string? CompletionControl,
    int? PromoteThreshold,
    BonusAmountsDto? PeriodBonuses,
    string? CurrencyCode,
    int? CentsPerPoint,
    RewardGoalsDto? RewardGoals);

/// <summary>Wire names of the enums of settings.</summary>
internal static class SettingsWire
{
    public static string ProviderName(AiProviderType type) => SettingsAudit.ProviderName(type);

    public static string ControlName(CompletionControl control) => SettingsAudit.ControlName(control);

    public static AiProviderType? ParseProvider(string name) => name switch
    {
        "none" => AiProviderType.None,
        "mock" => AiProviderType.Mock,
        "anthropic" => AiProviderType.Anthropic,
        "openai-compatible" => AiProviderType.OpenAiCompatible,
        "ollama" => AiProviderType.Ollama,
        _ => null,
    };

    public static CompletionControl? ParseControl(string name) => name switch
    {
        "circle" => CompletionControl.Circle,
        "thumb" => CompletionControl.Thumb,
        _ => null,
    };

    public static Interval ToInterval(IntervalDto dto) => new(dto.Key, dto.Label, dto.PerCycle, dto.PeriodDays);
}
