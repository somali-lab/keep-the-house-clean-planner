using System.ComponentModel;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Planning;
using Huishoudplanner.Domain.Ports.Driving;

namespace Huishoudplanner.Adapters.Http.CyclePlans;

// The request records below only document the bodies in OpenAPI; the bodies are read by CyclePlanRequestParser (Zod-style
// validation_error for a wrong type, null, malformed JSON or an empty body), never bound to these types.

/// <summary>Creates an inactive plan: empty, or with the slots and week themes of <c>copyFromId</c>.</summary>
public sealed record CreateCyclePlanRequest(
    [property: Description("Not empty after trimming.")] string? Name,
    [property: Description("The id of the plan to copy; 404 not_found when it does not exist.")] string? CopyFromId);

/// <summary>Changes a plan. Every member is optional; a member that is left out stays as it is.</summary>
public sealed record UpdateCyclePlanRequest(
    string? Name,
    [property: Description("Exactly four strings, one per week.")] IReadOnlyList<string>? WeekThemes);

/// <summary>The slots of a plan or of an unsaved draft. A save replaces all slots of the plan with these.</summary>
public sealed record PlanSlotsRequest(IReadOnlyList<PlanSlotBody>? Slots);

/// <summary>A placement in a request: <c>weekIndex</c> 0 to 3, <c>weekday</c> 0 (Sunday) to 6 (Saturday), no <c>assigneeId</c> (null) means anyone, <c>sortOrder</c> defaults to 0.</summary>
public sealed record PlanSlotBody(string? TaskId, int? WeekIndex, int? Weekday, string? AssigneeId, int? SortOrder);

/// <summary>A placement of a task in a plan.</summary>
public sealed record PlanSlotResponse(string TaskId, int WeekIndex, int Weekday, string? AssigneeId, int SortOrder)
{
    internal static PlanSlotResponse From(CyclePlanSlot slot) => new(slot.TaskId, slot.WeekIndex, slot.Weekday, slot.AssigneeId, slot.SortOrder);
}

/// <summary>A cycle plan as the API shows it. Exactly one plan is active; the oldest plan is the default plan.</summary>
public sealed record CyclePlanResponse(
    string Id,
    string Name,
    bool Active,
    IReadOnlyList<PlanSlotResponse> Slots,
    IReadOnlyList<string> WeekThemes,
    bool Draft,
    string Source,
    string? ProposalId,
    IReadOnlyList<string>? Rationale,
    bool Discarded,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static CyclePlanResponse From(CyclePlan plan) => new(
        plan.Id,
        plan.Name,
        plan.Active,
        [.. plan.Slots.Select(PlanSlotResponse.From)],
        plan.WeekThemes,
        plan.Draft,
        plan.Source,
        plan.ProposalId,
        plan.Rationale,
        plan.Discarded,
        plan.CreatedAt,
        plan.UpdatedAt);
}

/// <summary>One page of plans, oldest first; <see cref="NextCursor"/> is <c>null</c> on the last page.</summary>
public sealed record CyclePlanListResponse(IReadOnlyList<CyclePlanResponse> Items, string? NextCursor);

/// <summary>The answer to a delete.</summary>
public sealed record CyclePlanDeletedResponse(bool Deleted);

/// <summary>
/// A hard error or a warning of the plan validation. Only the members that belong to its <c>code</c> are set (requirements 4.3):
/// slot errors carry <c>slotIndex</c>, <c>taskId</c>, <c>weekIndex</c>, <c>weekday</c> (and <c>userId</c> for people); <c>interval_mismatch</c>
/// carries <c>taskId</c>, <c>placed</c>, <c>required</c>; budget warnings carry <c>userId</c>, <c>weekIndex</c>, <c>minutes</c>, <c>budget</c>
/// and <c>period</c> (weekday or weekend) or <c>weekday</c>.
/// </summary>
public sealed record PlanIssueResponse(
    string Code,
    int? SlotIndex,
    string? TaskId,
    string? UserId,
    int? WeekIndex,
    int? Weekday,
    string? Period,
    int? Placed,
    int? Required,
    int? Minutes,
    int? Budget)
{
    internal static PlanIssueResponse From(PlanIssue issue) => new(
        issue.Code,
        issue.SlotIndex,
        issue.TaskId,
        issue.UserId,
        issue.WeekIndex,
        issue.Weekday,
        issue.Period is { } period ? (period == BudgetPeriod.Weekday ? "weekday" : "weekend") : null,
        issue.Placed,
        issue.Required,
        issue.Minutes,
        issue.Budget);
}

/// <summary>The slots placed of a task against the slots its interval asks for (<c>required</c> is null when the grid does not plan the interval).</summary>
public sealed record TaskSummaryResponse(string TaskId, int Placed, int? Required);

public sealed record UserMinutesResponse(string UserId, int Minutes);

public sealed record UserDayMinutesResponse(string UserId, int Minutes, int Budget, bool OverBudget);

/// <summary>One day of the plan; <c>unassignedMinutes</c> is the work of slots without assignee, counted against no budget.</summary>
public sealed record DaySummaryResponse(int WeekIndex, int Weekday, IReadOnlyList<UserDayMinutesResponse> Users, int UnassignedMinutes);

public sealed record WeekSummaryResponse(int WeekIndex, IReadOnlyList<UserMinutesResponse> Users, int UnassignedMinutes)
{
    internal static WeekSummaryResponse From(WeekSummary week) =>
        new(week.WeekIndex, [.. week.Users.Select(u => new UserMinutesResponse(u.UserId, u.Minutes))], week.UnassignedMinutes);
}

/// <summary>The workload of a plan: slots per task, minutes per person per day and per week.</summary>
public sealed record PlanSummaryResponse(
    IReadOnlyList<TaskSummaryResponse> Tasks,
    IReadOnlyList<DaySummaryResponse> Days,
    IReadOnlyList<WeekSummaryResponse> Weeks)
{
    internal static PlanSummaryResponse From(PlanSummary summary) => new(
        [.. summary.Tasks.Select(t => new TaskSummaryResponse(t.TaskId, t.Placed, t.Required))],
        [.. summary.Days.Select(d => new DaySummaryResponse(
            d.WeekIndex,
            d.Weekday,
            [.. d.Users.Select(u => new UserDayMinutesResponse(u.UserId, u.Minutes, u.Budget, u.OverBudget))],
            d.UnassignedMinutes))],
        [.. summary.Weeks.Select(WeekSummaryResponse.From)]);
}

/// <summary>
/// What a save of the slots would answer: whether the plan can be saved, the hard <c>issues</c> (a plan with issues is refused with
/// 422 invalid_plan), the same issues keyed by field path in <c>errors</c> (for example <c>slots[3].assigneeId</c>, each message an
/// error code), the non-blocking warnings and the summary.
/// </summary>
public sealed record PlanValidationResponse(
    bool Valid,
    IReadOnlyDictionary<string, string[]> Errors,
    IReadOnlyList<PlanIssueResponse> Issues,
    IReadOnlyList<PlanIssueResponse> Warnings,
    PlanSummaryResponse Summary)
{
    internal static PlanValidationResponse From(PlanValidation validation) => new(
        validation.IsValid,
        validation.ToValidationErrors().Errors,
        [.. validation.Errors.Select(PlanIssueResponse.From)],
        [.. validation.Warnings.Select(PlanIssueResponse.From)],
        PlanSummaryResponse.From(validation.Summary));
}

/// <summary>What generating one cycle did: occurrences inserted, and already there (<c>skipped</c>, idempotency). <c>planId</c> is null when no plan is active.</summary>
public sealed record GenerationResultResponse(int CycleIndex, string CycleId, string? PlanId, int Inserted, int Skipped)
{
    internal static GenerationResultResponse From(GenerationResult result) => new(result.CycleIndex, result.CycleId, result.PlanId, result.Inserted, result.Skipped);
}

/// <summary>
/// The replacement of the upcoming occurrences that saving the slots of the active plan carries: how many open generated occurrences were
/// removed (the replaced and created ones are audited with the system as source and the saving profile as actor) and what was generated again.
/// </summary>
public sealed record SynchronizedResponse(int Removed, IReadOnlyList<GenerationResultResponse> Generated)
{
    internal static SynchronizedResponse From(ReplacementResult result) => new(result.Removed, [.. result.Generated.Select(GenerationResultResponse.From)]);
}

/// <summary>A saved slot list: the plan, the non-blocking warnings, the summary, and <c>synchronized</c> (null unless the plan is the active one).</summary>
public sealed record PlanSlotsSavedResponse(CyclePlanResponse Plan, IReadOnlyList<PlanIssueResponse> Warnings, PlanSummaryResponse Summary, SynchronizedResponse? Synchronized)
{
    internal static PlanSlotsSavedResponse From(PlanSlotsSaved saved) => new(
        CyclePlanResponse.From(saved.Plan),
        [.. saved.Warnings.Select(PlanIssueResponse.From)],
        PlanSummaryResponse.From(saved.Summary),
        saved.Synchronized is { } synchronized ? SynchronizedResponse.From(synchronized) : null);
}

public sealed record DiffPositionResponse(int WeekIndex, int Weekday, string? AssigneeId)
{
    internal static DiffPositionResponse From(DiffPosition position) => new(position.WeekIndex, position.Weekday, position.AssigneeId);
}

/// <summary>A slot that exists in only one of the two plans.</summary>
public sealed record DiffSlotResponse(string TaskId, string TaskName, string? RoomName, int DurationMinutes, int WeekIndex, int Weekday, string? AssigneeId)
{
    internal static DiffSlotResponse From(DiffSlot slot) => new(
        slot.TaskId, slot.TaskName, slot.RoomName, slot.DurationMinutes, slot.Position.WeekIndex, slot.Position.Weekday, slot.Position.AssigneeId);
}

/// <summary>A slot of the same task that sits elsewhere, or with another person, in the other plan.</summary>
public sealed record MovedSlotResponse(string TaskId, string TaskName, string? RoomName, int DurationMinutes, DiffPositionResponse From, DiffPositionResponse To)
{
    internal static MovedSlotResponse Of(MovedSlot slot) => new(
        slot.TaskId, slot.TaskName, slot.RoomName, slot.DurationMinutes, DiffPositionResponse.From(slot.From), DiffPositionResponse.From(slot.To));
}

/// <summary>The weekly minutes per person of the active plan (<c>before</c>) and of the compared plan (<c>after</c>).</summary>
public sealed record PlanDiffSummaryResponse(IReadOnlyList<WeekSummaryResponse> Before, IReadOnlyList<WeekSummaryResponse> After);

/// <summary>A plan compared with the active plan. <c>againstPlanId</c> is null when no plan is active (everything is then added).</summary>
public sealed record PlanDiffResponse(
    string PlanId,
    string? AgainstPlanId,
    IReadOnlyList<DiffSlotResponse> Added,
    IReadOnlyList<DiffSlotResponse> Removed,
    IReadOnlyList<MovedSlotResponse> Moved,
    int Unchanged,
    PlanDiffSummaryResponse Summary,
    IReadOnlyList<PlanIssueResponse> Warnings)
{
    internal static PlanDiffResponse From(PlanComparison comparison) => new(
        comparison.PlanId,
        comparison.AgainstPlanId,
        [.. comparison.Diff.Added.Select(DiffSlotResponse.From)],
        [.. comparison.Diff.Removed.Select(DiffSlotResponse.From)],
        [.. comparison.Diff.Moved.Select(MovedSlotResponse.Of)],
        comparison.Diff.Unchanged,
        new PlanDiffSummaryResponse(
            [.. comparison.Before.Select(WeekSummaryResponse.From)],
            [.. comparison.After.Select(WeekSummaryResponse.From)]),
        [.. comparison.Warnings.Select(PlanIssueResponse.From)]);
}
