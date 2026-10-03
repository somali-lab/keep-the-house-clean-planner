using Huishoudplanner.Domain.Identity;

namespace Huishoudplanner.Domain.Audit;

/// <summary>
/// Who did it and how it arrived. The stored entry holds exactly the actor id and the source (as in the Node server);
/// the role is not part of an entry.
/// </summary>
public sealed record AuditActor(string ActorId, AuditSource Source)
{
    /// <summary>The actor of nightly jobs and seeding (<c>SYSTEM_ACTOR_ID</c> of the Node server).</summary>
    public const string SystemActorId = "000000000000000000000000";

    public static AuditActor System { get; } = new(SystemActorId, AuditSource.System);

    public static AuditActor From(Actor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new(actor.ActorId, AuditNames.SourceOf(actor.Source));
    }
}

/// <summary>
/// One audit entry as a use case hands it to <c>ForRecordingAudit</c>. The adapter assigns what the use case must not
/// choose: the entry id and the moment (<c>at</c>, from the <c>TimeProvider</c>). <see cref="Before"/> and
/// <see cref="After"/> hold the changed fields only; <see cref="Meta"/> is the optional per-action detail (requirements 4.9).
/// </summary>
public sealed record AuditEntry(
    AuditActor Actor,
    AuditEntity Entity,
    string EntityId,
    AuditAction Action,
    AuditObject Before,
    AuditObject After,
    AuditObject? Meta = null)
{
    public static AuditEntry For(
        AuditActor actor,
        AuditEntity entity,
        string entityId,
        AuditAction action,
        FieldDiff diff,
        AuditObject? meta = null)
    {
        ArgumentNullException.ThrowIfNull(diff);
        return new(actor, entity, entityId, action, diff.Before, diff.After, meta);
    }
}
