using Huishoudplanner.Domain.Identity;

namespace Huishoudplanner.Domain.Audit;

/// <summary>The entity kinds of <c>auditLog.entity</c> (requirements section 3).</summary>
public enum AuditEntity
{
    Task,
    CyclePlan,
    Occurrence,
    User,
    Settings,
    Cycle,
    Room,
    Import,
    Points,
    Badge,
    BadgeAward,
}

/// <summary>The actions of <c>auditLog.action</c>. <see cref="AiApply"/> only exists in old history and is never written.</summary>
public enum AuditAction
{
    Create,
    Update,
    Delete,
    Complete,
    Uncomplete,
    Skip,
    Reschedule,
    Assign,
    Activate,
    AiApply,
    Reset,
    Recompute,
}

/// <summary>The origin of a change, <c>auditLog.source</c>.</summary>
public enum AuditSource
{
    Ui,
    Api,
    Ai,
    System,
}

/// <summary>The wire names stored in <c>auditLog</c>, identical to the Node server's.</summary>
public static class AuditNames
{
    public static string ToWire(AuditEntity entity) => entity switch
    {
        AuditEntity.Task => "task",
        AuditEntity.CyclePlan => "cyclePlan",
        AuditEntity.Occurrence => "occurrence",
        AuditEntity.User => "user",
        AuditEntity.Settings => "settings",
        AuditEntity.Cycle => "cycle",
        AuditEntity.Room => "room",
        AuditEntity.Import => "import",
        AuditEntity.Points => "points",
        AuditEntity.Badge => "badge",
        AuditEntity.BadgeAward => "badgeAward",
        _ => throw new ArgumentOutOfRangeException(nameof(entity)),
    };

    public static string ToWire(AuditAction action) => action switch
    {
        AuditAction.Create => "create",
        AuditAction.Update => "update",
        AuditAction.Delete => "delete",
        AuditAction.Complete => "complete",
        AuditAction.Uncomplete => "uncomplete",
        AuditAction.Skip => "skip",
        AuditAction.Reschedule => "reschedule",
        AuditAction.Assign => "assign",
        AuditAction.Activate => "activate",
        AuditAction.AiApply => "ai-apply",
        AuditAction.Reset => "reset",
        AuditAction.Recompute => "recompute",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public static string ToWire(AuditSource source) => source switch
    {
        AuditSource.Ui => "ui",
        AuditSource.Api => "api",
        AuditSource.Ai => "ai",
        AuditSource.System => "system",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    /// <summary>The wire names of every entity, in declaration order.</summary>
    public static IReadOnlyList<string> EntityNames { get; } = [.. Enum.GetValues<AuditEntity>().Select(ToWire)];

    /// <summary>The wire names of every source, in declaration order.</summary>
    public static IReadOnlyList<string> SourceNames { get; } = [.. Enum.GetValues<AuditSource>().Select(ToWire)];

    /// <summary>Parses a wire name (exact, case sensitive).</summary>
    public static bool TryParseEntity(string? wire, out AuditEntity entity)
    {
        foreach (var candidate in Enum.GetValues<AuditEntity>())
        {
            if (ToWire(candidate) == wire)
            {
                entity = candidate;
                return true;
            }
        }

        entity = default;
        return false;
    }

    /// <summary>Parses a wire name (exact, case sensitive).</summary>
    public static bool TryParseSource(string? wire, out AuditSource source)
    {
        foreach (var candidate in Enum.GetValues<AuditSource>())
        {
            if (ToWire(candidate) == wire)
            {
                source = candidate;
                return true;
            }
        }

        source = default;
        return false;
    }

    public static AuditSource SourceOf(ActorSource source) => source switch
    {
        ActorSource.Ui => AuditSource.Ui,
        ActorSource.Api => AuditSource.Api,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };
}
