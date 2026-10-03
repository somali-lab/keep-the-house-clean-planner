using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Tasks;

namespace Huishoudplanner.Domain.Tests.Tasks;

public sealed class TaskAuditTests
{
    private const string TaskId = "0123456789abcdef01234567";
    private const string Person = "0123456789abcdef01234568";

    private static readonly AuditActor Actor = new("0123456789abcdef01234569", AuditSource.Ui);

    private static HouseholdTask Task(string? assignee = null) => new(
        TaskId, "Badkamer", "0123456789abcdef0123456a", "1w", 30, 30, assignee, true, "", [], null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public void ForChange_ofOtherFieldsOnly_isOneUpdateEntry()
    {
        var change = ChangeSet.Between(TaskAudit.Fields(Task()), TaskAudit.Fields(Task() with { Name = "Anders" }));

        var entries = TaskAudit.ForChange(Actor, TaskId, change);

        entries.Should().ContainSingle().Which.Action.Should().Be(AuditAction.Update);
    }

    [Fact]
    public void ForChange_ofTheAssigneeOnly_isOneAssignEntry()
    {
        var change = ChangeSet.Between(TaskAudit.Fields(Task()), TaskAudit.Fields(Task(Person)));

        var entries = TaskAudit.ForChange(Actor, TaskId, change);

        entries.Should().ContainSingle().Which.Action.Should().Be(AuditAction.Assign);
    }

    [Fact]
    public void ForChange_ofBoth_isUpdateThenAssign_andTheUpdateEntryNamesNoAssignee()
    {
        var change = ChangeSet.Between(TaskAudit.Fields(Task()), TaskAudit.Fields(Task(Person) with { DurationMinutes = 20 }));

        var entries = TaskAudit.ForChange(Actor, TaskId, change);

        entries.Select(e => e.Action).Should().Equal(AuditAction.Update, AuditAction.Assign);
        entries[0].After.Keys.Should().Equal("durationMinutes");
    }

    [Fact]
    public void ForChange_ofTags_comparesThemAsAWhole()
    {
        var change = ChangeSet.Between(TaskAudit.Fields(Task()), TaskAudit.Fields(Task() with { Tags = ["nat"] }));

        change.IsNoOp.Should().BeFalse();
        TaskAudit.ForChange(Actor, TaskId, change).Single().After["tags"].Should().Be(AuditArray.Of("nat"));
    }
}
