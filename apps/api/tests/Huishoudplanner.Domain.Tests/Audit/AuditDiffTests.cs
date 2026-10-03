using Huishoudplanner.Domain.Audit;

namespace Huishoudplanner.Domain.Tests.Audit;

/// <summary>
/// Every case of apps/server/test/audit-diff.test.ts. Mapping: JS <c>Date</c> is <see cref="AuditInstant"/>, an
/// <c>ObjectId</c> is <see cref="AuditObjectId"/>, <c>undefined</c> is an absent key.
/// </summary>
public class AuditDiffTests
{
    private const string Hex = "0123456789abcdef01234567";

    private static AuditInstant Instant(string iso) => new(DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture));

    private static AuditInstant Ms(long unixMs) => new(DateTimeOffset.FromUnixTimeMilliseconds(unixMs));

    private static AuditObject O(params (string, AuditValue)[] p) => AuditObject.Of(p);

    private static void Should(FieldDiff actual, AuditObject before, AuditObject after)
    {
        actual.Before.Should().Be(before);
        actual.After.Should().Be(after);
    }

    [Fact]
    public void Returns_only_changed_top_level_fields_and_ignores_updatedAt()
    {
        var diff = AuditDiff.Diff(
            O(("name", "Badkamer"), ("durationMinutes", 30), ("active", true), ("updatedAt", Ms(1))),
            O(("name", "Badkamer"), ("durationMinutes", 45), ("active", true), ("updatedAt", Ms(2))));

        Should(diff, O(("durationMinutes", 30)), O(("durationMinutes", 45)));
    }

    [Fact]
    public void Diffs_nested_objects_recursively()
    {
        var diff = AuditDiff.Diff(
            O(("dailyBudgetMinutes", O(("weekday", 60), ("weekend", 120))), ("aiProvider", O(("type", "none")))),
            O(("dailyBudgetMinutes", O(("weekday", 90), ("weekend", 120))), ("aiProvider", O(("type", "none")))));

        Should(diff, O(("dailyBudgetMinutes", O(("weekday", 60)))), O(("dailyBudgetMinutes", O(("weekday", 90)))));
    }

    [Fact]
    public void Nested_objects_never_ignore_updatedAt_only_the_top_level_does()
    {
        var diff = AuditDiff.Diff(O(("inner", O(("updatedAt", 1)))), O(("inner", O(("updatedAt", 2)))));

        Should(diff, O(("inner", O(("updatedAt", 1)))), O(("inner", O(("updatedAt", 2)))));
    }

    [Fact]
    public void Compares_arrays_as_a_whole_by_value()
    {
        AuditDiff.Diff(O(("tags", AuditArray.Of("a", "b"))), O(("tags", AuditArray.Of("a", "b")))).IsEmpty.Should().BeTrue();
        Should(
            AuditDiff.Diff(O(("tags", AuditArray.Of("a", "b"))), O(("tags", AuditArray.Of("b", "a")))),
            O(("tags", AuditArray.Of("a", "b"))),
            O(("tags", AuditArray.Of("b", "a"))));

        var slot = () => O(("slots", AuditArray.Of(O(("w", 1), ("d", O(("x", 1)))))));
        AuditDiff.Diff(slot(), slot()).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Compares_object_ids_by_value()
    {
        AuditDiff.Diff(O(("roomId", new AuditObjectId(Hex))), O(("roomId", new AuditObjectId(Hex)))).IsEmpty.Should().BeTrue();

        var other = new AuditObjectId("ffffffffffffffffffffffff");
        var diff = AuditDiff.Diff(O(("roomId", new AuditObjectId(Hex))), O(("roomId", other)));
        diff.Before["roomId"].Should().Be(new AuditObjectId(Hex));
        diff.After["roomId"].Should().BeSameAs(other);

        AuditDiff.Diff(O(("a", AuditNull.Instance)), O(("a", new AuditObjectId(Hex)))).Before.Should().Be(O(("a", AuditNull.Instance)));
    }

    [Fact]
    public void Object_id_hex_is_case_insensitive_and_must_be_24_hex_characters()
    {
        new AuditObjectId(Hex.ToUpperInvariant()).Should().Be(new AuditObjectId(Hex));
        FluentActions.Invoking(() => new AuditObjectId("nope")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Compares_dates_by_time()
    {
        AuditDiff.Diff(O(("at", Instant("2026-09-14T00:00:00Z"))), O(("at", Instant("2026-09-14T00:00:00Z")))).IsEmpty.Should().BeTrue();
        AuditDiff.Diff(O(("at", Instant("2026-09-14T00:00:00Z"))), O(("at", Instant("2026-09-15T00:00:00Z")))).After
            .Should().Be(O(("at", Instant("2026-09-15T00:00:00Z"))));
    }

    [Fact]
    public void A_date_compares_by_the_instant_not_the_offset()
    {
        AuditDiff.Diff(O(("at", Instant("2026-09-14T02:00:00+02:00"))), O(("at", Instant("2026-09-14T00:00:00Z")))).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Handles_added_and_removed_keys_and_null_documents()
    {
        Should(AuditDiff.Diff(O(("a", 1)), O(("b", 2))), O(("a", 1)), O(("b", 2)));
        Should(AuditDiff.Diff(null, O(("name", "x"), ("updatedAt", Ms(0)))), AuditObject.Empty, O(("name", "x")));
    }

    [Fact]
    public void A_removed_document_is_all_before()
    {
        Should(AuditDiff.Diff(O(("name", "x"), ("updatedAt", Ms(0))), null), O(("name", "x")), AuditObject.Empty);
    }

    [Fact]
    public void Supports_custom_ignore_lists()
    {
        var diff = AuditDiff.Diff(AuditObject.Empty, O(("_id", 1), ("createdAt", 2), ("name", "x")), ["_id", "createdAt"]);

        Should(diff, AuditObject.Empty, O(("name", "x")));
    }

    [Fact]
    public void A_custom_ignore_list_replaces_the_default_so_updatedAt_is_diffed()
    {
        var diff = AuditDiff.Diff(O(("updatedAt", 1)), O(("updatedAt", 2)), ["createdAt"]);

        Should(diff, O(("updatedAt", 1)), O(("updatedAt", 2)));
    }

    [Fact]
    public void Null_is_a_value_and_differs_from_an_absent_key()
    {
        Should(AuditDiff.Diff(O(("a", AuditNull.Instance)), AuditObject.Empty), O(("a", AuditNull.Instance)), AuditObject.Empty);
        Should(AuditDiff.Diff(AuditObject.Empty, O(("a", AuditNull.Instance))), AuditObject.Empty, O(("a", AuditNull.Instance)));
    }

    [Fact]
    public void An_object_replaced_by_a_scalar_is_reported_whole()
    {
        Should(AuditDiff.Diff(O(("a", O(("x", 1)))), O(("a", 5))), O(("a", O(("x", 1)))), O(("a", 5)));
    }

    [Fact]
    public void A_nested_key_that_is_added_shows_only_in_after()
    {
        Should(
            AuditDiff.Diff(O(("a", O(("x", 1)))), O(("a", O(("x", 1), ("y", 2))))),
            O(("a", AuditObject.Empty)),
            O(("a", O(("y", 2)))));
    }
}

/// <summary>The <c>deepEqual</c> describe block of audit-diff.test.ts, plus the numeric rule of JS equality.</summary>
public class AuditDeepEqualTests
{
    [Fact]
    public void Distinguishes_types()
    {
        AuditDiff.DeepEqual(new AuditInteger(1), new AuditString("1")).Should().BeFalse();
        AuditDiff.DeepEqual(AuditArray.Of(), AuditObject.Empty).Should().BeFalse();
        AuditDiff.DeepEqual(new AuditInstant(DateTimeOffset.UnixEpoch), new AuditInteger(0)).Should().BeFalse();
        AuditDiff.DeepEqual(new AuditObjectId("0123456789abcdef01234567"), new AuditString("x")).Should().BeFalse();
        AuditDiff.DeepEqual(AuditNull.Instance, null).Should().BeFalse();
    }

    [Fact]
    public void A_whole_number_equals_the_same_double()
    {
        AuditDiff.DeepEqual(new AuditInteger(1), new AuditDouble(1.0)).Should().BeTrue();
        AuditDiff.DeepEqual(new AuditInteger(1), new AuditDouble(1.5)).Should().BeFalse();
    }

    [Fact]
    public void Object_key_order_does_not_matter()
    {
        AuditDiff.DeepEqual(AuditObject.Of(("a", 1), ("b", 2)), AuditObject.Of(("b", 2), ("a", 1))).Should().BeTrue();
    }

    [Fact]
    public void Array_length_and_order_matter()
    {
        AuditDiff.DeepEqual(AuditArray.Of(1, 2), AuditArray.Of(1)).Should().BeFalse();
        AuditDiff.DeepEqual(AuditArray.Of(1, 2), AuditArray.Of(2, 1)).Should().BeFalse();
    }
}

public class ChangeSetTests
{
    private static AuditObject O(params (string, AuditValue)[] p) => AuditObject.Of(p);

    [Fact]
    public void Identical_states_are_a_no_op_even_when_only_updatedAt_moved()
    {
        var change = ChangeSet.Between(O(("name", "x"), ("updatedAt", 1)), O(("name", "x"), ("updatedAt", 2)));

        change.IsNoOp.Should().BeTrue();
    }

    [Fact]
    public void A_changed_field_is_a_change_and_becomes_an_entry_with_the_diff_and_meta()
    {
        var change = ChangeSet.Between(O(("name", "x")), O(("name", "y")));
        var actor = new AuditActor("0123456789abcdef01234567", AuditSource.Ui);
        var meta = O(("reason", "test"));

        var entry = change.ToEntry(actor, AuditEntity.Room, "ffffffffffffffffffffffff", AuditAction.Update, meta);

        change.IsNoOp.Should().BeFalse();
        entry.Before.Should().Be(O(("name", "x")));
        entry.After.Should().Be(O(("name", "y")));
        entry.Meta.Should().Be(meta);
        entry.Actor.Should().Be(actor);
    }

    [Fact]
    public void Wire_names_are_those_of_the_node_server()
    {
        AuditNames.ToWire(AuditEntity.BadgeAward).Should().Be("badgeAward");
        AuditNames.ToWire(AuditEntity.CyclePlan).Should().Be("cyclePlan");
        AuditNames.ToWire(AuditAction.AiApply).Should().Be("ai-apply");
        AuditNames.ToWire(AuditSource.System).Should().Be("system");
        Enum.GetValues<AuditEntity>().Select(AuditNames.ToWire).Should().OnlyHaveUniqueItems().And.HaveCount(11);
        Enum.GetValues<AuditAction>().Select(AuditNames.ToWire).Should().OnlyHaveUniqueItems().And.HaveCount(12);
    }

    [Fact]
    public void The_system_actor_is_the_all_zero_id_with_source_system()
    {
        AuditActor.System.Should().Be(new AuditActor("000000000000000000000000", AuditSource.System));
        AuditActor.From(new Huishoudplanner.Domain.Identity.Actor("0123456789abcdef01234567", Huishoudplanner.Domain.Identity.Role.Planner, Huishoudplanner.Domain.Identity.ActorSource.Api))
            .Source.Should().Be(AuditSource.Api);
    }
}
