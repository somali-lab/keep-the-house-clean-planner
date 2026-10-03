using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;

namespace Huishoudplanner.Application.Tests.Audit;

/// <summary>Reading and clearing the history: every result variant. Port of the logic behind <c>routes/audit.ts</c>.</summary>
public sealed class AuditLogServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly AuditLogFilter NoFilter = new();

    [Fact]
    public async Task List_returnsTheNewestEntryFirst_withoutACursorOnTheLastPage()
    {
        var world = new AuditWorld();
        var older = world.Add(T0);
        var newer = world.Add(T0.AddHours(1));

        var page = (await world.Log.ListAsync(NoFilter, null, null, Ct)).AsT0;

        page.Items.Should().Equal(newer, older);
        page.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task List_defaultsToFiftyEntries_andAsksTheStoreForOneMoreToKnowWhetherThereIsANextPage()
    {
        var world = new AuditWorld();

        await world.Log.ListAsync(NoFilter, null, null, Ct);

        world.Store.LastQuery!.Value.Take.Should().Be(51);
    }

    [Fact]
    public async Task List_pagesWithAStableCursor_evenWhenEntriesShareATimestamp()
    {
        var world = new AuditWorld();
        for (var i = 0; i < 7; i++)
        {
            world.Add(i < 4 ? T0 : T0.AddMinutes(i));
        }

        var expected = world.Store.Entries.OrderByDescending(e => e.At).ThenByDescending(e => e.Id, StringComparer.Ordinal).Select(e => e.Id).ToList();
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = (await world.Log.ListAsync(NoFilter, 3, cursor, Ct)).AsT0;
            page.Items.Count.Should().BeLessThanOrEqualTo(3);
            seen.AddRange(page.Items.Select(e => e.Id));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        pages.Should().Be(3);
        seen.Should().Equal(expected);
    }

    [Fact]
    public async Task List_whenThePageIsExactlyFull_hasNoCursor()
    {
        var world = new AuditWorld();
        world.Add(T0);
        world.Add(T0.AddMinutes(1));

        var page = (await world.Log.ListAsync(NoFilter, 2, null, Ct)).AsT0;

        page.Items.Should().HaveCount(2);
        page.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task List_passesTheFilterToTheStore()
    {
        var world = new AuditWorld();
        var room = world.Add(T0, "room", "0000000000000000000000aa", source: "api");
        world.Add(T0, "user", source: "ui");
        var filter = new AuditLogFilter(AuditEntity.Room, "0000000000000000000000aa", null, AuditSource.Api, T0, T0);

        var page = (await world.Log.ListAsync(filter, null, null, Ct)).AsT0;

        page.Items.Should().Equal(room);
        world.Store.LastQuery!.Value.Filter.Should().Be(filter);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public async Task List_withALimitOutsideOneToTwoHundred_isAValidationError_andReadsNothing(int limit)
    {
        var world = new AuditWorld();

        var result = await world.Log.ListAsync(NoFilter, limit, null, Ct);

        result.AsT1.Errors.Should().ContainKey("limit");
        world.Store.LastQuery.Should().BeNull();
    }

    [Fact]
    public async Task List_withAMalformedCursor_isAValidationErrorOnCursor()
    {
        var world = new AuditWorld();

        var result = await world.Log.ListAsync(NoFilter, null, "bm9wZQ", Ct);

        result.AsT1.Errors["cursor"].Should().Equal("invalid_cursor");
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("0123456789abcdef0123456")]
    public async Task List_withAMalformedIdFilter_isAValidationErrorOnThatField(string id)
    {
        var world = new AuditWorld();

        (await world.Log.ListAsync(new AuditLogFilter(EntityId: id), null, null, Ct)).AsT1.Errors.Should().ContainKey("entityId");
        (await world.Log.ListAsync(new AuditLogFilter(ActorId: id), null, null, Ct)).AsT1.Errors.Should().ContainKey("actorId");
    }

    [Fact]
    public async Task List_collectsEveryProblemInOneAnswer()
    {
        var world = new AuditWorld();

        var result = await world.Log.ListAsync(new AuditLogFilter(EntityId: "x", ActorId: "y"), 0, "z", Ct);

        result.AsT1.Errors.Keys.Should().BeEquivalentTo("entityId", "actorId", "limit", "cursor");
    }

    [Fact]
    public async Task List_addsTheOccurrenceContextToOccurrenceEntriesThatLackIt()
    {
        var world = new AuditWorld();
        var occurrenceId = "0000000000000000000000aa";
        var date = new DateTimeOffset(2026, 9, 18, 22, 0, 0, TimeSpan.Zero);
        world.Occurrences.Items[occurrenceId] = new OccurrenceContext("Douche schoonmaken", "Badkamer", date);
        world.Add(T0, "occurrence", occurrenceId, meta: AuditObject.Of(("reason", "complete")));

        var item = (await world.Log.ListAsync(NoFilter, null, null, Ct)).AsT0.Items.Single();

        item.Meta.Should().Be(AuditObject.Of(
            ("reason", "complete"),
            ("occurrence", AuditObject.Of(("taskNameSnapshot", "Douche schoonmaken"), ("roomNameSnapshot", "Badkamer"), ("date", date)))));
    }

    [Fact]
    public async Task List_usesNullForAMissingRoomName_andCreatesMetaWhenThereIsNone()
    {
        var world = new AuditWorld();
        var occurrenceId = "0000000000000000000000aa";
        var date = new DateTimeOffset(2026, 9, 18, 22, 0, 0, TimeSpan.Zero);
        world.Occurrences.Items[occurrenceId] = new OccurrenceContext("Afwassen", null, date);
        world.Add(T0, "occurrence", occurrenceId);

        var item = (await world.Log.ListAsync(NoFilter, null, null, Ct)).AsT0.Items.Single();

        item.Meta!["occurrence"].Should().Be(AuditObject.Of(("taskNameSnapshot", "Afwassen"), ("roomNameSnapshot", AuditNull.Instance), ("date", date)));
    }

    [Fact]
    public async Task List_leavesEntriesAloneThatNeedNoEnrichment_andDoesNotLookUpWithoutOccurrenceEntries()
    {
        var world = new AuditWorld();
        var existing = AuditObject.Of(("occurrence", AuditObject.Of(("taskNameSnapshot", "Oud"))));
        var withContext = world.Add(T0, "occurrence", "0000000000000000000000aa", meta: existing);
        var room = world.Add(T0.AddMinutes(1), "room");
        world.Occurrences.Items["0000000000000000000000aa"] = new OccurrenceContext("Nieuw", null, T0);

        var items = (await world.Log.ListAsync(NoFilter, null, null, Ct)).AsT0.Items;

        items.Should().Equal(room, withContext);
        world.Occurrences.Lookups.Should().BeEmpty();
    }

    [Fact]
    public async Task List_leavesAnOccurrenceEntryAloneWhenItsOccurrenceNoLongerExists()
    {
        var world = new AuditWorld();
        var entry = world.Add(T0, "occurrence", "0000000000000000000000aa");

        var items = (await world.Log.ListAsync(NoFilter, null, null, Ct)).AsT0.Items;

        items.Should().Equal(entry);
        world.Occurrences.Lookups.Should().ContainSingle();
    }

    [Fact]
    public async Task List_looksUpEachOccurrenceOncePerPage()
    {
        var world = new AuditWorld();
        world.Add(T0, "occurrence", "0000000000000000000000aa");
        world.Add(T0.AddMinutes(1), "occurrence", "0000000000000000000000aa");
        world.Add(T0.AddMinutes(2), "occurrence", "0000000000000000000000bb");

        await world.Log.ListAsync(NoFilter, null, null, Ct);

        world.Occurrences.Lookups.Should().ContainSingle().Which.Should().BeEquivalentTo(["0000000000000000000000aa", "0000000000000000000000bb"]);
    }

    [Fact]
    public async Task List_passesOnAStoreFailure()
    {
        var world = new AuditWorld { Store = { Failure = new PortError("down") } };

        (await world.Log.ListAsync(NoFilter, null, null, Ct)).AsT2.Message.Should().Be("down");
    }

    [Fact]
    public async Task List_passesOnAFailingOccurrenceLookup()
    {
        var world = new AuditWorld { Occurrences = { Failure = new PortError("no occurrences") } };
        world.Add(T0, "occurrence");

        (await world.Log.ListAsync(NoFilter, null, null, Ct)).AsT2.Message.Should().Be("no occurrences");
    }

    [Fact]
    public async Task Clear_removesEverything_andReturnsTheCount_withoutWritingAnEntry()
    {
        var world = new AuditWorld();
        world.Add(T0);
        world.Add(T0.AddMinutes(1));

        var result = await world.Log.ClearAsync(Ct);

        result.AsT0.Should().Be(2);
        world.Store.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Clear_ofAnEmptyLog_isZero()
    {
        var world = new AuditWorld();

        (await world.Log.ClearAsync(Ct)).AsT0.Should().Be(0);
    }

    [Fact]
    public async Task Clear_passesOnAStoreFailure()
    {
        var world = new AuditWorld { Store = { Failure = new PortError("down") } };

        (await world.Log.ClearAsync(Ct)).AsT1.Message.Should().Be("down");
    }
}
