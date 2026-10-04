using Huishoudplanner.Domain.Points;

namespace Huishoudplanner.Domain.Badges;

/// <summary>What started an evaluation of all awards: a points reconciliation, a change of a badge, or a statistics reset (TS <c>BadgeEvalTrigger</c>).</summary>
public enum BadgeEvalTrigger
{
    Startup,
    Nightly,
    Import,
    Admin,
    Badge,
    Reset,
}

public static class BadgeTriggers
{
    public static string ToWire(BadgeEvalTrigger trigger) => trigger switch
    {
        BadgeEvalTrigger.Startup => "startup",
        BadgeEvalTrigger.Nightly => "nightly",
        BadgeEvalTrigger.Import => "import",
        BadgeEvalTrigger.Admin => "admin",
        BadgeEvalTrigger.Badge => "badge",
        BadgeEvalTrigger.Reset => "reset",
        _ => throw new ArgumentOutOfRangeException(nameof(trigger)),
    };

    /// <summary>The trigger of the badge step of a points reconciliation: the same names.</summary>
    public static BadgeEvalTrigger From(PointsRecomputeTrigger trigger) => trigger switch
    {
        PointsRecomputeTrigger.Startup => BadgeEvalTrigger.Startup,
        PointsRecomputeTrigger.Nightly => BadgeEvalTrigger.Nightly,
        PointsRecomputeTrigger.Import => BadgeEvalTrigger.Import,
        PointsRecomputeTrigger.Admin => BadgeEvalTrigger.Admin,
        _ => throw new ArgumentOutOfRangeException(nameof(trigger)),
    };
}

/// <summary>
/// Whose awards an evaluation looks at. <see cref="PersonIds"/> is <see langword="null"/> for everybody. When the task of the execution that
/// changed is known (<see cref="TaskKnown"/>; <see cref="TaskId"/> is <see langword="null"/> for a one-off task) a badge whose rule does not
/// cover it cannot have changed, so its executions are not loaded and its awards are left alone.
/// </summary>
public sealed record BadgeEvaluationScope(IReadOnlyCollection<string>? PersonIds, bool TaskKnown = false, string? TaskId = null)
{
    public static BadgeEvaluationScope Everybody { get; } = new(PersonIds: null);
}

/// <summary>The badges one evaluation looks at, and what that needs to read.</summary>
/// <param name="Evaluated">The active badges the change can have affected.</param>
/// <param name="Untouched">Ids of the active badges the changed task cannot affect: neither evaluated nor touched.</param>
/// <param name="Names">The name of every badge by id (also of a deleted one, when the caller said so), for the history.</param>
public sealed record BadgeSelection(
    IReadOnlyList<Badge> Evaluated,
    IReadOnlySet<string> Untouched,
    IReadOnlyDictionary<string, string> Names)
{
    /// <summary>Executions are only read when an evaluated badge counts them.</summary>
    public bool NeedsExecutions => Evaluated.Any(b => b.Rule.Type != BadgeRuleType.OnTimeWeeks);

    /// <summary>The on-time week bonuses are only read when an evaluated badge counts them.</summary>
    public bool NeedsOnTimeWeeks => Evaluated.Any(b => b.Rule.Type == BadgeRuleType.OnTimeWeeks);
}

/// <summary>A new award the plan inserts.</summary>
public sealed record PlannedAward(string BadgeId, string PersonId, DateTimeOffset AwardedAt);

/// <summary>An award whose moment moves.</summary>
public sealed record PlannedMove(BadgeAward Current, DateTimeOffset AwardedAt);

/// <summary>The differences between the expected and the stored awards.</summary>
public sealed record BadgeAwardPlan(IReadOnlyList<PlannedAward> Inserts, IReadOnlyList<PlannedMove> Updates, IReadOnlyList<BadgeAward> Deletes)
{
    public static BadgeAwardPlan Empty { get; } = new([], [], []);

    public bool IsEmpty => Inserts.Count == 0 && Updates.Count == 0 && Deletes.Count == 0;
}

public enum AwardChange
{
    Created,
    Updated,
    Removed,
}

/// <summary>A change the store really made: <see cref="Award"/> is the award after a create or update, or the one that was removed; <see cref="Previous"/> the award before an update.</summary>
public sealed record AppliedAward(AwardChange Change, BadgeAward Award, BadgeAward? Previous = null);

/// <summary>How many awards an evaluation created, moved and removed.</summary>
public sealed record BadgeEvaluationResult(int Created, int Updated, int Removed)
{
    public static BadgeEvaluationResult None { get; } = new(0, 0, 0);
}

/// <summary>
/// Makes the awards match the data (ADR-0014; <c>evaluateBadgeAwards</c> of the Node server): a person holds a badge exactly while the done
/// executions credited to them, or their on-time week bonuses, reach the threshold of an active badge. The award is dated at the moment the data
/// first crossed the threshold, so recomputing gives the same answer every time and never awards twice. Pure: it reads nothing and writes nothing.
/// </summary>
public static class BadgeAwardPlanner
{
    public static BadgeSelection Select(IReadOnlyList<Badge> everyBadge, BadgeEvaluationScope scope, IReadOnlyDictionary<string, string>? extraNames = null)
    {
        ArgumentNullException.ThrowIfNull(everyBadge);
        ArgumentNullException.ThrowIfNull(scope);
        var names = new Dictionary<string, string>(extraNames ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var badge in everyBadge)
        {
            names[badge.Id] = badge.Name;
        }

        var active = everyBadge.Where(b => b.Active).ToList();
        var untouched = new HashSet<string>(StringComparer.Ordinal);
        if (scope.TaskKnown)
        {
            foreach (var badge in active.Where(b => b.Rule.Type != BadgeRuleType.OnTimeWeeks && !BadgeRules.Covers(b.Rule, scope.TaskId)))
            {
                untouched.Add(badge.Id);
            }
        }

        return new BadgeSelection([.. active.Where(b => !untouched.Contains(b.Id))], untouched, names);
    }

    public static BadgeAwardPlan Plan(
        BadgeSelection selection,
        BadgeEvaluationScope scope,
        IReadOnlyList<BadgeAward> stored,
        IReadOnlyList<CreditedExecution> executions,
        IReadOnlyList<OnTimeWeek> onTimeWeeks)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(executions);
        ArgumentNullException.ThrowIfNull(onTimeWeeks);
        var considered = stored.Where(a => !selection.Untouched.Contains(a.BadgeId)).ToList();
        var badges = selection.Evaluated;
        if (badges.Count == 0 && considered.Count == 0)
        {
            return BadgeAwardPlan.Empty;
        }

        var executionsOf = executions.GroupBy(e => e.PersonId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<BadgeExecution>)[.. g.Select(e => e.Execution)], StringComparer.Ordinal);
        var onTimeOf = onTimeWeeks.GroupBy(w => w.PersonId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DateTimeOffset>)[.. g.Select(w => w.Date)], StringComparer.Ordinal);

        var people = new SortedSet<string>(executionsOf.Keys.Concat(onTimeOf.Keys).Concat(scope.PersonIds ?? []), StringComparer.Ordinal);
        var expected = new Dictionary<string, PlannedAward>(StringComparer.Ordinal);
        foreach (var person in people)
        {
            foreach (var badge in badges)
            {
                var outcome = BadgeRules.Evaluate(badge.Rule, executionsOf.GetValueOrDefault(person) ?? [], onTimeOf.GetValueOrDefault(person) ?? []);
                if (outcome.AwardedAt is { } at)
                {
                    expected[BadgeAward.KeyOf(badge.Id, person)] = new PlannedAward(badge.Id, person, at);
                }
            }
        }

        var storedByKey = considered.ToDictionary(a => a.Key, StringComparer.Ordinal);
        var inserts = new List<PlannedAward>();
        var updates = new List<PlannedMove>();
        foreach (var (key, want) in expected)
        {
            if (!storedByKey.TryGetValue(key, out var current))
            {
                inserts.Add(want);
            }
            else if (current.AwardedAt.ToUnixTimeMilliseconds() != want.AwardedAt.ToUnixTimeMilliseconds())
            {
                updates.Add(new PlannedMove(current, want.AwardedAt));
            }
        }

        var deletes = storedByKey.Where(pair => !expected.ContainsKey(pair.Key)).Select(pair => pair.Value).ToList();
        return new BadgeAwardPlan(inserts, updates, deletes);
    }
}
