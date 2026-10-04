using Huishoudplanner.Domain.Due;

namespace Huishoudplanner.Domain.Settings;

/// <summary>The id of the singleton settings document; also the audit <c>entityId</c> of settings (apps/server/src/data/settings.ts).</summary>
public static class SettingsIds
{
    public const string Singleton = "000000000000000000000001";
}

public sealed record VacationRange(DateOnly From, DateOnly To);

public enum AiProviderType
{
    None,
    Mock,
    Anthropic,
    OpenAiCompatible,
    Ollama,
}

/// <summary>
/// The provider choice. The API key is never part of it: it is the environment variable <c>AI_API_KEY</c> only
/// (requirements 5), so it is neither stored nor returned.
/// </summary>
public sealed record AiProviderSettings(AiProviderType Type, string? Endpoint = null, string? Model = null, int? TimeoutSeconds = null);

/// <summary>Household instructions per AI use case, each at most 8000 characters; empty means none.</summary>
public sealed record AiPrompts(string PlanProposal, string PlanRebalance, string TaskSuggestions, string PlanExplanation);

public sealed record AiPromptTemplate(string System, string User);

public sealed record AiPromptTemplates(
    AiPromptTemplate PlanProposal,
    AiPromptTemplate PlanRebalance,
    AiPromptTemplate TaskSuggestions,
    AiPromptTemplate PlanExplanation);

public enum CompletionControl
{
    Circle,
    Thumb,
}

/// <summary>A dismissed promote suggestion; written by the promote slice, carried through unchanged here.</summary>
public sealed record DismissedPromotion(
    string PlanId,
    string TaskId,
    int WeekIndex,
    int Weekday,
    int ToWeekday,
    string? ToAssigneeId,
    string LastEvidenceId);

/// <summary>Goals of the reward meter in points; <see langword="null"/> is the automatic goal, 0 means no goal (requirements 4.12).</summary>
public sealed record RewardGoals(int? WeekPoints, int? CyclePoints)
{
    public static RewardGoals Automatic { get; } = new(null, null);
}

/// <summary>
/// The settings document. A missing optional value is <see langword="null"/> here and means its default for the API
/// (<see cref="SettingsDefaults"/>): the stored document is kept as the Node server wrote it. <see cref="Version"/> is the optimistic concurrency
/// version of the document (<see cref="Concurrency.EntityVersion"/>, ADR-0022); it rises with every real change of the settings, also a dismissed
/// promote suggestion, but not with the activation guard counter, which is no part of the settings.
/// </summary>
public sealed record HouseholdSettings(
    DateOnly CycleAnchorDate,
    int WeekStartsOn,
    string Timezone,
    IReadOnlyList<VacationRange> VacationRanges,
    IReadOnlyList<Interval> Intervals,
    AiProviderSettings AiProvider,
    AiPrompts? AiPrompts,
    AiPromptTemplates? AiPromptTemplates,
    CompletionControl? CompletionControl,
    int PromoteThreshold,
    IReadOnlyList<DismissedPromotion> DismissedPromotions,
    IReadOnlyList<BonusScheduleRow>? BonusSchedule,
    DateOnly? BonusFloor,
    string? CurrencyCode,
    int? CentsPerPoint,
    RewardGoals? RewardGoals,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version = 0);
