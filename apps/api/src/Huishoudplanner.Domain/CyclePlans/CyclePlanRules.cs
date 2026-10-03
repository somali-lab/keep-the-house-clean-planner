using Huishoudplanner.Domain.Errors;

namespace Huishoudplanner.Domain.CyclePlans;

/// <summary>The rules of plan names, week themes, ids and the default plan (port of the schemas of <c>cyclePlans.ts</c> and the route guards).</summary>
public static class CyclePlanRules
{
    public const string DefaultPlanName = "Standaard";

    public const string DefaultPlanCode = "default_plan";

    public const string ActivePlanCode = "active_plan";

    public const int WeekThemeCount = 4;

    public static IReadOnlyList<string> EmptyWeekThemes { get; } = ["", "", "", ""];

    public static bool IsId(string? id) =>
        id is { Length: 24 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    public static ValidationErrors? Validate(CreateCyclePlanCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        CheckName(command.Name, errors);
        if (command.CopyFromId is not null && !IsId(command.CopyFromId))
        {
            errors["copyFromId"] = ["invalid_object_id"];
        }

        return errors.Count == 0 ? null : new ValidationErrors(errors);
    }

    public static ValidationErrors? Validate(CyclePlanPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (patch.Name is not null)
        {
            CheckName(patch.Name, errors);
        }

        if (patch.WeekThemes is { Count: not WeekThemeCount })
        {
            errors["weekThemes"] = ["must hold exactly four week themes"];
        }

        return errors.Count == 0 ? null : new ValidationErrors(errors);
    }

    /// <summary>
    /// Field-keyed problems of the slots of a save or a draft: the id shape and the position ranges (week 0 to 3, day 0 to 6), keyed
    /// <c>slots[3].weekIndex</c>. Like the Node schema, which refuses these before the plan rules run.
    /// </summary>
    public static ValidationErrors? Validate(IReadOnlyList<CyclePlanSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        for (var index = 0; index < slots.Count; index++)
        {
            var slot = slots[index];
            if (!IsId(slot.TaskId))
            {
                errors[$"slots[{index}].taskId"] = ["invalid_object_id"];
            }

            if (slot.AssigneeId is not null && !IsId(slot.AssigneeId))
            {
                errors[$"slots[{index}].assigneeId"] = ["invalid_object_id"];
            }

            if (slot.WeekIndex is < 0 or > 3)
            {
                errors[$"slots[{index}].weekIndex"] = ["must be a whole number from 0 to 3"];
            }

            if (slot.Weekday is < 0 or > 6)
            {
                errors[$"slots[{index}].weekday"] = ["must be a whole number from 0 to 6"];
            }
        }

        return errors.Count == 0 ? null : new ValidationErrors(errors);
    }

    /// <summary>The ids of the slots in their stored form (lowercase), which is what an <c>ObjectId</c> reads back as.</summary>
    public static IReadOnlyList<CyclePlanSlot> Normalise(IEnumerable<CyclePlanSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        return [.. slots.Select(s => s with { TaskId = s.TaskId.ToLowerInvariant(), AssigneeId = s.AssigneeId?.ToLowerInvariant() })];
    }

    /// <summary>
    /// What stops a delete: the oldest plan cannot be deleted (<c>default_plan</c>, checked first), nor can the active one
    /// (<c>active_plan</c>).
    /// </summary>
    public static ConflictError? DeleteConflict(CyclePlan plan, CyclePlan? defaultPlan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (defaultPlan is not null && defaultPlan.Id == plan.Id)
        {
            return new ConflictError(DefaultPlanCode, "The default plan cannot be deleted");
        }

        return plan.Active ? new ConflictError(ActivePlanCode, "Activate another plan before deleting this plan") : null;
    }

    private static void CheckName(string value, Dictionary<string, string[]> errors)
    {
        if (value.Trim().Length == 0)
        {
            errors["name"] = ["must not be empty"];
        }
    }
}
