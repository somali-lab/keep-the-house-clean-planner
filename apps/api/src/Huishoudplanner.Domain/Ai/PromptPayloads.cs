using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Huishoudplanner.Domain.Ai;

/// <summary>The two modes of a plan request; the wire value is the user message's <c>mode</c>.</summary>
public static class PlanModes
{
    public const string Propose = "propose";

    public const string Rebalance = "rebalance";
}

/// <summary>A task as the model sees it. <c>PerCycle</c> is the slots per 28-day cycle; null means optional (not planned through the grid).</summary>
public sealed record PromptTask(
    string Id,
    string Name,
    string? Room,
    string IntervalKey,
    string IntervalLabel,
    int? PerCycle,
    int PeriodDays,
    int DurationMinutes);

/// <summary>Minutes on Monday to Friday together (<c>Weekday</c>) or on the weekend (<c>Weekend</c>).</summary>
public sealed record PromptMinutes(int Weekday, int Weekend);

/// <summary>A person as the model sees it. <c>UnavailableWeekdays</c> are 0=Sunday..6=Saturday.</summary>
public sealed record PromptUser(
    string Id,
    string Name,
    IReadOnlyList<int> UnavailableWeekdays,
    PromptMinutes DailyBudgetMinutes,
    PromptMinutes MaxDailyMinutes);

public sealed record PromptSlot(string TaskId, int WeekIndex, int Weekday, string? AssigneeId);

/// <summary>
/// The user message of a plan request: exactly this object as JSON, so it can be parsed back (for example by the mock).
/// <c>PreviousErrors</c> is present on the single re-prompt only: why the previous answer was rejected.
/// </summary>
public sealed record PlanPromptPayload(
    string Mode,
    IReadOnlyList<PromptTask> Tasks,
    IReadOnlyList<PromptUser> Users,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PromptSlot>? CurrentSlots = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Constraints = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? PreviousErrors = null);

public sealed record ExistingTaskInfo(string Name, string IntervalKey, int DurationMinutes);

public sealed record OtherTaskInfo(string? Room, string Name);

public sealed record PromptInterval(string Key, string Label, int PeriodDays);

/// <summary>The user message of a task suggestion request.</summary>
public sealed record TaskSuggestionPayload(
    string Room,
    IReadOnlyList<ExistingTaskInfo> ExistingTasks,
    IReadOnlyList<OtherTaskInfo> OtherTasks,
    IReadOnlyList<PromptInterval> Intervals);

public sealed record ExplanationUser(string Id, string Name, PromptMinutes DailyBudgetMinutes, PromptMinutes MaxDailyMinutes);

public sealed record ExplanationSlot(string Task, int WeekIndex, int Weekday, string? Assignee, int DurationMinutes);

/// <summary>The user message of a plan explanation request.</summary>
public sealed record ExplanationPayload(string PlanName, IReadOnlyList<ExplanationUser> Users, IReadOnlyList<ExplanationSlot> Slots);

/// <summary>JSON the way the Node server writes it (<c>JSON.stringify</c>): camelCase, compact, non-ASCII characters left as they are.</summary>
public static class PromptJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
