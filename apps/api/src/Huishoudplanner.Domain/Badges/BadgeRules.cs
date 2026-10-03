namespace Huishoudplanner.Domain.Badges;

/// <summary>
/// Rule kinds (TS <c>BADGE_RULE_TYPES</c>). <see cref="Executions"/> and <see cref="Minutes"/> look at chosen tasks (none chosen =
/// every task), <see cref="OnTimeWeeks"/> at the on-time week bonuses.
/// </summary>
public enum BadgeRuleType
{
    Executions,
    Minutes,
    OnTimeWeeks,
}

/// <summary>A badge rule. <see cref="TaskIds"/> is empty for an on-time-weeks rule, which has no tasks.</summary>
public sealed record BadgeRule(BadgeRuleType Type, IReadOnlyList<string> TaskIds, int Threshold)
{
    public static BadgeRule OnTimeWeeks(int threshold) => new(BadgeRuleType.OnTimeWeeks, [], threshold);
}

/// <summary>The part of a done occurrence that a rule needs, credited to one person.</summary>
/// <param name="Id">The occurrence id; breaks ties between executions at the same moment.</param>
/// <param name="TaskId">Null for a one-off task.</param>
/// <param name="Minutes">The duration the occurrence had (<c>durationMinutesSnapshot</c>).</param>
/// <param name="At">When the execution counts: its completion instant, else its date.</param>
public sealed record BadgeExecution(string Id, string? TaskId, int Minutes, DateTimeOffset At);

/// <summary>The standing of a person for one rule.</summary>
/// <param name="Current">How far the person is: executions, minutes or on-time weeks. It can exceed the threshold.</param>
/// <param name="AwardedAt">The moment the threshold was first crossed according to the data, or <see langword="null"/> while it is not reached.</param>
public sealed record BadgeOutcome(int Current, DateTimeOffset? AwardedAt);

/// <summary>Port of the rule evaluation of <c>packages/shared/src/badges.ts</c> (ADR-0014): pure functions of the audited execution data.</summary>
public static class BadgeRules
{
    /// <summary>Whether an execution counts for the rule: a one-off task only counts when no task was chosen (TS <c>ruleCovers</c>).</summary>
    public static bool Covers(BadgeRule rule, string? taskId)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Type == BadgeRuleType.OnTimeWeeks)
        {
            return false;
        }

        if (rule.TaskIds.Count == 0)
        {
            return true;
        }

        return taskId is not null && rule.TaskIds.Contains(taskId, StringComparer.Ordinal);
    }

    /// <summary>
    /// Evaluates one rule for one person (TS <c>evaluateBadgeRule</c>). <paramref name="executions"/> are all done executions
    /// credited to the person; <paramref name="onTimeWeekDates"/> are the instants (the last day of the week) of their
    /// on-time week bonuses. The result is a pure function of its input, so recomputing never changes it. A threshold below 1
    /// never awards through the count rules (there is no 0th execution), but a minutes rule with threshold 0 or less is
    /// awarded at the first execution, exactly as in TypeScript.
    /// </summary>
    public static BadgeOutcome Evaluate(BadgeRule rule, IReadOnlyList<BadgeExecution> executions, IReadOnlyList<DateTimeOffset> onTimeWeekDates)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(executions);
        ArgumentNullException.ThrowIfNull(onTimeWeekDates);
        if (rule.Type == BadgeRuleType.OnTimeWeeks)
        {
            var dates = onTimeWeekDates.OrderBy(d => d.ToUnixTimeMilliseconds()).ToList();
            return new BadgeOutcome(dates.Count, dates.Count >= rule.Threshold ? NthOrNull(dates, rule.Threshold, d => d) : null);
        }

        var counted = executions
            .Where(e => Covers(rule, e.TaskId))
            .OrderBy(e => e.At.ToUnixTimeMilliseconds())
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
        if (rule.Type == BadgeRuleType.Executions)
        {
            return new BadgeOutcome(counted.Count, counted.Count >= rule.Threshold ? NthOrNull(counted, rule.Threshold, e => e.At) : null);
        }

        var total = 0;
        DateTimeOffset? awardedAt = null;
        foreach (var execution in counted)
        {
            total += execution.Minutes;
            if (awardedAt is null && total >= rule.Threshold)
            {
                awardedAt = execution.At;
            }
        }

        return new BadgeOutcome(total, awardedAt);
    }

    /// <summary>The element at the 1-based position <paramref name="threshold"/>, or <see langword="null"/> when there is none (JavaScript's out-of-range <c>[threshold - 1]</c>).</summary>
    private static DateTimeOffset? NthOrNull<T>(List<T> sorted, int threshold, Func<T, DateTimeOffset> at) =>
        threshold >= 1 && threshold <= sorted.Count ? at(sorted[threshold - 1]) : null;
}
