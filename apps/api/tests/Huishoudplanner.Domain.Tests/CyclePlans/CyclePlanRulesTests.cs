using Huishoudplanner.Domain.CyclePlans;

namespace Huishoudplanner.Domain.Tests.CyclePlans;

/// <summary>The schemas of <c>cyclePlans.ts</c> (<c>createCyclePlanInputSchema</c>, <c>updateCyclePlanInputSchema</c>, <c>slotSchema</c>), the default plan guard and the cursor.</summary>
public sealed class CyclePlanRulesTests
{
    private const string Id = "0123456789abcdef01234567";

    [Fact]
    public void A_create_command_needs_a_name_that_is_not_empty_after_trimming()
    {
        CyclePlanRules.Validate(new CreateCyclePlanCommand("  ")).Should().NotBeNull()
            .And.Subject.As<Huishoudplanner.Domain.Errors.ValidationErrors>().Errors.Keys.Should().Equal("name");
        CyclePlanRules.Validate(new CreateCyclePlanCommand("Zomer")).Should().BeNull();
    }

    [Fact]
    public void A_copy_source_must_look_like_an_id()
    {
        CyclePlanRules.Validate(new CreateCyclePlanCommand("X", "nope"))!.Errors["copyFromId"].Should().Equal("invalid_object_id");
        CyclePlanRules.Validate(new CreateCyclePlanCommand("X", Id.ToUpperInvariant())).Should().BeNull();
    }

    [Fact]
    public void A_patch_needs_exactly_four_week_themes_and_a_name_that_is_not_empty()
    {
        CyclePlanRules.Validate(new CyclePlanPatch(WeekThemes: ["a", "b"]))!.Errors.Keys.Should().Equal("weekThemes");
        CyclePlanRules.Validate(new CyclePlanPatch(Name: "")).Should().NotBeNull();
        CyclePlanRules.Validate(new CyclePlanPatch(Name: "Zomer", WeekThemes: ["a", "", "c", ""])).Should().BeNull();
        CyclePlanRules.Validate(new CyclePlanPatch()).Should().BeNull();
    }

    [Fact]
    public void Slots_are_checked_for_id_shape_and_position_range_and_keyed_by_path()
    {
        var errors = CyclePlanRules.Validate(
        [
            new CyclePlanSlot("nope", 0, 1, null),
            new CyclePlanSlot(Id, 4, 7, "x"),
            new CyclePlanSlot(Id, 3, 6, Id),
        ])!.Errors;

        errors.Keys.Should().BeEquivalentTo("slots[0].taskId", "slots[1].weekIndex", "slots[1].weekday", "slots[1].assigneeId");
    }

    [Fact]
    public void Normalise_lowercases_the_ids()
    {
        var slots = CyclePlanRules.Normalise([new CyclePlanSlot(Id.ToUpperInvariant(), 0, 1, Id.ToUpperInvariant(), 2)]);

        slots.Should().Equal(new CyclePlanSlot(Id, 0, 1, Id, 2));
    }

    private static CyclePlan Plan(string id, bool active) =>
        new(id, "P", active, [], CyclePlanRules.EmptyWeekThemes, false, PlanSources.Manual, null, null, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public void The_default_plan_cannot_be_deleted_even_when_it_is_active_and_the_active_plan_cannot_either()
    {
        var oldest = Plan("a00000000000000000000001", active: true);
        var other = Plan("b00000000000000000000002", active: false);
        var active = Plan("c00000000000000000000003", active: true);

        CyclePlanRules.DeleteConflict(oldest, oldest)!.Code.Should().Be("default_plan");
        CyclePlanRules.DeleteConflict(active, oldest)!.Code.Should().Be("active_plan");
        CyclePlanRules.DeleteConflict(other, oldest).Should().BeNull();
        CyclePlanRules.DeleteConflict(other, null).Should().BeNull();
    }

    [Fact]
    public void The_cursor_round_trips_and_refuses_anything_else()
    {
        var cursor = new CyclePlanCursor(new DateTimeOffset(2026, 10, 3, 8, 0, 0, TimeSpan.Zero), Id);

        CyclePlanCursor.TryDecode(cursor.Encode(), out var decoded).Should().BeTrue();

        decoded.Should().Be(cursor);
        CyclePlanCursor.TryDecode("not a cursor", out _).Should().BeFalse();
        CyclePlanCursor.TryDecode("", out _).Should().BeFalse();
        CyclePlanCursor.TryDecode("WyJ4IiwiYSJd", out _).Should().BeFalse(); // ["x","a"]
    }
}
