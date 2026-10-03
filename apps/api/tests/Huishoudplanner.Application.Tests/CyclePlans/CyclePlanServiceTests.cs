using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Planning;

namespace Huishoudplanner.Application.Tests.CyclePlans;

/// <summary>
/// The cycle plan use cases (cyclePlans.test.ts without activation): every result variant, the audit input and "no write on a no-op".
/// The HTTP behaviour and the stored documents are covered by the integration tests.
/// </summary>
public sealed class CyclePlanServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly AuditActor PlannerActor = new(CyclePlanWorld.Planner.ActorId, AuditSource.Ui);

    private static CyclePlanSlot Slot(string task, int week, int weekday, string? assignee = null) => new(task, week, weekday, assignee);

    // ---- read

    [Fact]
    public async Task List_returns_plans_oldest_first_and_pages_with_a_cursor()
    {
        var world = new CyclePlanWorld();
        var oldest = world.Plan("Standaard", active: true, daysAgo: 3);
        var middle = world.Plan("Zomer", daysAgo: 2);
        var newest = world.Plan("Winter", daysAgo: 1);

        var first = (await world.Service.ListAsync(2, null, Ct)).AsT0;
        var second = (await world.Service.ListAsync(2, first.NextCursor, Ct)).AsT0;

        first.Items.Select(p => p.Id).Should().Equal(oldest.Id, middle.Id);
        first.NextCursor.Should().NotBeNull();
        second.Items.Select(p => p.Id).Should().Equal(newest.Id);
        second.NextCursor.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task List_refuses_a_limit_outside_1_to_200(int limit)
    {
        var world = new CyclePlanWorld();

        var result = await world.Service.ListAsync(limit, null, Ct);

        result.AsT1.Errors.Should().ContainKey("limit");
    }

    [Fact]
    public async Task List_refuses_a_cursor_it_did_not_produce()
    {
        var world = new CyclePlanWorld();

        var result = await world.Service.ListAsync(null, "garbage", Ct);

        result.AsT1.Errors["cursor"].Should().Equal("invalid_cursor");
    }

    [Fact]
    public async Task List_passes_on_a_port_failure()
    {
        var world = new CyclePlanWorld();
        world.Plans.Failure = new PortError("down");

        (await world.Service.ListAsync(null, null, Ct)).AsT2.Message.Should().Be("down");
    }

    [Fact]
    public async Task Get_finds_a_plan_whatever_the_case_of_the_id_and_distinguishes_unknown_from_malformed()
    {
        var world = new CyclePlanWorld();
        var plan = world.Plan("Zomer");

        (await world.Service.GetAsync(plan.Id.ToUpperInvariant(), Ct)).AsT0.Should().Be(plan);
        (await world.Service.GetAsync("0123456789abcdef01234567", Ct)).IsT1.Should().BeTrue();
        (await world.Service.GetAsync("nope", Ct)).AsT2.Errors["id"].Should().Equal("invalid_object_id");
    }

    [Fact]
    public async Task GetActive_is_the_active_plan_or_not_found()
    {
        var world = new CyclePlanWorld();
        (await world.Service.GetActiveAsync(Ct)).IsT1.Should().BeTrue();

        var active = world.Plan("Standaard", active: true);

        (await world.Service.GetActiveAsync(Ct)).AsT0.Should().Be(active);
    }

    // ---- create

    [Fact]
    public async Task Create_makes_an_inactive_empty_manual_plan_and_audits_every_field()
    {
        var world = new CyclePlanWorld();

        var plan = (await world.Service.CreateAsync(CyclePlanWorld.Planner, new CreateCyclePlanCommand("  Leeg  "), Ct)).AsT0;

        plan.Should().BeEquivalentTo(new
        {
            Name = "Leeg",
            Active = false,
            Draft = false,
            Source = "manual",
            ProposalId = (string?)null,
            Discarded = false,
            CreatedAt = CyclePlanWorld.Now,
            UpdatedAt = CyclePlanWorld.Now,
        });
        plan.Slots.Should().BeEmpty();
        plan.WeekThemes.Should().Equal("", "", "", "");
        var entry = world.Audit.Entries.Should().ContainSingle().Subject;
        entry.Should().Be(CyclePlanAudit.ForCreate(PlannerActor, plan));
        entry.Meta.Should().BeNull();
    }

    [Fact]
    public async Task Create_as_a_copy_takes_sorted_slots_and_themes_and_audits_copiedFrom()
    {
        var world = new CyclePlanWorld();
        var task = world.Task("Badkamer", world.Room("Badkamer").Id).Id;
        var source = world.Plan("Standaard", active: true, daysAgo: 2, Slot(task, 1, 1), Slot(task, 0, 1));
        world.Plans.Items[0] = source with { WeekThemes = ["Keuken", "", "Ramen", ""] };

        var plan = (await world.Service.CreateAsync(CyclePlanWorld.Planner, new CreateCyclePlanCommand("Experiment", source.Id.ToUpperInvariant()), Ct)).AsT0;

        plan.Active.Should().BeFalse();
        plan.Slots.Select(s => s.WeekIndex).Should().Equal(0, 1);
        plan.WeekThemes.Should().Equal("Keuken", "", "Ramen", "");
        world.Audit.Entries.Should().ContainSingle().Which.Meta.Should().Be(AuditObject.Of(("copiedFrom", new AuditObjectId(source.Id))));
    }

    [Fact]
    public async Task Create_as_a_copy_of_an_unknown_plan_is_not_found_and_writes_nothing()
    {
        var world = new CyclePlanWorld();

        var result = await world.Service.CreateAsync(CyclePlanWorld.Planner, new CreateCyclePlanCommand("X", "0123456789abcdef01234567"), Ct);

        result.IsT1.Should().BeTrue();
        world.Plans.Writes.Should().Be(0);
        world.Audit.Entries.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_refuses_an_empty_name(string name)
    {
        var world = new CyclePlanWorld();

        var result = await world.Service.CreateAsync(CyclePlanWorld.Planner, new CreateCyclePlanCommand(name), Ct);

        result.AsT2.Errors.Should().ContainKey("name");
        world.Plans.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Create_refuses_a_malformed_copy_source()
    {
        var world = new CyclePlanWorld();

        var result = await world.Service.CreateAsync(CyclePlanWorld.Planner, new CreateCyclePlanCommand("X", "nope"), Ct);

        result.AsT2.Errors["copyFromId"].Should().Equal("invalid_object_id");
    }

    [Fact]
    public async Task Create_rolls_the_plan_back_when_its_audit_entry_fails()
    {
        var world = new CyclePlanWorld();
        world.Audit.Failure = new PortError("audit down");

        var result = await world.Service.CreateAsync(CyclePlanWorld.Planner, new CreateCyclePlanCommand("X"), Ct);

        result.AsT4.Message.Should().Be("audit down");
        world.Plans.Items.Should().BeEmpty();
        world.Transactions.Aborts.Should().Be(1);
    }

    [Fact]
    public async Task Create_passes_on_a_transaction_conflict()
    {
        var world = new CyclePlanWorld();
        world.Transactions.ConflictInsteadOfRunning = new ConflictError("write_conflict", "busy");

        (await world.Service.CreateAsync(CyclePlanWorld.Planner, new CreateCyclePlanCommand("X"), Ct)).AsT3.Code.Should().Be("write_conflict");
    }

    // ---- update

    [Fact]
    public async Task Update_renames_and_sets_themes_with_an_audited_diff_of_only_the_changed_fields()
    {
        var world = new CyclePlanWorld();
        var plan = world.Plan("Leeg");

        var updated = (await world.Service.UpdateAsync(CyclePlanWorld.Planner, plan.Id, new CyclePlanPatch(" Zomer ", ["Keuken", "", "Ramen", ""]), Ct)).AsT0;

        updated.Name.Should().Be("Zomer");
        updated.WeekThemes.Should().Equal("Keuken", "", "Ramen", "");
        updated.UpdatedAt.Should().Be(CyclePlanWorld.Now);
        var entry = world.Audit.Entries.Should().ContainSingle().Subject;
        entry.Action.Should().Be(AuditAction.Update);
        entry.EntityId.Should().Be(plan.Id);
        entry.Before.Should().Be(AuditObject.Of(("name", "Leeg"), ("weekThemes", AuditArray.Of("", "", "", ""))));
        entry.After.Should().Be(AuditObject.Of(("name", "Zomer"), ("weekThemes", AuditArray.Of("Keuken", "", "Ramen", ""))));
    }

    [Fact]
    public async Task Update_with_nothing_new_writes_and_audits_nothing()
    {
        var world = new CyclePlanWorld();
        var plan = world.Plan("Leeg");

        var same = (await world.Service.UpdateAsync(CyclePlanWorld.Planner, plan.Id, new CyclePlanPatch("Leeg", ["", "", "", ""]), Ct)).AsT0;
        var empty = (await world.Service.UpdateAsync(CyclePlanWorld.Planner, plan.Id, new CyclePlanPatch(), Ct)).AsT0;

        same.Should().Be(plan);
        empty.Should().Be(plan);
        world.Plans.Writes.Should().Be(0);
        world.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_of_an_unknown_plan_is_not_found_and_of_a_bad_patch_a_validation_error()
    {
        var world = new CyclePlanWorld();

        (await world.Service.UpdateAsync(CyclePlanWorld.Planner, "0123456789abcdef01234567", new CyclePlanPatch("X"), Ct)).IsT1.Should().BeTrue();
        (await world.Service.UpdateAsync(CyclePlanWorld.Planner, "0123456789abcdef01234567", new CyclePlanPatch(WeekThemes: ["a", "b"]), Ct)).AsT2.Errors.Should().ContainKey("weekThemes");
        (await world.Service.UpdateAsync(CyclePlanWorld.Planner, "nope", new CyclePlanPatch("X"), Ct)).AsT2.Errors.Should().ContainKey("id");
    }

    // ---- delete

    [Fact]
    public async Task Delete_removes_a_plan_that_is_neither_default_nor_active_and_audits_the_removed_plan()
    {
        var world = new CyclePlanWorld();
        world.Plan("Standaard", active: true, daysAgo: 3);
        var temporary = world.Plan("Tijdelijk", daysAgo: 1);

        var result = await world.Service.DeleteAsync(CyclePlanWorld.Planner, temporary.Id, Ct);

        result.IsT0.Should().BeTrue();
        world.Plans.Items.Should().NotContain(p => p.Id == temporary.Id);
        world.Audit.Entries.Should().ContainSingle().Which.Should().Be(CyclePlanAudit.ForDelete(PlannerActor, temporary));
    }

    [Fact]
    public async Task Delete_of_the_oldest_plan_is_default_plan_even_when_it_is_not_active()
    {
        var world = new CyclePlanWorld();
        var oldest = world.Plan("Standaard", active: false, daysAgo: 3);
        world.Plan("Actief", active: true, daysAgo: 1);

        var result = await world.Service.DeleteAsync(CyclePlanWorld.Planner, oldest.Id, Ct);

        result.AsT3.Code.Should().Be("default_plan");
        world.Plans.Writes.Should().Be(0);
        world.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_of_the_default_plan_that_is_also_active_is_default_plan_first()
    {
        var world = new CyclePlanWorld();
        var oldest = world.Plan("Standaard", active: true, daysAgo: 3);

        (await world.Service.DeleteAsync(CyclePlanWorld.Planner, oldest.Id, Ct)).AsT3.Code.Should().Be("default_plan");
    }

    [Fact]
    public async Task Delete_of_the_active_plan_is_active_plan()
    {
        var world = new CyclePlanWorld();
        world.Plan("Standaard", daysAgo: 3);
        var active = world.Plan("Actief", active: true, daysAgo: 1);

        (await world.Service.DeleteAsync(CyclePlanWorld.Planner, active.Id, Ct)).AsT3.Code.Should().Be("active_plan");
        world.Plans.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Delete_of_an_unknown_plan_is_not_found_and_of_a_malformed_id_a_validation_error()
    {
        var world = new CyclePlanWorld();

        (await world.Service.DeleteAsync(CyclePlanWorld.Planner, "0123456789abcdef01234567", Ct)).IsT1.Should().BeTrue();
        (await world.Service.DeleteAsync(CyclePlanWorld.Planner, "x", Ct)).AsT2.Errors.Should().ContainKey("id");
    }

    [Fact]
    public async Task Delete_rolls_the_removal_back_when_its_audit_entry_fails()
    {
        var world = new CyclePlanWorld();
        world.Plan("Standaard", active: true, daysAgo: 3);
        var temporary = world.Plan("Tijdelijk", daysAgo: 1);
        world.Audit.Failure = new PortError("audit down");

        var result = await world.Service.DeleteAsync(CyclePlanWorld.Planner, temporary.Id, Ct);

        result.AsT4.Message.Should().Be("audit down");
        world.Plans.Items.Should().Contain(p => p.Id == temporary.Id);
    }

    // ---- slots

    private static (CyclePlanWorld World, CyclePlan Plan, string Weekly, string Twice, string Anna, string Bram) Slotted()
    {
        var world = new CyclePlanWorld();
        var room = world.Room("Badkamer");
        var weekly = world.Task("Badkamer schoonmaken", room.Id, "1w", 30).Id;
        var twice = world.Task("Wastafel poetsen", room.Id, "2w", 10).Id;
        var anna = world.Person("Persoon 1").Id;
        var bram = world.Person("Persoon 2", unavailable: [2]).Id;
        world.Plan("Standaard", active: true, daysAgo: 3);
        var plan = world.Plan("Slots", daysAgo: 1);
        return (world, plan, weekly, twice, anna, bram);
    }

    [Fact]
    public async Task ReplaceSlots_saves_a_valid_plan_with_warnings_and_summary_and_audits_only_added_slots()
    {
        var (world, plan, weekly, twice, anna, _) = Slotted();

        var saved = (await world.Service.ReplaceSlotsAsync(CyclePlanWorld.Planner, plan.Id, [Slot(weekly, 0, 1, anna), Slot(twice, 0, 3)], Ct)).AsT0;

        saved.Plan.Slots.Should().HaveCount(2);
        saved.Plan.UpdatedAt.Should().Be(CyclePlanWorld.Now);
        saved.Warnings.Should().Contain(new PlanIssue(PlanWarningCodes.IntervalMismatch, TaskId: weekly, Placed: 1, Required: 4));
        saved.Warnings.Should().Contain(new PlanIssue(PlanWarningCodes.IntervalMismatch, TaskId: twice, Placed: 1, Required: 8));
        saved.Summary.Tasks.Should().Contain(new TaskSummary(weekly, 1, 4));
        var entry = world.Audit.Entries.Should().ContainSingle().Subject;
        entry.Before.Should().Be(AuditObject.Of(("slots", AuditArray.Of())));
        ((AuditArray)entry.After["slots"]!).Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReplaceSlots_audits_only_added_removed_and_changed_slots()
    {
        var (world, plan, weekly, twice, anna, bram) = Slotted();
        await world.Service.ReplaceSlotsAsync(CyclePlanWorld.Planner, plan.Id, [Slot(weekly, 0, 1, anna), Slot(twice, 0, 3)], Ct);
        world.Audit.Entries.Clear();

        var result = await world.Service.ReplaceSlotsAsync(
            CyclePlanWorld.Planner, plan.Id, [Slot(weekly, 0, 1, bram), Slot(twice, 1, 5)], Ct);

        result.IsT0.Should().BeTrue();
        var entry = world.Audit.Entries.Should().ContainSingle().Subject;
        Positions(entry.Before).Should().Equal((0, 3), (0, 1));
        Positions(entry.After).Should().Equal((1, 5), (0, 1));
    }

    private static IEnumerable<(int, int)> Positions(AuditObject side) =>
        ((AuditArray)side["slots"]!).Items.Cast<AuditObject>()
            .Select(s => ((int)((AuditInteger)s["weekIndex"]!).Value, (int)((AuditInteger)s["weekday"]!).Value));

    [Fact]
    public async Task ReplaceSlots_with_a_hard_error_is_an_invalid_plan_and_writes_nothing()
    {
        var (world, plan, weekly, _, _, bram) = Slotted();

        var result = await world.Service.ReplaceSlotsAsync(CyclePlanWorld.Planner, plan.Id, [Slot(weekly, 2, 2, bram)], Ct);

        var invalid = result.AsT3;
        invalid.Validation.Errors.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Code = PlanErrorCodes.AssigneeUnavailable, SlotIndex = 0 });
        world.Plans.Writes.Should().Be(0);
        world.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task ReplaceSlots_refuses_the_same_task_twice_on_a_day()
    {
        var (world, plan, _, twice, anna, bram) = Slotted();

        var result = await world.Service.ReplaceSlotsAsync(CyclePlanWorld.Planner, plan.Id, [Slot(twice, 3, 4, anna), Slot(twice, 3, 4, bram)], Ct);

        result.AsT3.Validation.Errors.Select(e => e.Code).Should().Equal(PlanErrorCodes.DuplicateTaskDay);
    }

    [Fact]
    public async Task ReplaceSlots_refuses_an_unknown_or_inactive_task_and_person()
    {
        var (world, plan, weekly, _, anna, _) = Slotted();
        var inactiveTask = world.Task("Oud", world.Room("Hal").Id, active: false).Id;
        var inactivePerson = world.Person("Oud", active: false).Id;

        var result = await world.Service.ReplaceSlotsAsync(
            CyclePlanWorld.Planner,
            plan.Id,
            [Slot("aaaaaaaaaaaaaaaaaaaaaaaa", 0, 1), Slot(inactiveTask, 0, 2), Slot(weekly, 0, 3, "bbbbbbbbbbbbbbbbbbbbbbbb"), Slot(weekly, 0, 4, inactivePerson), Slot(weekly, 0, 5, anna)],
            Ct);

        result.AsT3.Validation.Errors.Select(e => e.Code).Should().Equal(
            PlanErrorCodes.UnknownTask, PlanErrorCodes.InactiveTask, PlanErrorCodes.UnknownUser, PlanErrorCodes.InactiveUser);
    }

    [Fact]
    public async Task ReplaceSlots_of_what_is_stored_writes_and_audits_nothing_but_still_answers_with_the_validation()
    {
        var (world, plan, weekly, _, anna, _) = Slotted();
        await world.Service.ReplaceSlotsAsync(CyclePlanWorld.Planner, plan.Id, [Slot(weekly, 0, 1, anna)], Ct);
        var writes = world.Plans.Writes;
        world.Audit.Entries.Clear();

        var again = (await world.Service.ReplaceSlotsAsync(CyclePlanWorld.Planner, plan.Id, [Slot(weekly, 0, 1, anna)], Ct)).AsT0;

        world.Plans.Writes.Should().Be(writes);
        world.Audit.Entries.Should().BeEmpty();
        again.Warnings.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ReplaceSlots_sorts_the_stored_slots_and_lowercases_ids()
    {
        var (world, plan, weekly, twice, anna, _) = Slotted();

        var saved = (await world.Service.ReplaceSlotsAsync(
            CyclePlanWorld.Planner,
            plan.Id.ToUpperInvariant(),
            [Slot(twice.ToUpperInvariant(), 1, 1), Slot(weekly, 0, 0, anna.ToUpperInvariant()), Slot(weekly, 0, 1)],
            Ct)).AsT0;

        saved.Plan.Slots.Select(s => (s.WeekIndex, s.Weekday)).Should().Equal((0, 1), (0, 0), (1, 1));
        saved.Plan.Slots[1].AssigneeId.Should().Be(anna);
        saved.Plan.Slots[2].TaskId.Should().Be(twice);
    }

    [Fact]
    public async Task ReplaceSlots_of_an_unknown_plan_is_not_found_before_the_validation_runs()
    {
        var (world, _, weekly, _, _, bram) = Slotted();

        var result = await world.Service.ReplaceSlotsAsync(CyclePlanWorld.Planner, "0123456789abcdef01234567", [Slot(weekly, 2, 2, bram)], Ct);

        result.IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task ReplaceSlots_refuses_a_malformed_id_a_bad_task_id_and_a_position_out_of_range_as_validation_errors()
    {
        var (world, plan, weekly, _, _, _) = Slotted();

        (await world.Service.ReplaceSlotsAsync(CyclePlanWorld.Planner, "x", [], Ct)).AsT2.Errors.Should().ContainKey("id");
        (await world.Service.ReplaceSlotsAsync(CyclePlanWorld.Planner, plan.Id, [Slot("nope", 0, 1)], Ct)).AsT2.Errors.Should().ContainKey("slots[0].taskId");
        (await world.Service.ReplaceSlotsAsync(CyclePlanWorld.Planner, plan.Id, [Slot(weekly, 4, 1)], Ct)).AsT2.Errors.Should().ContainKey("slots[0].weekIndex");
        (await world.Service.ReplaceSlotsAsync(CyclePlanWorld.Planner, plan.Id, [Slot(weekly, 0, 7)], Ct)).AsT2.Errors.Should().ContainKey("slots[0].weekday");
        world.Plans.Writes.Should().Be(0);
    }

    [Fact]
    public async Task ReplaceSlots_sees_every_task_even_when_there_are_more_than_one_page()
    {
        var (world, plan, _, _, _, _) = Slotted();
        var room = world.Room("Hal").Id;
        for (var i = 0; i < 230; i++)
        {
            world.Task($"Taak {i:000}", room);
        }

        var last = world.Task("Zzz laatste", room).Id;

        var result = await world.Service.ReplaceSlotsAsync(CyclePlanWorld.Planner, plan.Id, [Slot(last, 0, 1)], Ct);

        result.IsT0.Should().BeTrue();
    }

    [Fact]
    public async Task ReplaceSlots_rolls_back_when_its_audit_entry_fails()
    {
        var (world, plan, weekly, _, anna, _) = Slotted();
        world.Audit.Failure = new PortError("audit down");

        var result = await world.Service.ReplaceSlotsAsync(CyclePlanWorld.Planner, plan.Id, [Slot(weekly, 0, 1, anna)], Ct);

        result.AsT5.Message.Should().Be("audit down");
        world.Plans.Items.Single(p => p.Id == plan.Id).Slots.Should().BeEmpty();
    }

    // ---- compare

    [Fact]
    public async Task Compare_shows_the_slot_differences_with_the_active_plan_and_the_weekly_minutes_before_and_after()
    {
        var (world, plan, weekly, twice, anna, bram) = Slotted();
        var active = world.Plans.Items.Single(p => p.Active);
        world.Plans.Items[world.Plans.Items.IndexOf(active)] = active with { Slots = [Slot(weekly, 0, 1, anna), Slot(twice, 0, 3)] };
        world.Plans.Items[world.Plans.Items.IndexOf(plan)] = plan with { Slots = [Slot(weekly, 0, 1, bram), Slot(twice, 1, 5)] };

        var comparison = (await world.Service.CompareWithActiveAsync(plan.Id, Ct)).AsT0;

        comparison.PlanId.Should().Be(plan.Id);
        comparison.AgainstPlanId.Should().Be(active.Id);
        comparison.Diff.Moved.Select(m => m.TaskId).Should().Equal(weekly, twice);
        comparison.Diff.Moved[0].TaskName.Should().Be("Badkamer schoonmaken");
        comparison.Diff.Moved[0].RoomName.Should().Be("Badkamer");
        comparison.Diff.Moved[0].DurationMinutes.Should().Be(30);
        comparison.Before[0].Users.Single(u => u.UserId == anna).Minutes.Should().Be(30);
        comparison.After[0].Users.Single(u => u.UserId == bram).Minutes.Should().Be(30);
        comparison.Warnings.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Compare_without_an_active_plan_has_no_base_and_everything_is_added()
    {
        var (world, plan, weekly, _, anna, _) = Slotted();
        world.Plans.Items.RemoveAll(p => p.Active);
        world.Plans.Items[world.Plans.Items.IndexOf(plan)] = plan with { Slots = [Slot(weekly, 0, 1, anna)] };

        var comparison = (await world.Service.CompareWithActiveAsync(plan.Id, Ct)).AsT0;

        comparison.AgainstPlanId.Should().BeNull();
        comparison.Diff.Added.Should().ContainSingle();
    }

    [Fact]
    public async Task Compare_of_an_unknown_plan_is_not_found()
    {
        var world = new CyclePlanWorld();

        (await world.Service.CompareWithActiveAsync("0123456789abcdef01234567", Ct)).IsT1.Should().BeTrue();
        (await world.Service.CompareWithActiveAsync("x", Ct)).IsT2.Should().BeTrue();
    }

    // ---- validate

    [Fact]
    public async Task Validate_of_a_stored_plan_runs_the_rules_of_a_save_and_writes_nothing()
    {
        var (world, plan, weekly, _, _, bram) = Slotted();
        world.Plans.Items[world.Plans.Items.IndexOf(plan)] = plan with { Slots = [Slot(weekly, 2, 2, bram)] };

        var validation = (await world.Service.ValidateAsync(plan.Id, Ct)).AsT0;

        validation.IsValid.Should().BeFalse();
        validation.Errors.Single().Code.Should().Be(PlanErrorCodes.AssigneeUnavailable);
        validation.ToValidationErrors().Errors.Keys.Should().Equal("slots[0].assigneeId");
        world.Plans.Writes.Should().Be(0);
        world.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_of_an_unknown_plan_is_not_found()
    {
        var world = new CyclePlanWorld();

        (await world.Service.ValidateAsync("0123456789abcdef01234567", Ct)).IsT1.Should().BeTrue();
        (await world.Service.ValidateAsync("x", Ct)).IsT2.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateDraft_returns_the_same_result_a_save_of_those_slots_would()
    {
        var (world, plan, weekly, twice, anna, bram) = Slotted();
        var slots = new[] { Slot(weekly, 0, 1, anna), Slot(twice, 3, 4, bram), Slot(twice, 3, 4, anna), Slot(weekly, 2, 2, bram) };

        var draft = (await world.Service.ValidateDraftAsync(slots, Ct)).AsT0;
        var save = (await world.Service.ReplaceSlotsAsync(CyclePlanWorld.Planner, plan.Id, slots, Ct)).AsT3;

        draft.Should().BeEquivalentTo(save.Validation);
        draft.Errors.Select(e => e.Code).Should().Equal(PlanErrorCodes.DuplicateTaskDay, PlanErrorCodes.AssigneeUnavailable);
    }

    [Fact]
    public async Task ValidateDraft_of_a_valid_draft_has_a_summary_and_no_errors_and_writes_nothing()
    {
        var (world, _, weekly, _, anna, _) = Slotted();

        var draft = (await world.Service.ValidateDraftAsync([Slot(weekly, 0, 1, anna)], Ct)).AsT0;

        draft.IsValid.Should().BeTrue();
        draft.Summary.Weeks.Should().HaveCount(4);
        world.Plans.Writes.Should().Be(0);
    }

    [Fact]
    public async Task ValidateDraft_refuses_malformed_slots_like_a_save()
    {
        var world = new CyclePlanWorld();

        (await world.Service.ValidateDraftAsync([Slot("nope", 0, 1)], Ct)).AsT1.Errors.Should().ContainKey("slots[0].taskId");
    }

    [Fact]
    public async Task Validate_passes_on_a_port_failure_and_treats_missing_settings_as_no_intervals()
    {
        var (world, _, weekly, _, anna, _) = Slotted();
        world.Settings.Document = null;

        var draft = (await world.Service.ValidateDraftAsync([Slot(weekly, 0, 1, anna)], Ct)).AsT0;

        draft.Warnings.Should().BeEmpty();
        draft.Summary.Tasks.Should().Contain(new TaskSummary(weekly, 1, null));
        world.Tasks.Failure = new PortError("down");
        (await world.Service.ValidateDraftAsync([], Ct)).AsT2.Message.Should().Be("down");
    }

    // ---- seed

    [Fact]
    public async Task Seed_creates_the_empty_active_plan_standaard_as_the_system_on_a_first_start()
    {
        var world = new CyclePlanWorld();

        var created = (await world.Seed.SeedAsync(Ct)).AsT0;

        created.Should().BeTrue();
        var plan = world.Plans.Items.Should().ContainSingle().Subject;
        plan.Should().BeEquivalentTo(new { Name = "Standaard", Active = true, Draft = false, Source = "manual" });
        plan.Slots.Should().BeEmpty();
        plan.WeekThemes.Should().Equal("", "", "", "");
        world.Audit.Entries.Should().ContainSingle().Which.Should().Be(CyclePlanAudit.ForCreate(AuditActor.System, plan));
    }

    [Fact]
    public async Task Seed_does_nothing_when_plans_exist()
    {
        var world = new CyclePlanWorld();
        world.Plan("Zomer");

        var created = (await world.Seed.SeedAsync(Ct)).AsT0;

        created.Should().BeFalse();
        world.Plans.Writes.Should().Be(0);
        world.Audit.Entries.Should().BeEmpty();
    }
}
