using Huishoudplanner.Application.Generation;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;

namespace Huishoudplanner.Application.Tests.Generation;

/// <summary>The read-only cycle list (cycles-api.test.ts): in index order, bounded and paged with a cursor.</summary>
public sealed class CycleServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 14, 6, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (CycleService Service, FakeCycleStore Store) Arrange(params int[] indexes)
    {
        var store = new FakeCycleStore();
        foreach (var index in indexes)
        {
            var start = new DateOnly(2026, 9, 14).AddDays(index * 28);
            store.Items.Add(new Cycle(store.NextId(), index, start, start.AddDays(27), null, At, "run"));
        }

        return (new CycleService(store), store);
    }

    [Fact]
    public async Task List_is_empty_before_any_generation()
    {
        var (service, _) = Arrange();

        var list = (await service.ListAsync(null, null, Ct)).AsT0;

        list.Items.Should().BeEmpty();
        list.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task List_returns_the_cycles_in_index_order_with_their_day_key_boundaries_negative_indexes_first()
    {
        var (service, _) = Arrange(1, -1, 0);

        var list = (await service.ListAsync(null, null, Ct)).AsT0;

        list.Items.Select(c => (c.Index, c.StartDate, c.EndDate)).Should().Equal(
            (-1, new DateOnly(2026, 8, 17), new DateOnly(2026, 9, 13)),
            (0, new DateOnly(2026, 9, 14), new DateOnly(2026, 10, 11)),
            (1, new DateOnly(2026, 10, 12), new DateOnly(2026, 11, 8)));
        list.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task List_pages_with_a_cursor_also_across_a_negative_index()
    {
        var (service, _) = Arrange(-1, 0, 1);

        var first = (await service.ListAsync(2, null, Ct)).AsT0;
        var second = (await service.ListAsync(2, first.NextCursor, Ct)).AsT0;

        first.Items.Select(c => c.Index).Should().Equal(-1, 0);
        first.NextCursor.Should().NotBeNull();
        second.Items.Select(c => c.Index).Should().Equal(1);
        second.NextCursor.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public async Task List_refuses_a_limit_outside_1_to_200(int limit)
    {
        var (service, _) = Arrange();

        (await service.ListAsync(limit, null, Ct)).AsT1.Errors.Should().ContainKey("limit");
    }

    [Fact]
    public async Task List_refuses_a_cursor_it_did_not_produce()
    {
        var (service, _) = Arrange();

        (await service.ListAsync(null, "garbage", Ct)).AsT1.Errors["cursor"].Should().Equal("invalid_cursor");
    }

    [Fact]
    public async Task List_passes_on_a_port_failure()
    {
        var (service, store) = Arrange(0);
        store.Failure = new PortError("down");

        (await service.ListAsync(null, null, Ct)).AsT2.Message.Should().Be("down");
    }
}
