using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Tasks;

namespace Huishoudplanner.Application.Tests.Tasks;

/// <summary>
/// <c>PATCH /tasks/:id</c> with a new room (tasks.test.ts, "moves future open work to the new room while completed history keeps its old
/// room"): the room snapshots of the open occurrences from today on follow the task, in the same transaction as the task update.
/// </summary>
public sealed class TaskRoomSnapshotTests
{
    private static readonly TimeZoneInfo Amsterdam = DayKeys.FindZone("Europe/Amsterdam");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Occurrence Occurrence(TaskWorld world, string id, string taskId, DateOnly day, OccurrenceStatus status, string roomId, string roomName) => new(
        id,
        taskId,
        "f00000000000000000000001",
        null,
        DayKeys.FromDayKey(day, Amsterdam),
        DayKeys.FromDayKey(day, Amsterdam),
        null,
        status,
        null,
        status == OccurrenceStatus.Done ? TaskWorld.Now : null,
        null,
        null,
        20,
        "Kast opruimen",
        roomId,
        roomName,
        OccurrenceOrigin.Generated,
        TaskWorld.Now,
        TaskWorld.Now);

    private static (TaskWorld World, HouseholdTask Task, Huishoudplanner.Domain.Rooms.Room Badkamer, Huishoudplanner.Domain.Rooms.Room Keuken) Arrange()
    {
        var world = new TaskWorld();
        var badkamer = world.Room("Badkamer");
        var keuken = world.Room("Keuken");
        var task = world.Seed("Kast opruimen", badkamer.Id);
        return (world, task, badkamer, keuken);
    }

    [Fact]
    public async Task A_room_change_moves_future_open_work_to_the_new_room_while_completed_history_keeps_its_old_room()
    {
        var (world, task, badkamer, keuken) = Arrange();
        var today = DayKeys.Today(Amsterdam, TaskWorld.Now);
        world.Occurrences.Items.Add(Occurrence(world, "d00000000000000000000001", task.Id, today.AddDays(-7), OccurrenceStatus.Done, badkamer.Id, badkamer.Name));
        world.Occurrences.Items.Add(Occurrence(world, "d00000000000000000000002", task.Id, today.AddDays(-1), OccurrenceStatus.Open, badkamer.Id, badkamer.Name));
        world.Occurrences.Items.Add(Occurrence(world, "d00000000000000000000003", task.Id, today, OccurrenceStatus.Open, badkamer.Id, badkamer.Name));
        world.Occurrences.Items.Add(Occurrence(world, "d00000000000000000000004", task.Id, today.AddDays(3), OccurrenceStatus.Open, badkamer.Id, badkamer.Name));
        world.Occurrences.Items.Add(Occurrence(world, "d00000000000000000000005", task.Id, today.AddDays(4), OccurrenceStatus.Done, badkamer.Id, badkamer.Name));

        var result = await world.Service.UpdateAsync(TaskWorld.Planner, task.Id, new TaskPatch(RoomId: keuken.Id), Ct);

        result.AsT0.RoomId.Should().Be(keuken.Id);
        var rooms = world.Occurrences.Items.ToDictionary(o => o.Id, o => o.RoomNameSnapshot);
        rooms["d00000000000000000000001"].Should().Be("Badkamer", "history keeps its snapshot");
        rooms["d00000000000000000000002"].Should().Be("Badkamer", "work before today stays");
        rooms["d00000000000000000000003"].Should().Be("Keuken");
        rooms["d00000000000000000000004"].Should().Be("Keuken");
        rooms["d00000000000000000000005"].Should().Be("Badkamer", "done work keeps its snapshot");
        world.Occurrences.Items.Single(o => o.Id == "d00000000000000000000004").RoomIdSnapshot.Should().Be(keuken.Id);
        world.Audit.Entries.Should().ContainSingle("the snapshot refresh is not audited, as in the Node server");
    }

    [Fact]
    public async Task A_patch_that_leaves_the_room_alone_does_not_touch_occurrences()
    {
        var (world, task, badkamer, _) = Arrange();
        world.Occurrences.Items.Add(Occurrence(world, "d00000000000000000000001", task.Id, DayKeys.Today(Amsterdam, TaskWorld.Now).AddDays(2), OccurrenceStatus.Open, badkamer.Id, badkamer.Name));

        await world.Service.UpdateAsync(TaskWorld.Planner, task.Id, new TaskPatch(Name: "Kast leegruimen", DurationMinutes: 45), Ct);
        await world.Service.UpdateAsync(TaskWorld.Planner, task.Id, new TaskPatch(RoomId: badkamer.Id), Ct);

        world.Occurrences.RoomSnapshotWrites.Should().Be(0);
        world.Occurrences.Items.Single().TaskNameSnapshot.Should().Be("Kast opruimen", "a rename never rewrites a snapshot (ADR-0011)");
    }

    [Fact]
    public async Task Only_the_occurrences_of_the_patched_task_follow_the_room()
    {
        var (world, task, badkamer, keuken) = Arrange();
        var other = world.Seed("Andere taak", badkamer.Id);
        var future = DayKeys.Today(Amsterdam, TaskWorld.Now).AddDays(2);
        world.Occurrences.Items.Add(Occurrence(world, "d00000000000000000000001", other.Id, future, OccurrenceStatus.Open, badkamer.Id, badkamer.Name));

        await world.Service.UpdateAsync(TaskWorld.Planner, task.Id, new TaskPatch(RoomId: keuken.Id), Ct);

        world.Occurrences.Items.Single().RoomNameSnapshot.Should().Be("Badkamer");
    }

    [Fact]
    public async Task Without_settings_the_task_is_still_updated_and_no_snapshot_changes()
    {
        var (world, task, badkamer, keuken) = Arrange();
        world.Occurrences.Items.Add(Occurrence(world, "d00000000000000000000001", task.Id, DayKeys.Today(Amsterdam, TaskWorld.Now).AddDays(2), OccurrenceStatus.Open, badkamer.Id, badkamer.Name));
        world.SettingsStore.Document = null;

        var result = await world.Service.UpdateAsync(TaskWorld.Planner, task.Id, new TaskPatch(RoomId: keuken.Id), Ct);

        result.AsT0.RoomId.Should().Be(keuken.Id);
        world.Occurrences.Items.Single().RoomNameSnapshot.Should().Be("Badkamer");
    }

    [Fact]
    public async Task A_failing_snapshot_refresh_rolls_the_task_update_and_its_audit_entry_back()
    {
        var (world, task, badkamer, keuken) = Arrange();
        world.Occurrences.Items.Add(Occurrence(world, "d00000000000000000000001", task.Id, DayKeys.Today(Amsterdam, TaskWorld.Now).AddDays(2), OccurrenceStatus.Open, badkamer.Id, badkamer.Name));
        world.Occurrences.Failure = new PortError("occurrences down");

        var result = await world.Service.UpdateAsync(TaskWorld.Planner, task.Id, new TaskPatch(RoomId: keuken.Id), Ct);

        result.AsT4.Message.Should().Be("occurrences down");
        world.TaskStore.Items.Single().RoomId.Should().Be(badkamer.Id);
        world.Audit.Entries.Should().BeEmpty();
        world.Transactions.Aborts.Should().Be(1);
    }
}
