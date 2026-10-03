using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Planning;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>
/// The cycle plans (requirements 4.3): reading, creating (empty or as a copy), renaming, deleting, saving slots, comparing with the
/// active plan and validating. Who may call what (planners write, everyone reads) is decided by the driving adapter; the
/// <see cref="Actor"/> is attribution for the audit entry. Activation is a separate use case (ADR-0008, slice 2.4).
/// </summary>
public interface ICyclePlanService
{
    /// <summary>A page of plans, oldest first. A bad <c>limit</c> or cursor is a <see cref="ValidationErrors"/>.</summary>
    Task<OneOf<CyclePlanList, ValidationErrors, PortError>> ListAsync(int? limit, string? cursor, CancellationToken cancellationToken);

    Task<OneOf<CyclePlan, NotFound, ValidationErrors, PortError>> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary><see cref="NotFound"/> when no plan is active.</summary>
    Task<OneOf<CyclePlan, NotFound, PortError>> GetActiveAsync(CancellationToken cancellationToken);

    /// <summary>Creates an inactive manual plan, empty or with the slots and week themes of <see cref="CreateCyclePlanCommand.CopyFromId"/> (<see cref="NotFound"/> when that plan does not exist).</summary>
    Task<OneOf<CyclePlan, NotFound, ValidationErrors, ConflictError, PortError>> CreateAsync(Actor actor, CreateCyclePlanCommand command, CancellationToken cancellationToken);

    /// <summary>Changes the name and/or week themes. A patch that changes nothing writes and audits nothing and returns the plan as it is.</summary>
    Task<OneOf<CyclePlan, NotFound, ValidationErrors, ConflictError, PortError>> UpdateAsync(Actor actor, string id, CyclePlanPatch patch, CancellationToken cancellationToken);

    /// <summary>Deletes a plan. The default (oldest) plan is a <c>409 default_plan</c>, the active plan a <c>409 active_plan</c> (a <see cref="ConflictError"/> with that code).</summary>
    Task<OneOf<Success, NotFound, ValidationErrors, ConflictError, PortError>> DeleteAsync(Actor actor, string id, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces all slots after the plan validation: a hard error is an <see cref="InvalidPlan"/> and nothing is written; otherwise the
    /// saved plan comes back with the warnings and the summary. Saving what is already stored writes and audits nothing.
    /// </summary>
    Task<OneOf<PlanSlotsSaved, NotFound, ValidationErrors, InvalidPlan, ConflictError, PortError, SettingsMissing>> ReplaceSlotsAsync(Actor actor, string id, IReadOnlyList<CyclePlanSlot> slots, CancellationToken cancellationToken, AuditObject? meta = null);

    /// <summary>The slot differences between the plan and the active plan, and the minutes per person per week before and after.</summary>
    Task<OneOf<PlanComparison, NotFound, ValidationErrors, PortError>> CompareWithActiveAsync(string id, CancellationToken cancellationToken);

    /// <summary>The validation of a stored plan, as a save of its slots would run it.</summary>
    Task<OneOf<PlanValidation, NotFound, ValidationErrors, PortError>> ValidateAsync(string id, CancellationToken cancellationToken);

    /// <summary>The validation of an unsaved draft, as a save of these slots would run it.</summary>
    Task<OneOf<PlanValidation, ValidationErrors, PortError>> ValidateDraftAsync(IReadOnlyList<CyclePlanSlot> slots, CancellationToken cancellationToken);
}

/// <summary>
/// A saved slot list: the plan as stored, the non-blocking warnings and the workload summary. <see cref="Synchronized"/> is the replacement of the
/// upcoming occurrences that saving the slots of the active plan always carries (requirements 4.3); <see langword="null"/> for any other plan.
/// </summary>
public sealed record PlanSlotsSaved(CyclePlan Plan, IReadOnlyList<PlanIssue> Warnings, PlanSummary Summary, ReplacementResult? Synchronized = null);

/// <summary>
/// A plan against the active one: <see cref="AgainstPlanId"/> is <see langword="null"/> when no plan is active (the base is then empty).
/// <see cref="Before"/> and <see cref="After"/> are the weekly minutes per person of the base and of the plan; the warnings are those of the plan.
/// </summary>
public sealed record PlanComparison(
    string PlanId,
    string? AgainstPlanId,
    PlanDiff Diff,
    IReadOnlyList<WeekSummary> Before,
    IReadOnlyList<WeekSummary> After,
    IReadOnlyList<PlanIssue> Warnings);
