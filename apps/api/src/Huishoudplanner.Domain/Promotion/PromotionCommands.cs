using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Domain.Promotion;

/// <summary>
/// Moves one slot of the active plan (identified by plan, task, week and weekday) to <see cref="ToWeekday"/>, and to <see cref="ToAssigneeId"/>
/// when that is set (otherwise the slot keeps its person). Weekdays are 0 Sunday to 6 Saturday.
/// </summary>
public sealed record ApplyPromotionCommand(string PlanId, string TaskId, int WeekIndex, int Weekday, int ToWeekday, string? ToAssigneeId);

/// <summary>The slot of a promotion is no longer in the plan. Maps to <c>404 slot_not_found</c>.</summary>
public readonly record struct SlotNotFound;

/// <summary>The value rules of the two promotion writes (the <c>applyPromotionInputSchema</c> and <c>dismissedPromotionSchema</c> of Node).</summary>
public static class PromotionRules
{
    public static ValidationErrors? Validate(ApplyPromotionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        Identity(command.PlanId, command.TaskId, command.WeekIndex, command.Weekday, command.ToWeekday, errors);
        if (command.ToAssigneeId is not null && !CyclePlanRules.IsId(command.ToAssigneeId))
        {
            errors["toAssigneeId"] = ["must be a 24 character hexadecimal id"];
        }

        return errors.Count > 0 ? new ValidationErrors(errors) : null;
    }

    public static ValidationErrors? Validate(DismissedPromotion promotion)
    {
        ArgumentNullException.ThrowIfNull(promotion);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        Identity(promotion.PlanId, promotion.TaskId, promotion.WeekIndex, promotion.Weekday, promotion.ToWeekday, errors);
        if (promotion.ToAssigneeId is not null && !CyclePlanRules.IsId(promotion.ToAssigneeId))
        {
            errors["toAssigneeId"] = ["must be a 24 character hexadecimal id or null"];
        }

        if (!CyclePlanRules.IsId(promotion.LastEvidenceId))
        {
            errors["lastEvidenceId"] = ["must be a 24 character hexadecimal id"];
        }

        return errors.Count > 0 ? new ValidationErrors(errors) : null;
    }

    /// <summary>Ids are stored in lower case.</summary>
    public static ApplyPromotionCommand Normalise(ApplyPromotionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command with { PlanId = command.PlanId.ToLowerInvariant(), TaskId = command.TaskId.ToLowerInvariant(), ToAssigneeId = command.ToAssigneeId?.ToLowerInvariant() };
    }

    public static DismissedPromotion Normalise(DismissedPromotion promotion)
    {
        ArgumentNullException.ThrowIfNull(promotion);
        return promotion with
        {
            PlanId = promotion.PlanId.ToLowerInvariant(),
            TaskId = promotion.TaskId.ToLowerInvariant(),
            ToAssigneeId = promotion.ToAssigneeId?.ToLowerInvariant(),
            LastEvidenceId = promotion.LastEvidenceId.ToLowerInvariant(),
        };
    }

    /// <summary>A dismissal for the same slot and target replaces the earlier one, so only the newest evidence counts (<c>addDismissedPromotion</c>).</summary>
    public static IReadOnlyList<DismissedPromotion> Dismiss(IReadOnlyList<DismissedPromotion> current, DismissedPromotion entry)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(entry);
        return [.. current.Where(d => !SameTarget(d, entry)), entry];
    }

    private static bool SameTarget(DismissedPromotion a, DismissedPromotion b) =>
        a.PlanId == b.PlanId && a.TaskId == b.TaskId && a.WeekIndex == b.WeekIndex && a.Weekday == b.Weekday
        && a.ToWeekday == b.ToWeekday && a.ToAssigneeId == b.ToAssigneeId;

    private static void Identity(string planId, string taskId, int weekIndex, int weekday, int toWeekday, Dictionary<string, string[]> errors)
    {
        if (!CyclePlanRules.IsId(planId))
        {
            errors["planId"] = ["must be a 24 character hexadecimal id"];
        }

        if (!CyclePlanRules.IsId(taskId))
        {
            errors["taskId"] = ["must be a 24 character hexadecimal id"];
        }

        if (weekIndex is < 0 or > 3)
        {
            errors["weekIndex"] = ["must be 0 to 3"];
        }

        if (weekday is < 0 or > 6)
        {
            errors["weekday"] = ["must be 0 (Sunday) to 6 (Saturday)"];
        }

        if (toWeekday is < 0 or > 6)
        {
            errors["toWeekday"] = ["must be 0 (Sunday) to 6 (Saturday)"];
        }
    }
}
