using Huishoudplanner.Domain.Errors;

namespace Huishoudplanner.Domain.Planning;

/// <summary>
/// Outcome of validating a plan: the hard-rule <c>Errors</c> (a plan with errors cannot be saved), the non-blocking
/// <c>Warnings</c> and the workload <c>Summary</c>.
/// </summary>
public sealed record PlanValidation(
    IReadOnlyList<PlanIssue> Errors,
    IReadOnlyList<PlanIssue> Warnings,
    PlanSummary Summary)
{
    public bool IsValid => Errors.Count == 0;

    /// <summary>
    /// The errors in the field-level shape of <see cref="ValidationErrors"/>: keyed by the field path of the offending
    /// slot value (<c>slots[3].taskId</c>), each message being the error code (a message key for the client).
    /// </summary>
    public ValidationErrors ToValidationErrors()
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var error in Errors)
        {
            var field = $"slots[{error.SlotIndex}].{FieldOf(error.Code)}";
            if (!fields.TryGetValue(field, out var messages))
            {
                fields[field] = messages = [];
            }

            messages.Add(error.Code);
        }

        return new ValidationErrors(fields.ToDictionary(f => f.Key, f => f.Value.ToArray(), StringComparer.Ordinal));
    }

    private static string FieldOf(string code) => code switch
    {
        PlanErrorCodes.WeekIndexOutOfRange => "weekIndex",
        PlanErrorCodes.WeekdayOutOfRange => "weekday",
        PlanErrorCodes.UnknownUser or PlanErrorCodes.InactiveUser or PlanErrorCodes.AssigneeUnavailable => "assigneeId",
        _ => "taskId",
    };
}
