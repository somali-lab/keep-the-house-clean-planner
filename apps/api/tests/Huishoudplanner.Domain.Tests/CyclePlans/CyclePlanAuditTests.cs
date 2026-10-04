using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.CyclePlans;

namespace Huishoudplanner.Domain.Tests.CyclePlans;

/// <summary>The audit shapes of <c>data/cyclePlans.ts</c>: stored fields without id and timestamps, and slot diffs with only the touched slots.</summary>
public sealed class CyclePlanAuditTests
{
    private const string PlanId = "0123456789abcdef01234567";
    private const string Task = "a00000000000000000000001";
    private const string Anna = "0000000000000000000000a1";
    private static readonly AuditActor Actor = new("0000000000000000000000ff", AuditSource.Ui);

    private static CyclePlan Plan(params CyclePlanSlot[] slots) =>
        new(PlanId, "Zomer", false, slots, ["Keuken", "", "", ""], false, PlanSources.Manual, null, null, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_create_entry_holds_every_stored_field_and_the_source_of_a_copy_in_meta()
    {
        var entry = CyclePlanAudit.ForCreate(Actor, Plan(new CyclePlanSlot(Task, 0, 1, Anna, 2)), copiedFrom: "bbbbbbbbbbbbbbbbbbbbbbbb");

        entry.Entity.Should().Be(AuditEntity.CyclePlan);
        entry.Action.Should().Be(AuditAction.Create);
        entry.Before.Should().Be(AuditObject.Empty);
        entry.After.Should().Be(AuditObject.Of(
            ("name", "Zomer"),
            ("active", false),
            ("slots", AuditArray.Of(AuditObject.Of(
                ("taskId", new AuditObjectId(Task)),
                ("weekIndex", 0),
                ("weekday", 1),
                ("assigneeId", new AuditObjectId(Anna)),
                ("sortOrder", 2)))),
            ("weekThemes", AuditArray.Of("Keuken", "", "", "")),
            ("draft", false),
            ("source", "manual"),
            ("proposalId", AuditNull.Instance),
            ("rationale", AuditNull.Instance),
            ("discarded", false)));
        entry.Meta.Should().Be(AuditObject.Of(("copiedFrom", new AuditObjectId("bbbbbbbbbbbbbbbbbbbbbbbb"))));
    }

    [Fact]
    public void A_create_entry_without_a_source_has_no_meta()
    {
        CyclePlanAudit.ForCreate(Actor, Plan()).Meta.Should().BeNull();
    }

    [Fact]
    public void A_delete_entry_holds_the_removed_plan_as_before()
    {
        var entry = CyclePlanAudit.ForDelete(Actor, Plan());

        entry.Action.Should().Be(AuditAction.Delete);
        entry.After.Should().Be(AuditObject.Empty);
        entry.Before["name"].Should().Be(new AuditString("Zomer"));
        entry.Before["active"].Should().Be(new AuditBool(false));
    }

    [Fact]
    public void A_task_delete_entry_lists_the_removed_slots_with_the_reason_and_the_task_in_meta()
    {
        var first = new CyclePlanSlot(Task, 0, 3, null);
        var second = new CyclePlanSlot(Task, 2, 5, Anna);

        var entry = CyclePlanAudit.ForTaskDelete(Actor, PlanId, Task, new SlotDiff([], [first, second], []));

        entry.Entity.Should().Be(AuditEntity.CyclePlan);
        entry.EntityId.Should().Be(PlanId);
        entry.Action.Should().Be(AuditAction.Update);
        entry.Before.Should().Be(AuditObject.Of(("slots", AuditArray.Of(CyclePlanAudit.Slot(first), CyclePlanAudit.Slot(second)))));
        entry.After.Should().Be(AuditObject.Of(("slots", AuditArray.Of())));
        entry.Meta.Should().Be(AuditObject.Of(("reason", "task_delete"), ("taskId", new AuditObjectId(Task))));
    }

    [Fact]
    public void A_slot_entry_lists_removed_then_changed_before_and_added_then_changed_after()
    {
        var removed = new CyclePlanSlot(Task, 0, 3, null);
        var added = new CyclePlanSlot(Task, 1, 5, null);
        var changedBefore = new CyclePlanSlot(Task, 0, 1, Anna);
        var changedAfter = changedBefore with { AssigneeId = null };

        var entry = CyclePlanAudit.ForSlots(Actor, PlanId, new SlotDiff([added], [removed], [(changedBefore, changedAfter)]));

        entry.Action.Should().Be(AuditAction.Update);
        entry.Before.Should().Be(AuditObject.Of(("slots", AuditArray.Of(CyclePlanAudit.Slot(removed), CyclePlanAudit.Slot(changedBefore)))));
        entry.After.Should().Be(AuditObject.Of(("slots", AuditArray.Of(CyclePlanAudit.Slot(added), CyclePlanAudit.Slot(changedAfter)))));
    }
}
