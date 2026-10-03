using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Rooms;

namespace Huishoudplanner.Application.Tests.Rooms;

/// <summary>The room use cases: every result variant, the audit input and "no write on a no-op".</summary>
public sealed class RoomServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuditObject Fields(string name, int sortOrder, bool active, bool isVirtual) =>
        AuditObject.Of(("name", name), ("sortOrder", sortOrder), ("active", active), ("virtual", isVirtual));

    // ---- create

    [Fact]
    public async Task Create_withoutSortOrder_goesTenPlacesAfterTheLastRoom_andIsAuditedOnce()
    {
        var world = new RoomWorld();
        world.Seed("Keuken", 10);
        world.Seed("Hele huis", 70, isVirtual: true);

        var result = await world.Service.CreateAsync(RoomWorld.Admin, new CreateRoomCommand("Zolder"), Ct);

        var room = result.AsT0;
        room.Should().BeEquivalentTo(new { Name = "Zolder", SortOrder = 80, Active = true, Virtual = false, CreatedAt = RoomWorld.Now, UpdatedAt = RoomWorld.Now });
        room.Id.Should().HaveLength(24);
        world.Audit.Entries.Should().ContainSingle().Which.Should().Be(new AuditEntry(
            new AuditActor(RoomWorld.Admin.ActorId, AuditSource.Ui),
            AuditEntity.Room,
            room.Id,
            AuditAction.Create,
            AuditObject.Empty,
            Fields("Zolder", 80, true, false)));
    }

    [Fact]
    public async Task Create_inAnEmptyHouse_startsAtTen()
    {
        var world = new RoomWorld();

        var result = await world.Service.CreateAsync(RoomWorld.Admin, new CreateRoomCommand("Keuken"), Ct);

        result.AsT0.SortOrder.Should().Be(10);
    }

    [Fact]
    public async Task Create_withASortOrderAndTheVirtualFlag_usesThem_andTrimsTheName()
    {
        var world = new RoomWorld();
        world.Seed("Keuken", 10);

        var result = await world.Service.CreateAsync(RoomWorld.Admin, new CreateRoomCommand("  Hele huis  ", 5, true), Ct);

        result.AsT0.Should().BeEquivalentTo(new { Name = "Hele huis", SortOrder = 5, Virtual = true });
        world.Audit.Entries.Single().After.Should().Be(Fields("Hele huis", 5, true, true));
    }

    [Fact]
    public async Task Create_nearTheLargestSortOrder_doesNotOverflow()
    {
        var world = new RoomWorld();
        world.Seed("Keuken", int.MaxValue - 3);

        var result = await world.Service.CreateAsync(RoomWorld.Admin, new CreateRoomCommand("Zolder"), Ct);

        result.AsT0.SortOrder.Should().Be(int.MaxValue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_withABlankName_isAValidationErrorOnName_andWritesNothing(string name)
    {
        var world = new RoomWorld();

        var result = await world.Service.CreateAsync(RoomWorld.Admin, new CreateRoomCommand(name), Ct);

        result.AsT1.Errors.Should().ContainKey("name");
        world.Rooms.Writes.Should().Be(0);
        world.Audit.Entries.Should().BeEmpty();
        world.Transactions.Runs.Should().Be(0);
    }

    [Fact]
    public async Task Create_whenTheAuditEntryCannotBeWritten_rollsTheRoomBack_andIsAPortError()
    {
        var world = new RoomWorld();
        world.Audit.Failure = new PortError("audit down");

        var result = await world.Service.CreateAsync(RoomWorld.Admin, new CreateRoomCommand("Zolder"), Ct);

        result.AsT3.Message.Should().Be("audit down");
        world.Rooms.Items.Should().BeEmpty();
        world.Transactions.Aborts.Should().Be(1);
    }

    [Fact]
    public async Task Create_whenTheStoreFails_isAPortError_andWritesNoAuditEntry()
    {
        var world = new RoomWorld { Rooms = { FailWrites = true } };

        var result = await world.Service.CreateAsync(RoomWorld.Admin, new CreateRoomCommand("Zolder"), Ct);

        result.IsT3.Should().BeTrue();
        world.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_whenConcurrentWritersKeepWinning_isAConflict()
    {
        var world = new RoomWorld();
        world.Transactions.ConflictInsteadOfRunning = new ConflictError("write_conflict", "busy");

        var result = await world.Service.CreateAsync(RoomWorld.Admin, new CreateRoomCommand("Zolder"), Ct);

        result.AsT2.Code.Should().Be("write_conflict");
    }

    // ---- update

    [Fact]
    public async Task Update_renamesAndDeactivates_withTheChangedFieldsBeforeAndAfter()
    {
        var world = new RoomWorld();
        var hal = world.Seed("Hal", 60);
        world.Clock.Now = RoomWorld.Now.AddHours(1);

        var result = await world.Service.UpdateAsync(RoomWorld.Admin, hal.Id, new RoomPatch(Name: "Gang", Active: false), Ct);

        result.AsT0.Should().BeEquivalentTo(new { Name = "Gang", Active = false, SortOrder = 60, UpdatedAt = RoomWorld.Now.AddHours(1), CreatedAt = hal.CreatedAt });
        world.Audit.Entries.Should().ContainSingle().Which.Should().Be(new AuditEntry(
            new AuditActor(RoomWorld.Admin.ActorId, AuditSource.Ui),
            AuditEntity.Room,
            hal.Id,
            AuditAction.Update,
            AuditObject.Of(("name", "Hal"), ("active", true)),
            AuditObject.Of(("name", "Gang"), ("active", false))));
    }

    [Fact]
    public async Task Update_auditsOnlyTheFieldsThatReallyChange()
    {
        var world = new RoomWorld();
        var hal = world.Seed("Hal", 60);

        await world.Service.UpdateAsync(RoomWorld.Admin, hal.Id, new RoomPatch(Name: "Hal", SortOrder: 65, Active: true), Ct);

        var entry = world.Audit.Entries.Single();
        entry.Before.Should().Be(AuditObject.Of(("sortOrder", 60)));
        entry.After.Should().Be(AuditObject.Of(("sortOrder", 65)));
        world.Rooms.Items.Single().Name.Should().Be("Hal");
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData("Hal", null, null, null)]
    [InlineData("  Hal  ", 60, true, false)]
    public async Task Update_thatChangesNothing_writesAndAuditsNothing_andReturnsTheRoomAsItIs(string? name, int? sortOrder, bool? active, bool? isVirtual)
    {
        var world = new RoomWorld();
        var hal = world.Seed("Hal", 60);
        world.Clock.Now = RoomWorld.Now.AddHours(1);

        var result = await world.Service.UpdateAsync(RoomWorld.Admin, hal.Id, new RoomPatch(name, sortOrder, active, isVirtual), Ct);

        result.AsT0.Should().Be(hal);
        world.Rooms.Writes.Should().Be(0);
        world.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_ofAnUnknownRoom_isNotFound_andWritesNothing()
    {
        var world = new RoomWorld();

        var result = await world.Service.UpdateAsync(RoomWorld.Admin, "0123456789abcdef01234567", new RoomPatch(Name: "X"), Ct);

        result.IsT1.Should().BeTrue();
        world.Audit.Entries.Should().BeEmpty();
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("0123456789abcdef0123456")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task Update_withAMalformedId_isAValidationErrorOnId(string id)
    {
        var world = new RoomWorld();

        var result = await world.Service.UpdateAsync(RoomWorld.Admin, id, new RoomPatch(Name: "X"), Ct);

        result.AsT2.Errors.Should().ContainKey("id");
    }

    [Fact]
    public async Task Update_withABlankName_isAValidationErrorOnName_andWritesNothing()
    {
        var world = new RoomWorld();
        var hal = world.Seed("Hal", 60);

        var result = await world.Service.UpdateAsync(RoomWorld.Admin, hal.Id, new RoomPatch(Name: "  "), Ct);

        result.AsT2.Errors.Should().ContainKey("name");
        world.Rooms.Writes.Should().Be(0);
        world.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_whenTheAuditEntryCannotBeWritten_rollsTheChangeBack()
    {
        var world = new RoomWorld();
        var hal = world.Seed("Hal", 60);
        world.Audit.Failure = new PortError("audit down");

        var result = await world.Service.UpdateAsync(RoomWorld.Admin, hal.Id, new RoomPatch(Name: "Gang"), Ct);

        result.AsT4.Message.Should().Be("audit down");
        world.Rooms.Items.Single().Should().Be(hal);
    }

    [Fact]
    public async Task Update_whenTheReadFails_isAPortError()
    {
        var world = new RoomWorld();
        var hal = world.Seed("Hal", 60);
        world.Rooms.Failure = new PortError("db down");

        var result = await world.Service.UpdateAsync(RoomWorld.Admin, hal.Id, new RoomPatch(Name: "Gang"), Ct);

        result.AsT4.Message.Should().Be("db down");
    }

    // ---- delete

    [Fact]
    public async Task Delete_ofAnEmptyRoom_removesIt_andAuditsTheDeletedFields()
    {
        var world = new RoomWorld();
        var empty = world.Seed("Lege kamer", 80);

        var result = await world.Service.DeleteAsync(RoomWorld.Admin, empty.Id, Ct);

        result.IsT0.Should().BeTrue();
        world.Rooms.Items.Should().BeEmpty();
        world.Audit.Entries.Should().ContainSingle().Which.Should().Be(new AuditEntry(
            new AuditActor(RoomWorld.Admin.ActorId, AuditSource.Ui),
            AuditEntity.Room,
            empty.Id,
            AuditAction.Delete,
            Fields("Lege kamer", 80, true, false),
            AuditObject.Empty));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public async Task Delete_ofARoomThatTasksUse_isRoomInUseWithTheCount_andChangesNothing(int taskCount)
    {
        var world = new RoomWorld();
        var used = world.Seed("Gebruikte kamer", 80);
        world.Usage.TasksPerRoom[used.Id] = taskCount;

        var result = await world.Service.DeleteAsync(RoomWorld.Admin, used.Id, Ct);

        result.AsT3.Should().Be(new RoomInUse(taskCount));
        world.Rooms.Items.Should().ContainSingle();
        world.Audit.Entries.Should().BeEmpty();
        world.Rooms.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Delete_ofAnUnknownRoom_isNotFound_beforeTheUsageIsChecked()
    {
        var world = new RoomWorld();
        world.Usage.Failure = new PortError("must not be asked");

        var result = await world.Service.DeleteAsync(RoomWorld.Admin, "0123456789abcdef01234567", Ct);

        result.IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_withAMalformedId_isAValidationErrorOnId()
    {
        var world = new RoomWorld();

        var result = await world.Service.DeleteAsync(RoomWorld.Admin, "nope", Ct);

        result.AsT2.Errors.Should().ContainKey("id");
    }

    [Fact]
    public async Task Delete_whenTheUsageCheckFails_isAPortError_andKeepsTheRoom()
    {
        var world = new RoomWorld();
        var room = world.Seed("Kamer", 10);
        world.Usage.Failure = new PortError("tasks unreadable");

        var result = await world.Service.DeleteAsync(RoomWorld.Admin, room.Id, Ct);

        result.AsT5.Message.Should().Be("tasks unreadable");
        world.Rooms.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Delete_whenTheAuditEntryCannotBeWritten_keepsTheRoom()
    {
        var world = new RoomWorld();
        var room = world.Seed("Kamer", 10);
        world.Audit.Failure = new PortError("audit down");

        var result = await world.Service.DeleteAsync(RoomWorld.Admin, room.Id, Ct);

        result.AsT5.Message.Should().Be("audit down");
        world.Rooms.Items.Should().ContainSingle();
    }

    // ---- list

    [Fact]
    public async Task List_returnsRoomsInSortOrder_withoutACursorOnTheLastPage()
    {
        var world = new RoomWorld();
        world.Seed("Hele huis", 70, isVirtual: true);
        world.Seed("Keuken", 10);
        world.Seed("Badkamer", 20);

        var result = await world.Service.ListAsync(null, null, null, Ct);

        var list = result.AsT0;
        list.Items.Select(r => r.Name).Should().Equal("Keuken", "Badkamer", "Hele huis");
        list.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task List_filtersOnTheActiveFlag()
    {
        var world = new RoomWorld();
        world.Seed("Keuken", 10);
        world.Seed("Gang", 20, active: false);

        (await world.Service.ListAsync(true, null, null, Ct)).AsT0.Items.Select(r => r.Name).Should().Equal("Keuken");
        (await world.Service.ListAsync(false, null, null, Ct)).AsT0.Items.Select(r => r.Name).Should().Equal("Gang");
    }

    [Fact]
    public async Task List_pagesWithAnOpaqueCursor_untilTheLastPage()
    {
        var world = new RoomWorld();
        world.Seed("A", 10);
        world.Seed("B", 20);
        world.Seed("C", 30);

        var first = (await world.Service.ListAsync(null, 2, null, Ct)).AsT0;
        var second = (await world.Service.ListAsync(null, 2, first.NextCursor, Ct)).AsT0;

        first.Items.Select(r => r.Name).Should().Equal("A", "B");
        first.NextCursor.Should().NotBeNullOrEmpty();
        second.Items.Select(r => r.Name).Should().Equal("C");
        second.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task List_whenThePageIsExactlyFull_hasNoNextCursor()
    {
        var world = new RoomWorld();
        world.Seed("A", 10);
        world.Seed("B", 20);

        var page = (await world.Service.ListAsync(null, 2, null, Ct)).AsT0;

        page.NextCursor.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public async Task List_withALimitOutsideOneToTwoHundred_isAValidationErrorOnLimit(int limit)
    {
        var world = new RoomWorld();

        var result = await world.Service.ListAsync(null, limit, null, Ct);

        result.AsT1.Errors.Should().ContainKey("limit");
    }

    [Fact]
    public async Task List_withAMalformedCursor_isAValidationErrorOnCursor()
    {
        var world = new RoomWorld();

        var result = await world.Service.ListAsync(null, null, "not-a-cursor", Ct);

        result.AsT1.Errors["cursor"].Should().Contain("invalid_cursor");
    }

    [Fact]
    public async Task List_whenTheStoreFails_isAPortError()
    {
        var world = new RoomWorld { Rooms = { Failure = new PortError("db down") } };

        var result = await world.Service.ListAsync(null, null, null, Ct);

        result.AsT2.Message.Should().Be("db down");
    }
}
