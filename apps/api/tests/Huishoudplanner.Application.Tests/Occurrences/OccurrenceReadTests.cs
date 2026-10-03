using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;

namespace Huishoudplanner.Application.Tests.Occurrences;

/// <summary>
/// The reads of the occurrences (<c>occurrences.test.ts</c>, "GET /api/occurrences"): day keys with <c>isOverdue</c> and <c>movedFrom</c>, the
/// filters, the validation of the query, and the paging and the cycle and week index that v2 adds.
/// </summary>
public sealed class OccurrenceReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OccurrenceListRequest Week(string from = "2026-09-14", string to = "2026-09-20") =>
        new(DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture));

    private static OccurrenceWorld Arranged()
    {
        var w = new OccurrenceWorld();
        w.Seed(w.Weekly, "2026-09-14", w.P1);
        w.Seed(w.Weekly, "2026-09-16", w.P1);
        w.Seed(w.Twice, "2026-09-16");
        w.Seed(w.Twice, "2026-09-17", w.P2);
        w.Seed(w.Weekly, "2026-09-21", w.P1);
        return w;
    }

    [Fact]
    public async Task List_returnsTheRangeInDisplayOrderWithOverdueAndMovedFrom()
    {
        var w = Arranged();

        var list = (await w.Service.ListAsync(Week(), Ct)).AsT0;

        list.Items.Select(v => (v.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), v.Occurrence.TaskNameSnapshot)).Should().Equal(
            ("2026-09-14", "Badkamer schoonmaken"),
            ("2026-09-16", "Badkamer schoonmaken"),
            ("2026-09-16", "Wastafel"),
            ("2026-09-17", "Wastafel"));
        list.Items.Select(v => v.IsOverdue).Should().Equal(true, false, false, false);
        list.Items.Should().OnlyContain(v => v.MovedFrom == null && v.PlannedDate == v.Date);
        list.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task List_showsWhereAMovedOccurrenceCameFromAndWhereTheDayFallsInTheCycles()
    {
        var w = new OccurrenceWorld();
        var moved = w.Seed(w.Weekly, "2026-09-14", w.P1, tweak: o => o with { Date = OccurrenceWorld.At("2026-10-12"), CycleId = w.Cycle1.Id });

        var view = (await w.Service.ListAsync(Week("2026-10-12", "2026-10-12"), Ct)).AsT0.Items.Single();

        view.Occurrence.Id.Should().Be(moved.Id);
        view.MovedFrom.Should().Be(new DateOnly(2026, 9, 14));
        (view.CycleIndex, view.WeekIndex).Should().Be((1, 0));
    }

    [Fact]
    public async Task List_filtersByAssigneeAndStatus()
    {
        var w = Arranged();

        var mine = (await w.Service.ListAsync(Week() with { AssigneeId = w.P2.Id }, Ct)).AsT0;
        var done = (await w.Service.ListAsync(Week("2026-09-14", "2026-09-14") with { Status = OccurrenceStatus.Done }, Ct)).AsT0;

        mine.Items.Select(v => v.Date).Should().Equal(new DateOnly(2026, 9, 17));
        done.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task List_aRangeThatRunsBackwardsIsAValidationError()
    {
        var w = Arranged();

        var result = await w.Service.ListAsync(Week("2026-09-20", "2026-09-14"), Ct);

        result.AsT1.Errors["from"].Should().Equal("from_after_to");
    }

    [Theory]
    [InlineData("assigneeId", "not-an-id")]
    [InlineData("cursor", "garbage")]
    public async Task List_aMalformedFilterIsAFieldKeyedValidationError(string field, string value)
    {
        var w = Arranged();
        var request = field == "assigneeId" ? Week() with { AssigneeId = value } : Week() with { Cursor = value };

        var result = await w.Service.ListAsync(request, Ct);

        result.AsT1.Errors.Should().ContainKey(field);
    }

    [Fact]
    public async Task List_aRangeAtTheEdgeOfTheCalendarIsAValidationErrorAndNeverOverflows()
    {
        var w = Arranged();

        var result = await w.Service.ListAsync(new OccurrenceListRequest(DateOnly.MinValue, DateOnly.MaxValue), Ct);

        result.AsT1.Errors["from"].Should().Equal("out_of_range");
        result.AsT1.Errors["to"].Should().Equal("out_of_range");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task List_aLimitOutsideTheRangeIsAValidationError(int limit)
    {
        var w = Arranged();

        var result = await w.Service.ListAsync(Week() with { Limit = limit }, Ct);

        result.AsT1.Errors.Should().ContainKey("limit");
    }

    [Fact]
    public async Task List_pagesWithACursorAndTheLastPageHasNone()
    {
        var w = Arranged();

        var first = (await w.Service.ListAsync(Week() with { Limit = 3 }, Ct)).AsT0;
        var second = (await w.Service.ListAsync(Week() with { Limit = 3, Cursor = first.NextCursor }, Ct)).AsT0;

        first.Items.Should().HaveCount(3);
        first.NextCursor.Should().NotBeNull();
        second.Items.Should().HaveCount(1);
        second.NextCursor.Should().BeNull();
        first.Items.Concat(second.Items).Select(v => v.Occurrence.Id).Should().OnlyHaveUniqueItems().And.HaveCount(4);
    }

    [Fact]
    public async Task List_withoutSettingsIsSettingsMissing()
    {
        var w = Arranged();
        w.SettingsStore.Document = null;

        var result = await w.Service.ListAsync(Week(), Ct);

        result.IsT2.Should().BeTrue();
    }

    [Fact]
    public async Task List_aFailingStoreIsAPortError()
    {
        var w = Arranged();
        w.Occurrences.Failure = new PortError("down");

        var result = await w.Service.ListAsync(Week(), Ct);

        result.AsT3.Message.Should().Be("down");
    }

    [Fact]
    public async Task Get_returnsTheViewAnUnknownIdIsNotFoundAndABadIdIsAValidationError()
    {
        var w = Arranged();
        var known = w.Occurrences.Items[0];

        var found = await w.Service.GetAsync(known.Id, Ct);
        var unknown = await w.Service.GetAsync("0123456789abcdef01234567", Ct);
        var bad = await w.Service.GetAsync("nope", Ct);

        found.AsT0.Occurrence.Id.Should().Be(known.Id);
        found.AsT0.IsOverdue.Should().BeTrue();
        unknown.IsT1.Should().BeTrue();
        bad.AsT2.Errors["id"].Should().Equal("invalid_object_id");
    }
}
