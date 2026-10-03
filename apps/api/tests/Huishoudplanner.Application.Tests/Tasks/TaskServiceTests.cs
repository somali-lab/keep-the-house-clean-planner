using Huishoudplanner.Application.Tests.Settings;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Tasks;

namespace Huishoudplanner.Application.Tests.Tasks;

/// <summary>The task use cases (tasks.test.ts and the task side of interval-change.test.ts): every result variant, the audit input and "no write on a no-op".</summary>
public sealed class TaskServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly AuditActor PlannerActor = new(TaskWorld.Planner.ActorId, AuditSource.Ui);

    // ---- create

    [Fact]
    public async Task Create_withDefaults_storesAnActiveTask_withDefaultPoints_andAuditsEveryField()
    {
        var world = new TaskWorld();
        var room = world.Room("Badkamer");

        var result = await world.Service.CreateAsync(TaskWorld.Planner, new CreateTaskCommand("Badkamer schoonmaken", room.Id, "1w", 30, Tags: ["nat"]), Ct);

        var task = result.AsT0;
        task.Should().BeEquivalentTo(new
        {
            Name = "Badkamer schoonmaken",
            RoomId = room.Id,
            IntervalKey = "1w",
            DurationMinutes = 30,
            Points = 30,
            DefaultAssigneeId = (string?)null,
            Active = true,
            Notes = "",
            LastCompletedAt = (DateTimeOffset?)null,
            CreatedAt = TaskWorld.Now,
            UpdatedAt = TaskWorld.Now,
        });
        task.Tags.Should().Equal("nat");
        world.Audit.Entries.Should().ContainSingle().Which.Should().Be(new AuditEntry(
            PlannerActor,
            AuditEntity.Task,
            task.Id,
            AuditAction.Create,
            AuditObject.Empty,
            AuditObject.Of(
                ("name", "Badkamer schoonmaken"),
                ("roomId", new AuditObjectId(room.Id)),
                ("intervalKey", "1w"),
                ("durationMinutes", 30),
                ("points", 30),
                ("defaultAssigneeId", AuditNull.Instance),
                ("notes", ""),
                ("tags", AuditArray.Of("nat")),
                ("active", true),
                ("lastCompletedAt", AuditNull.Instance))));
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(10, 10)]
    [InlineData(1200, 1000)]
    public async Task Create_withoutPoints_defaultsToOnePointPerMinute_atMostTheMaximum(int minutes, int expected)
    {
        var world = new TaskWorld();
        var room = world.Room("Badkamer");

        var result = await world.Service.CreateAsync(TaskWorld.Planner, TaskWorld.Command(room.Id, minutes: minutes), Ct);

        result.AsT0.Points.Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(1000)]
    public async Task Create_withExplicitPoints_keepsThem_zeroIncluded(int points)
    {
        var world = new TaskWorld();
        var room = world.Room("Badkamer");

        var result = await world.Service.CreateAsync(TaskWorld.Planner, TaskWorld.Command(room.Id) with { Points = points }, Ct);

        result.AsT0.Points.Should().Be(points);
    }

    [Fact]
    public async Task Create_trimsTheNameAndTags_keepsTheNotes_andStoresTheDefaultAssignee()
    {
        var world = new TaskWorld();
        var room = world.Room("Badkamer");
        var person = world.Person("Sven");

        var result = await world.Service.CreateAsync(
            TaskWorld.Planner,
            new CreateTaskCommand("  Kast  ", room.Id, "1w", 10, null, person.Id, "  twee spaties ", ["  nat ", "kamer"]),
            Ct);

        var task = result.AsT0;
        task.Name.Should().Be("Kast");
        task.Tags.Should().Equal("nat", "kamer");
        task.Notes.Should().Be("  twee spaties ");
        task.DefaultAssigneeId.Should().Be(person.Id);
    }

    [Fact]
    public async Task Create_withAnIntervalFromSettings_isAccepted_evenWhenItIsNotADefault()
    {
        var world = new TaskWorld();
        var room = world.Room("Badkamer");
        world.SettingsStore.Document = world.SettingsStore.Document! with
        {
            Intervals = [.. world.SettingsStore.Document.Intervals, SettingsSamples.Year],
        };

        var result = await world.Service.CreateAsync(TaskWorld.Planner, TaskWorld.Command(room.Id, "Matras keren", "year", 15), Ct);

        result.AsT0.IntervalKey.Should().Be("year");
    }

    [Theory]
    [InlineData("", "name")]
    [InlineData("   ", "name")]
    public async Task Create_withABlankName_isAValidationErrorOnName_andWritesNothing(string name, string field)
    {
        var world = new TaskWorld();
        var room = world.Room("Badkamer");

        var result = await world.Service.CreateAsync(TaskWorld.Planner, TaskWorld.Command(room.Id, name), Ct);

        result.AsT1.Errors.Should().ContainKey(field);
        NothingWritten(world);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Create_withADurationBelowOne_isAValidationErrorOnDurationMinutes(int minutes)
    {
        var world = new TaskWorld();
        var room = world.Room("Badkamer");

        var result = await world.Service.CreateAsync(TaskWorld.Planner, TaskWorld.Command(room.Id, minutes: minutes), Ct);

        result.AsT1.Errors.Should().ContainKey("durationMinutes");
        NothingWritten(world);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    public async Task Create_withPointsOutOfRange_isAValidationErrorOnPoints(int points)
    {
        var world = new TaskWorld();
        var room = world.Room("Badkamer");

        var result = await world.Service.CreateAsync(TaskWorld.Planner, TaskWorld.Command(room.Id) with { Points = points }, Ct);

        result.AsT1.Errors.Should().ContainKey("points");
        NothingWritten(world);
    }

    [Fact]
    public async Task Create_withBadIdsAndABlankTag_namesEveryField()
    {
        var world = new TaskWorld();

        var result = await world.Service.CreateAsync(
            TaskWorld.Planner,
            new CreateTaskCommand("X", "nope", "", 10, null, "also-nope", "", ["ok", " "]),
            Ct);

        result.AsT1.Errors.Keys.Should().BeEquivalentTo("roomId", "intervalKey", "defaultAssigneeId", "tags.1");
        NothingWritten(world);
    }

    [Fact]
    public async Task Create_withUnknownRoomIntervalAndAssignee_namesAllThree_andWritesNothing()
    {
        var world = new TaskWorld();

        var result = await world.Service.CreateAsync(
            TaskWorld.Planner,
            new CreateTaskCommand("X", "0123456789abcdef01234567", "fortnightly", 10, DefaultAssigneeId: "0123456789abcdef01234568"),
            Ct);

        result.AsT1.Errors.Should().BeEquivalentTo(new Dictionary<string, string[]>
        {
            ["roomId"] = ["unknown_room"],
            ["intervalKey"] = ["unknown_interval"],
            ["defaultAssigneeId"] = ["unknown_user"],
        });
        NothingWritten(world);
    }

    [Fact]
    public async Task Create_inAnInactiveRoom_orWithAnInactiveAssignee_isRefused()
    {
        var world = new TaskWorld();
        var room = world.Room("Zolder", active: false);
        var gone = world.Person("Oud", active: false);

        var result = await world.Service.CreateAsync(TaskWorld.Planner, TaskWorld.Command(room.Id) with { DefaultAssigneeId = gone.Id }, Ct);

        result.AsT1.Errors.Should().BeEquivalentTo(new Dictionary<string, string[]>
        {
            ["roomId"] = ["inactive_room"],
            ["defaultAssigneeId"] = ["inactive_user"],
        });
    }

    [Fact]
    public async Task Create_withoutSettings_hasNoKnownInterval()
    {
        var world = new TaskWorld();
        var room = world.Room("Badkamer");
        world.SettingsStore.Document = null;

        var result = await world.Service.CreateAsync(TaskWorld.Planner, TaskWorld.Command(room.Id), Ct);

        result.AsT1.Errors.Should().ContainKey("intervalKey").WhoseValue.Should().Equal("unknown_interval");
    }

    [Fact]
    public async Task Create_whenTheAuditEntryCannotBeWritten_rollsTheTaskBack()
    {
        var world = new TaskWorld();
        var room = world.Room("Badkamer");
        world.Audit.Failure = new PortError("audit down");

        var result = await world.Service.CreateAsync(TaskWorld.Planner, TaskWorld.Command(room.Id), Ct);

        result.AsT3.Message.Should().Be("audit down");
        world.TaskStore.Items.Should().BeEmpty();
        world.Transactions.Aborts.Should().Be(1);
    }

    [Fact]
    public async Task Create_whenTheTransactionConflicts_returnsTheConflict()
    {
        var world = new TaskWorld();
        var room = world.Room("Badkamer");
        world.Transactions.ConflictInsteadOfRunning = new ConflictError("write_conflict", "busy");

        var result = await world.Service.CreateAsync(TaskWorld.Planner, TaskWorld.Command(room.Id), Ct);

        result.AsT2.Code.Should().Be("write_conflict");
    }

    [Fact]
    public async Task Create_whenTheStoreFails_isAPortError()
    {
        var world = new TaskWorld();
        var room = world.Room("Badkamer");
        world.TaskStore.FailWrites = true;

        var result = await world.Service.CreateAsync(TaskWorld.Planner, TaskWorld.Command(room.Id), Ct);

        result.IsT3.Should().BeTrue();
        world.Audit.Entries.Should().BeEmpty();
    }

    // ---- update

    [Fact]
    public async Task Update_auditsOldAndNewValuesOfNameIntervalDurationAndRoom_inOneUpdateEntry()
    {
        var world = new TaskWorld();
        var badkamer = world.Room("Badkamer");
        var keuken = world.Room("Keuken");
        var task = world.Seed("Badkamer schoonmaken", badkamer.Id);

        var result = await world.Service.UpdateAsync(
            TaskWorld.Planner,
            task.Id,
            new TaskPatch(Name: "Badkamer grondig", RoomId: keuken.Id, IntervalKey: "2wk", DurationMinutes: 45),
            Ct);

        result.AsT0.Should().BeEquivalentTo(new { Name = "Badkamer grondig", RoomId = keuken.Id, IntervalKey = "2wk", DurationMinutes = 45, UpdatedAt = TaskWorld.Now });
        var entry = world.Audit.Entries.Should().ContainSingle().Subject;
        entry.Action.Should().Be(AuditAction.Update);
        entry.EntityId.Should().Be(task.Id);
        entry.Before.Should().Be(AuditObject.Of(
            ("name", "Badkamer schoonmaken"),
            ("roomId", new AuditObjectId(badkamer.Id)),
            ("intervalKey", "1w"),
            ("durationMinutes", 30)));
        entry.After.Should().Be(AuditObject.Of(
            ("name", "Badkamer grondig"),
            ("roomId", new AuditObjectId(keuken.Id)),
            ("intervalKey", "2wk"),
            ("durationMinutes", 45)));
    }

    [Fact]
    public async Task Update_ofTheDefaultAssignee_isItsOwnAssignEntry_andNoUpdateEntry()
    {
        var world = new TaskWorld();
        var room = world.Room("Badkamer");
        var person = world.Person("Sven");
        var task = world.Seed("Badkamer schoonmaken", room.Id);

        var result = await world.Service.UpdateAsync(TaskWorld.Planner, task.Id, new TaskPatch(DefaultAssignee: new AssigneeChoice(person.Id)), Ct);

        result.AsT0.DefaultAssigneeId.Should().Be(person.Id);
        var entry = world.Audit.Entries.Should().ContainSingle().Subject;
        entry.Action.Should().Be(AuditAction.Assign);
        entry.Before.Should().Be(AuditObject.Of(("defaultAssigneeId", AuditNull.Instance)));
        entry.After.Should().Be(AuditObject.Of(("defaultAssigneeId", new AuditObjectId(person.Id))));
    }

    [Fact]
    public async Task Update_ofDurationAndAssignee_writesAnUpdateEntryAndAnAssignEntry()
    {
        var world = new TaskWorld();
        var room = world.Room("Badkamer");
        var person = world.Person("Sven");
        var task = world.Seed("Badkamer schoonmaken", room.Id);

        await world.Service.UpdateAsync(
            TaskWorld.Planner, task.Id, new TaskPatch(DurationMinutes: 20, DefaultAssignee: new AssigneeChoice(person.Id)), Ct);

        world.Audit.Entries.Select(e => e.Action).Should().Equal(AuditAction.Update, AuditAction.Assign);
        world.Audit.Entries[0].After.Should().Be(AuditObject.Of(("durationMinutes", 20)));
    }

    [Fact]
    public async Task Update_clearingTheDefaultAssignee_isAnAssignEntryBackToNull()
    {
        var world = new TaskWorld();
        var room = world.Room("Badkamer");
        var person = world.Person("Sven");
        var task = world.Seed("Badkamer schoonmaken", room.Id, assignee: person.Id);

        var result = await world.Service.UpdateAsync(TaskWorld.Planner, task.Id, new TaskPatch(DefaultAssignee: new AssigneeChoice(null)), Ct);

        result.AsT0.DefaultAssigneeId.Should().BeNull();
        world.Audit.Entries.Single().After.Should().Be(AuditObject.Of(("defaultAssigneeId", AuditNull.Instance)));
    }

    [Fact]
    public async Task Update_deactivating_isAnUpdateEntryOfActive()
    {
        var world = new TaskWorld();
        var room = world.Room("Badkamer");
        var task = world.Seed("Badkamer schoonmaken", room.Id);

        var result = await world.Service.UpdateAsync(TaskWorld.Planner, task.Id, new TaskPatch(Active: false), Ct);

        result.AsT0.Active.Should().BeFalse();
        var entry = world.Audit.Entries.Single();
        entry.Before.Should().Be(AuditObject.Of(("active", true)));
        entry.After.Should().Be(AuditObject.Of(("active", false)));
    }

    [Fact]
    public async Task Update_thatChangesNothing_writesAndAuditsNothing_andReturnsTheTaskAsItIs()
    {
        var world = new TaskWorld();
        var room = world.Room("Badkamer");
        var task = world.Seed("Badkamer schoonmaken", room.Id);

        var result = await world.Service.UpdateAsync(
            TaskWorld.Planner, task.Id, new TaskPatch(Name: " Badkamer schoonmaken ", DurationMinutes: 30, Tags: [], Active: true), Ct);

        result.AsT0.Should().Be(task);
        NothingWritten(world);
    }

    [Fact]
    public async Task Update_ofUnknownTask_isNotFound()
    {
        var world = new TaskWorld();

        var result = await world.Service.UpdateAsync(TaskWorld.Planner, "0123456789abcdef01234567", new TaskPatch(Name: "X"), Ct);

        result.IsT1.Should().BeTrue();
        NothingWritten(world);
    }

    [Fact]
    public async Task Update_withABadId_isAValidationErrorOnId()
    {
        var world = new TaskWorld();

        var result = await world.Service.UpdateAsync(TaskWorld.Planner, "nope", new TaskPatch(Name: "X"), Ct);

        result.AsT2.Errors.Should().ContainKey("id");
    }

    [Fact]
    public async Task Update_withZeroDuration_orAnUnknownInterval_isAValidationError()
    {
        var world = new TaskWorld();
        var room = world.Room("Badkamer");
        var task = world.Seed("Badkamer schoonmaken", room.Id);

        var zero = await world.Service.UpdateAsync(TaskWorld.Planner, task.Id, new TaskPatch(DurationMinutes: 0), Ct);
        var interval = await world.Service.UpdateAsync(TaskWorld.Planner, task.Id, new TaskPatch(IntervalKey: "nope"), Ct);

        zero.AsT2.Errors.Should().ContainKey("durationMinutes");
        interval.AsT2.Errors.Should().ContainKey("intervalKey").WhoseValue.Should().Equal("unknown_interval");
        NothingWritten(world);
    }

    [Fact]
    public async Task Update_checksTheReferencesBeforeTheTask_likeTheNodeServer()
    {
        var world = new TaskWorld();

        var result = await world.Service.UpdateAsync(
            TaskWorld.Planner, "0123456789abcdef01234567", new TaskPatch(IntervalKey: "nope"), Ct);

        result.IsT2.Should().BeTrue();
    }

    [Fact]
    public async Task Update_movingToAnInactiveRoom_orAssigningAnInactiveUser_isRefused()
    {
        var world = new TaskWorld();
        var room = world.Room("Badkamer");
        var inactiveRoom = world.Room("Zolder", active: false);
        var gone = world.Person("Oud", active: false);
        var task = world.Seed("Badkamer schoonmaken", room.Id);

        var result = await world.Service.UpdateAsync(
            TaskWorld.Planner, task.Id, new TaskPatch(RoomId: inactiveRoom.Id, DefaultAssignee: new AssigneeChoice(gone.Id)), Ct);

        result.AsT2.Errors.Should().BeEquivalentTo(new Dictionary<string, string[]>
        {
            ["roomId"] = ["inactive_room"],
            ["defaultAssigneeId"] = ["inactive_user"],
        });
    }

    [Fact]
    public async Task Update_ofANameOnlyInAnInactiveRoom_isAccepted_becauseTheRoomIsNotInThePatch()
    {
        var world = new TaskWorld();
        var room = world.Room("Zolder", active: false);
        var task = world.Seed("Zolder opruimen", room.Id);

        var result = await world.Service.UpdateAsync(TaskWorld.Planner, task.Id, new TaskPatch(Name: "Zolder vegen"), Ct);

        result.AsT0.Name.Should().Be("Zolder vegen");
    }

    [Fact]
    public async Task Update_whenTheAuditEntryCannotBeWritten_rollsTheChangeBack()
    {
        var world = new TaskWorld();
        var room = world.Room("Badkamer");
        var task = world.Seed("Badkamer schoonmaken", room.Id);
        world.Audit.Failure = new PortError("audit down");

        var result = await world.Service.UpdateAsync(TaskWorld.Planner, task.Id, new TaskPatch(Name: "Anders"), Ct);

        result.AsT4.Message.Should().Be("audit down");
        world.TaskStore.Items.Single().Name.Should().Be("Badkamer schoonmaken");
    }

    [Fact]
    public async Task Update_ofTheDuration_keepsThePointsAsStored()
    {
        var world = new TaskWorld();
        var room = world.Room("Badkamer");
        var task = world.Seed("Badkamer schoonmaken", room.Id);

        var result = await world.Service.UpdateAsync(TaskWorld.Planner, task.Id, new TaskPatch(DurationMinutes: 45), Ct);

        result.AsT0.Points.Should().Be(30, "points do not follow the duration once set");
    }

    // ---- list

    [Fact]
    public async Task List_filtersByRoomAndActive_orderedByName()
    {
        var world = new TaskWorld();
        var berging = world.Room("Berging");
        var keuken = world.Room("Keuken");
        var a = world.Seed("Berging opruimen", berging.Id);
        world.Seed("Berging vegen", berging.Id, active: false);
        world.Seed("Afwas", keuken.Id);

        var inRoom = await world.Service.ListAsync(berging.Id, null, null, null, Ct);
        var activeOnly = await world.Service.ListAsync(berging.Id, true, null, null, Ct);
        var inactive = await world.Service.ListAsync(null, false, null, null, Ct);
        var all = await world.Service.ListAsync(null, null, null, null, Ct);

        inRoom.AsT0.Items.Select(t => t.Name).Should().Equal("Berging opruimen", "Berging vegen");
        activeOnly.AsT0.Items.Select(t => t.Id).Should().Equal(a.Id);
        inactive.AsT0.Items.Select(t => t.Name).Should().Equal("Berging vegen");
        all.AsT0.Items.Select(t => t.Name).Should().Equal("Afwas", "Berging opruimen", "Berging vegen");
        all.AsT0.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task List_pagesWithACursor_withoutGapsOrRepeats()
    {
        var world = new TaskWorld();
        var room = world.Room("Keuken");
        foreach (var name in new[] { "E", "C", "A", "D", "B" })
        {
            world.Seed(name, room.Id);
        }

        var first = (await world.Service.ListAsync(null, null, 2, null, Ct)).AsT0;
        var second = (await world.Service.ListAsync(null, null, 2, first.NextCursor, Ct)).AsT0;
        var third = (await world.Service.ListAsync(null, null, 2, second.NextCursor, Ct)).AsT0;

        first.Items.Select(t => t.Name).Should().Equal("A", "B");
        second.Items.Select(t => t.Name).Should().Equal("C", "D");
        third.Items.Select(t => t.Name).Should().Equal("E");
        third.NextCursor.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task List_withALimitOutOfRange_isAValidationErrorOnLimit(int limit)
    {
        var world = new TaskWorld();

        var result = await world.Service.ListAsync(null, null, limit, null, Ct);

        result.AsT1.Errors.Should().ContainKey("limit");
    }

    [Fact]
    public async Task List_withABadRoomIdAndCursor_namesBothFields()
    {
        var world = new TaskWorld();

        var result = await world.Service.ListAsync("nope", null, null, "garbage", Ct);

        result.AsT1.Errors.Keys.Should().BeEquivalentTo("roomId", "cursor");
    }

    [Fact]
    public async Task List_whenTheStoreFails_isAPortError()
    {
        var world = new TaskWorld();
        world.TaskStore.Failure = new PortError("db down");

        var result = await world.Service.ListAsync(null, null, null, null, Ct);

        result.AsT2.Message.Should().Be("db down");
    }

    // ---- bulk

    [Fact]
    public async Task Bulk_deactivate_deactivatesEveryActiveTaskOfTheRoom_withOneUpdateEntryEach()
    {
        var world = new TaskWorld();
        var room = world.Room("Schuur");
        var other = world.Room("Tuin");
        var tasks = new[] { world.Seed("Schuur 0", room.Id), world.Seed("Schuur 1", room.Id), world.Seed("Schuur 2", room.Id) };
        var inactive = world.Seed("Schuur oud", room.Id, active: false);
        var elsewhere = world.Seed("Tuin 0", other.Id);

        var result = await world.Service.BulkUpdateRoomAsync(TaskWorld.Planner, room.Id, new BulkRoomChange.Deactivate(), Ct);

        result.AsT0.Should().Be(3);
        world.Audit.Entries.Should().HaveCount(3).And.OnlyContain(e => e.Action == AuditAction.Update && e.Entity == AuditEntity.Task);
        world.Audit.Entries.Select(e => e.EntityId).Should().BeEquivalentTo(tasks.Select(t => t.Id));
        var wasActive = AuditObject.Of(("active", true));
        var isInactive = AuditObject.Of(("active", false));
        world.Audit.Entries.Should().OnlyContain(e => e.Before.Equals(wasActive) && e.After.Equals(isInactive));
        world.TaskStore.Items.Where(t => t.RoomId == room.Id).Should().OnlyContain(t => !t.Active);
        world.TaskStore.Items.Single(t => t.Id == elsewhere.Id).Active.Should().BeTrue();
        world.TaskStore.Items.Single(t => t.Id == inactive.Id).UpdatedAt.Should().Be(inactive.UpdatedAt);
    }

    [Fact]
    public async Task Bulk_reassign_givesEveryActiveTaskTheAssignee_withAnAssignEntryEach()
    {
        var world = new TaskWorld();
        var room = world.Room("Tuin");
        var person = world.Person("Sven");
        world.Seed("Tuin 0", room.Id);
        world.Seed("Tuin 1", room.Id);

        var result = await world.Service.BulkUpdateRoomAsync(TaskWorld.Planner, room.Id, new BulkRoomChange.Reassign(person.Id), Ct);

        result.AsT0.Should().Be(2);
        world.Audit.Entries.Should().HaveCount(2).And.OnlyContain(e => e.Action == AuditAction.Assign);
        var assigned = AuditObject.Of(("defaultAssigneeId", new AuditObjectId(person.Id)));
        world.Audit.Entries.Should().OnlyContain(e => e.After.Equals(assigned));
    }

    [Fact]
    public async Task Bulk_reassign_countsAndAuditsOnlyTheTasksThatChange()
    {
        var world = new TaskWorld();
        var room = world.Room("Tuin");
        var person = world.Person("Sven");
        world.Seed("Tuin 0", room.Id, assignee: person.Id);
        world.Seed("Tuin 1", room.Id);

        var result = await world.Service.BulkUpdateRoomAsync(TaskWorld.Planner, room.Id, new BulkRoomChange.Reassign(person.Id), Ct);

        result.AsT0.Should().Be(1);
        world.Audit.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task Bulk_reassignToNobody_isAllowed()
    {
        var world = new TaskWorld();
        var room = world.Room("Tuin");
        var person = world.Person("Sven");
        world.Seed("Tuin 0", room.Id, assignee: person.Id);

        var result = await world.Service.BulkUpdateRoomAsync(TaskWorld.Planner, room.Id, new BulkRoomChange.Reassign(null), Ct);

        result.AsT0.Should().Be(1);
        world.TaskStore.Items.Single().DefaultAssigneeId.Should().BeNull();
    }

    [Fact]
    public async Task Bulk_inARoomWithoutActiveTasks_changesNothing()
    {
        var world = new TaskWorld();
        var room = world.Room("Tuin");

        var result = await world.Service.BulkUpdateRoomAsync(TaskWorld.Planner, room.Id, new BulkRoomChange.Deactivate(), Ct);

        result.AsT0.Should().Be(0);
        NothingWritten(world);
    }

    [Fact]
    public async Task Bulk_inAnUnknownRoom_isNotFound()
    {
        var world = new TaskWorld();

        var result = await world.Service.BulkUpdateRoomAsync(TaskWorld.Planner, "0123456789abcdef01234567", new BulkRoomChange.Deactivate(), Ct);

        result.IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task Bulk_withABadRoomId_orAssigneeId_isAValidationError()
    {
        var world = new TaskWorld();
        var room = world.Room("Garage");

        var badRoom = await world.Service.BulkUpdateRoomAsync(TaskWorld.Planner, "nope", new BulkRoomChange.Deactivate(), Ct);
        var badAssignee = await world.Service.BulkUpdateRoomAsync(TaskWorld.Planner, room.Id, new BulkRoomChange.Reassign("nope"), Ct);

        badRoom.AsT2.Errors.Should().ContainKey("id");
        badAssignee.AsT2.Errors.Should().ContainKey("defaultAssigneeId");
    }

    [Fact]
    public async Task Bulk_reassign_toAnUnknownOrInactivePerson_isRefused_andNothingChanges()
    {
        var world = new TaskWorld();
        var room = world.Room("Garage");
        var gone = world.Person("Oud", active: false);
        world.Seed("Garage 0", room.Id);

        var unknown = await world.Service.BulkUpdateRoomAsync(TaskWorld.Planner, room.Id, new BulkRoomChange.Reassign("0123456789abcdef01234567"), Ct);
        var inactive = await world.Service.BulkUpdateRoomAsync(TaskWorld.Planner, room.Id, new BulkRoomChange.Reassign(gone.Id), Ct);

        unknown.AsT2.Errors["defaultAssigneeId"].Should().Equal("unknown_user");
        inactive.AsT2.Errors["defaultAssigneeId"].Should().Equal("inactive_user");
        NothingWritten(world);
    }

    [Fact]
    public async Task Bulk_whenAnAuditEntryFails_rollsEveryTaskBack()
    {
        var world = new TaskWorld();
        var room = world.Room("Schuur");
        world.Seed("Schuur 0", room.Id);
        world.Seed("Schuur 1", room.Id);
        world.Audit.Failure = new PortError("audit down");

        var result = await world.Service.BulkUpdateRoomAsync(TaskWorld.Planner, room.Id, new BulkRoomChange.Deactivate(), Ct);

        result.AsT4.Message.Should().Be("audit down");
        world.TaskStore.Items.Should().OnlyContain(t => t.Active);
    }

    private static void NothingWritten(TaskWorld world)
    {
        world.TaskStore.Writes.Should().Be(0);
        world.Audit.Entries.Should().BeEmpty();
    }
}
