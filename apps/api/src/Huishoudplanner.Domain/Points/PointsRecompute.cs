namespace Huishoudplanner.Domain.Points;

/// <summary>The person and the points an entry held (<see cref="PointsCorrection.From"/>) or holds now (<see cref="PointsCorrection.To"/>).</summary>
public sealed record PointsHolding(string PersonId, int Amount);

/// <summary>An execution entry that was changed or removed because it had drifted from its occurrence; <see cref="To"/> is <see langword="null"/> when it was removed.</summary>
public sealed record PointsCorrection(string Key, PointsHolding From, PointsHolding? To);

/// <summary>A week or cycle bonus that was created or removed (slice 4.2); the summary carries the shape already so the audit entry never changes.</summary>
public sealed record PointsBonusChange(string Key, string PersonId, int Amount, string Change);

/// <summary>
/// What one reconciliation did (<c>PointsRecomputeResult</c>): the answer of <c>POST /points/recompute</c> and the <c>meta</c> of its one summary audit
/// entry (requirements 4.9). The bonus members are zero until slice 4.2 adds the bonus step.
/// </summary>
public sealed record PointsRecomputeResult(
    PointsRecomputeTrigger Trigger,
    int TasksDefaulted,
    int SnapshotsSet,
    int Created,
    int Updated,
    int Removed,
    int Unattributed,
    int Skipped,
    IReadOnlyList<PointsCorrection> Corrections,
    int CorrectionsTotal,
    bool CorrectionsTruncated,
    int BonusesCreated,
    int BonusesRemoved,
    IReadOnlyList<PointsBonusChange> BonusChanges,
    int BonusChangesTotal,
    bool BonusChangesTruncated)
{
    /// <summary>The audit summary and the answer list at most this many corrections (<c>MAX_POINTS_CORRECTIONS</c>).</summary>
    public const int MaxCorrections = 100;

    public static PointsRecomputeResult Empty(PointsRecomputeTrigger trigger) =>
        new(trigger, 0, 0, 0, 0, 0, 0, 0, [], 0, false, 0, 0, [], 0, false);

    /// <summary>A run that changed nothing writes and audits nothing, so running it twice never counts anything twice.</summary>
    public bool ChangedAnything =>
        TasksDefaulted + SnapshotsSet + Created + Updated + Removed + BonusesCreated + BonusesRemoved > 0;
}
