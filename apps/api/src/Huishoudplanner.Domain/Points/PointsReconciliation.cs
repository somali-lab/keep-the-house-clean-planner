namespace Huishoudplanner.Domain.Points;

/// <summary>
/// The differences between the stored execution entries and the done occurrences (<see cref="Changes"/>), what they mean as
/// <see cref="Corrections"/>, the done occurrences nobody can be credited for and the ones that could not be read.
/// </summary>
public sealed record ReconciliationPlan(
    PointEntryChanges Changes,
    IReadOnlyList<PointsCorrection> Corrections,
    int Unattributed,
    IReadOnlySet<string> SkippedIds);

/// <summary>The pure part of the reconciliation (ADR-0011, steps 2 and 3 of <c>reconcileNow</c>).</summary>
public static class PointsReconciliation
{
    /// <summary>
    /// Computes the expected entry of every done occurrence and the inserts, updates and deletes that make the stored execution entries match:
    /// it inserts what is missing, updates what differs and deletes the entries without an occurrence. An occurrence that cannot be read is skipped
    /// and counted, and its stored entry stays as it is. Planning again after the result was applied gives an empty plan (idempotent).
    /// </summary>
    public static ReconciliationPlan Plan(IReadOnlyList<PointEntry> stored, IEnumerable<ExecutionSource> done, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(done);
        ArgumentNullException.ThrowIfNull(zone);
        var expected = new Dictionary<string, ExecutionEntryFields>(StringComparer.Ordinal);
        var unreadable = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        var unattributed = 0;
        foreach (var source in done)
        {
            var expectation = ExecutionPoints.Expect(source, zone);
            if (expectation.Unreadable)
            {
                unreadable.Add(ExecutionPoints.Key(source.Id));
                skipped.Add(source.Id);
            }
            else if (expectation.Fields is { } fields)
            {
                expected[ExecutionPoints.Key(source.Id)] = fields;
            }
            else if (expectation.Unattributed)
            {
                unattributed++;
            }
        }

        var storedByKey = stored.ToDictionary(e => e.Key, StringComparer.Ordinal);
        var inserts = new List<PointEntryInsert>();
        var updates = new List<PointEntryUpdate>();
        var deletes = new List<PointEntry>();
        var corrections = new List<PointsCorrection>();
        foreach (var (key, fields) in expected.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!storedByKey.TryGetValue(key, out var current))
            {
                inserts.Add(new PointEntryInsert(key, fields));
            }
            else if (!current.Matches(fields))
            {
                updates.Add(new PointEntryUpdate(current, fields));
                corrections.Add(new PointsCorrection(key, new PointsHolding(current.PersonId, current.Amount), new PointsHolding(fields.PersonId, fields.Amount)));
            }
        }

        foreach (var current in stored.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (expected.ContainsKey(current.Key) || unreadable.Contains(current.Key))
            {
                continue;
            }

            deletes.Add(current);
            corrections.Add(new PointsCorrection(current.Key, new PointsHolding(current.PersonId, current.Amount), null));
        }

        return new ReconciliationPlan(new PointEntryChanges(inserts, updates, deletes), corrections, unattributed, skipped);
    }

    /// <summary>The first <see cref="PointsRecomputeResult.MaxCorrections"/> corrections, with the total and whether the list was cut.</summary>
    public static (IReadOnlyList<PointsCorrection> Listed, int Total, bool Truncated) LimitCorrections(IReadOnlyList<PointsCorrection> corrections)
    {
        ArgumentNullException.ThrowIfNull(corrections);
        return (
            [.. corrections.Take(PointsRecomputeResult.MaxCorrections)],
            corrections.Count,
            corrections.Count > PointsRecomputeResult.MaxCorrections);
    }
}
