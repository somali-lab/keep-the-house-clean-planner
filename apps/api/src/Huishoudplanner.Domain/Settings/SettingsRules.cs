using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Limits;

namespace Huishoudplanner.Domain.Settings;

/// <summary>
/// The rules of <c>updateSettingsInputSchema</c> that go beyond the shape of the body (<c>packages/shared/src/schemas/settings.ts</c>).
/// Field names are the zod paths (<c>vacationRanges.0.to</c>) and messages are the same codes; every problem is reported at once.
/// </summary>
public static class SettingsRules
{
    public const string AnchorNotMonday = "anchor_not_monday";
    public const string VacationRangeInverted = "vacation_range_inverted";
    public const string DuplicateIntervalKey = "duplicate_interval_key";
    public const string SchemaPlaceholderRequired = "schema_placeholder_required";
    public const string InputPlaceholderRequired = "input_placeholder_required";
    public const string InvalidCurrencyCode = "invalid_currency_code";
    public const string CurrencyNotTwoDecimals = "currency_not_two_decimals";

    private const string SchemaPlaceholder = "{{schema}}";
    private const string InputPlaceholder = "{{input}}";
    private const string OutOfRange = "out_of_range";
    private const string Invalid = "invalid";

    /// <summary>Returns <see langword="null"/> when the patch is valid.</summary>
    public static ValidationErrors? Validate(SettingsPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list))
            {
                errors[field] = list = [];
            }

            list.Add(message);
        }

        var limits = HouseholdLimits.Current;

        if (patch.CycleAnchorDate is { } anchor && !DayKeys.IsMonday(anchor))
        {
            Add("cycleAnchorDate", AnchorNotMonday);
        }

        if (patch.VacationRanges is { } ranges)
        {
            for (var i = 0; i < ranges.Count; i++)
            {
                if (ranges[i].From > ranges[i].To)
                {
                    Add($"vacationRanges.{i}.to", VacationRangeInverted);
                }
            }
        }

        if (patch.Intervals is { } intervals)
        {
            for (var i = 0; i < intervals.Count; i++)
            {
                var interval = intervals[i];
                if (interval.Key.Length < 1 || interval.Key.Length > limits.Tasks.IntervalKeyMaxLength)
                {
                    Add($"intervals.{i}.key", OutOfRange);
                }

                if (interval.Label.Length < 1)
                {
                    Add($"intervals.{i}.label", OutOfRange);
                }

                if (interval.PerCycle is < 1)
                {
                    Add($"intervals.{i}.perCycle", OutOfRange);
                }

                if (interval.PeriodDays < 1)
                {
                    Add($"intervals.{i}.periodDays", OutOfRange);
                }
            }

            if (intervals.Select(i => i.Key).Distinct(StringComparer.Ordinal).Count() != intervals.Count)
            {
                Add("intervals", DuplicateIntervalKey);
            }
        }

        if (patch.AiProvider is { } provider)
        {
            if (provider.Endpoint is { } endpoint && !Uri.TryCreate(endpoint, UriKind.Absolute, out _))
            {
                Add("aiProvider.endpoint", Invalid);
            }

            if (provider.Model is { Length: 0 })
            {
                Add("aiProvider.model", OutOfRange);
            }

            if (provider.TimeoutSeconds is { } timeout && (timeout < limits.Ai.MinTimeoutSeconds || timeout > limits.Ai.MaxTimeoutSeconds))
            {
                Add("aiProvider.timeoutSeconds", OutOfRange);
            }
        }

        if (patch.AiPrompts is { } prompts)
        {
            foreach (var (name, text) in new[]
            {
                ("planProposal", prompts.PlanProposal),
                ("planRebalance", prompts.PlanRebalance),
                ("taskSuggestions", prompts.TaskSuggestions),
                ("planExplanation", prompts.PlanExplanation),
            })
            {
                if (text.Length > limits.Ai.PromptMaxLength)
                {
                    Add($"aiPrompts.{name}", OutOfRange);
                }
            }
        }

        if (patch.AiPromptTemplates is { } templates)
        {
            foreach (var (name, template) in new[]
            {
                ("planProposal", templates.PlanProposal),
                ("planRebalance", templates.PlanRebalance),
                ("taskSuggestions", templates.TaskSuggestions),
                ("planExplanation", templates.PlanExplanation),
            })
            {
                CheckTemplatePart($"aiPromptTemplates.{name}.system", template.System, SchemaPlaceholder, SchemaPlaceholderRequired);
                CheckTemplatePart($"aiPromptTemplates.{name}.user", template.User, InputPlaceholder, InputPlaceholderRequired);
            }
        }

        if (patch.PromoteThreshold is < 2)
        {
            Add("promoteThreshold", OutOfRange);
        }

        if (patch.PeriodBonuses is { } bonuses)
        {
            foreach (var (name, amount) in new[]
            {
                ("weekDone", bonuses.WeekDone),
                ("weekOnTime", bonuses.WeekOnTime),
                ("cycleDone", bonuses.CycleDone),
                ("cycleOnTime", bonuses.CycleOnTime),
            })
            {
                if (amount < limits.Bonuses.MinPoints || amount > limits.Bonuses.MaxPoints)
                {
                    Add($"periodBonuses.{name}", OutOfRange);
                }
            }
        }

        if (patch.CurrencyCode is { } currency)
        {
            if (!IsCurrencyShape(currency) || !Currencies.IsKnown(currency))
            {
                Add("currencyCode", InvalidCurrencyCode);
            }
            else if (!Currencies.HasTwoDecimals(currency))
            {
                Add("currencyCode", CurrencyNotTwoDecimals);
            }
        }

        if (patch.CentsPerPoint is { } cents && (cents < limits.Points.MinCentsPerPoint || cents > limits.Points.MaxCentsPerPoint))
        {
            Add("centsPerPoint", OutOfRange);
        }

        if (patch.RewardGoals is { } goals)
        {
            foreach (var (name, points) in new[] { ("weekPoints", goals.WeekPoints), ("cyclePoints", goals.CyclePoints) })
            {
                if (points is { } value && (value < limits.Rewards.MinGoalPoints || value > limits.Rewards.MaxGoalPoints))
                {
                    Add($"rewardGoals.{name}", OutOfRange);
                }
            }
        }

        return errors.Count == 0
            ? null
            : new ValidationErrors(errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal));

        void CheckTemplatePart(string field, string text, string placeholder, string missingMessage)
        {
            if (text.Length < 1 || text.Length > limits.Ai.PromptTemplateMaxLength)
            {
                Add(field, OutOfRange);
            }
            else if (!text.Contains(placeholder, StringComparison.Ordinal))
            {
                Add(field, missingMessage);
            }
        }
    }

    private static bool IsCurrencyShape(string code) => code.Length == 3 && code.All(c => c is >= 'A' and <= 'Z');
}
