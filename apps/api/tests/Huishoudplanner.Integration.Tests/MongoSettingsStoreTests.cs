using Huishoudplanner.Adapters.Mongo;
using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Settings;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests;

/// <summary>
/// The Mongo implementation of <c>ForStoringSettings</c> and <c>ForReadingCycleAnchor</c> against a document shaped like the Node
/// server writes it: a full read, a partial write that leaves every other field (also unknown ones) untouched, the idempotent insert.
/// </summary>
public sealed class MongoSettingsStoreTests(MongoContainerFixture mongo) : IAsyncLifetime
{
    private static readonly ObjectId Id = new("000000000000000000000001");
    private static readonly DateTime Created = new(2026, 9, 14, 8, 0, 0, DateTimeKind.Utc);

    private readonly MongoOptions options = new()
    {
        ConnectionString = mongo.ConnectionString,
        DatabaseName = MongoContainerFixture.NewDatabaseName(),
    };

    private readonly FixedTimeProvider clock = new("2026-09-16T08:00:00Z");
    private IMongoClient client = null!;
    private IMongoCollection<BsonDocument> collection = null!;
    private MongoSettingsStore store = null!;

    public ValueTask InitializeAsync()
    {
        client = MongoClientFactory.CreateClient(options);
        collection = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.Settings);
        store = new MongoSettingsStore(client, options, clock);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await client.DropDatabaseAsync(options.DatabaseName, TestContext.Current.CancellationToken);
        client.Dispose();
    }

    private static BsonDocument NodeDocument() => new()
    {
        { "_id", Id },
        { "cycleAnchorDate", "2026-09-14" },
        { "weekStartsOn", 1 },
        { "timezone", "Europe/Amsterdam" },
        { "vacationRanges", new BsonArray { new BsonDocument { { "from", "2026-12-24" }, { "to", "2026-12-31" } } } },
        {
            "intervals",
            new BsonArray
            {
                new BsonDocument { { "key", "daily" }, { "label", "Dagelijks" }, { "perCycle", 28 }, { "periodDays", 1 } },
                new BsonDocument { { "key", "quarter" }, { "label", "1x per kwartaal" }, { "perCycle", BsonNull.Value }, { "periodDays", 91.0 } },
            }
        },
        { "aiProvider", new BsonDocument { { "type", "openai-compatible" }, { "endpoint", "http://localhost:1234" }, { "model", "m" }, { "timeoutSeconds", 60 } } },
        { "aiPrompts", new BsonDocument { { "planProposal", "a" }, { "planRebalance", "b" }, { "taskSuggestions", "c" }, { "planExplanation", "d" } } },
        { "completionControl", "thumb" },
        { "promoteThreshold", 3 },
        {
            "dismissedPromotions",
            new BsonArray
            {
                new BsonDocument
                {
                    { "planId", new ObjectId("aaaaaaaaaaaaaaaaaaaaaaaa") },
                    { "taskId", new ObjectId("bbbbbbbbbbbbbbbbbbbbbbbb") },
                    { "weekIndex", 1 },
                    { "weekday", 2 },
                    { "toWeekday", 3 },
                    { "toAssigneeId", BsonNull.Value },
                    { "lastEvidenceId", new ObjectId("cccccccccccccccccccccccc") },
                },
            }
        },
        { "bonusSchedule", new BsonArray { new BsonDocument { { "from", "2026-09-01" }, { "weekDone", 5 }, { "weekOnTime", 3 }, { "cycleDone", 20 }, { "cycleOnTime", 10 } } } },
        { "bonusFloor", "2026-08-31" },
        { "currencyCode", "USD" },
        { "centsPerPoint", 7 },
        { "rewardGoals", new BsonDocument { { "weekPoints", 50 }, { "cyclePoints", BsonNull.Value } } },
        { "createdAt", new BsonDateTime(Created) },
        { "updatedAt", new BsonDateTime(Created) },
    };

    [Fact]
    public async Task Get_readsEveryFieldOfADocumentTheNodeServerWrote()
    {
        await collection.InsertOneAsync(NodeDocument(), cancellationToken: TestContext.Current.CancellationToken);

        var settings = (await store.GetAsync(TestContext.Current.CancellationToken)).AsT0;

        settings.CycleAnchorDate.Should().Be(new DateOnly(2026, 9, 14));
        settings.VacationRanges.Should().Equal(new VacationRange(new DateOnly(2026, 12, 24), new DateOnly(2026, 12, 31)));
        settings.Intervals.Select(i => (i.Key, i.PerCycle, i.PeriodDays)).Should().Equal(("daily", (int?)28, 1), ("quarter", null, 91));
        settings.AiProvider.Should().Be(new AiProviderSettings(AiProviderType.OpenAiCompatible, "http://localhost:1234", "m", 60));
        settings.AiPrompts.Should().Be(new AiPrompts("a", "b", "c", "d"));
        settings.AiPromptTemplates.Should().BeNull();
        settings.CompletionControl.Should().Be(CompletionControl.Thumb);
        settings.PromoteThreshold.Should().Be(3);
        settings.DismissedPromotions.Should().Equal(new DismissedPromotion(
            "aaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbb", 1, 2, 3, null, "cccccccccccccccccccccccc"));
        settings.BonusSchedule.Should().Equal(new BonusScheduleRow(new DateOnly(2026, 9, 1), new BonusAmounts(5, 3, 20, 10)));
        settings.BonusFloor.Should().Be(new DateOnly(2026, 8, 31));
        (settings.CurrencyCode, settings.CentsPerPoint).Should().Be(("USD", 7));
        settings.RewardGoals.Should().Be(new RewardGoals(50, null));
        settings.CreatedAt.Should().Be(new DateTimeOffset(Created));
    }

    [Fact]
    public async Task Get_readsADocumentThatHasNoOptionalParts()
    {
        var minimal = NodeDocument();
        foreach (var name in new[] { "aiPrompts", "completionControl", "bonusSchedule", "bonusFloor", "currencyCode", "centsPerPoint", "rewardGoals", "dismissedPromotions" })
        {
            minimal.Remove(name);
        }

        await collection.InsertOneAsync(minimal, cancellationToken: TestContext.Current.CancellationToken);

        var settings = (await store.GetAsync(TestContext.Current.CancellationToken)).AsT0;

        settings.AiPrompts.Should().BeNull();
        settings.CompletionControl.Should().BeNull();
        settings.BonusSchedule.Should().BeNull();
        settings.BonusFloor.Should().BeNull();
        settings.CurrencyCode.Should().BeNull();
        settings.CentsPerPoint.Should().BeNull();
        settings.RewardGoals.Should().BeNull();
        settings.DismissedPromotions.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_withoutADocument_isSettingsMissing()
    {
        (await store.GetAsync(TestContext.Current.CancellationToken)).IsT1.Should().BeTrue();
        (await store.GetAnchorAsync(TestContext.Current.CancellationToken)).IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task Get_ofAnUnreadableDocument_isAPortError_withoutItsContent()
    {
        var broken = NodeDocument();
        broken["aiProvider"] = new BsonDocument { { "type", "secret-provider-name" } };
        await collection.InsertOneAsync(broken, cancellationToken: TestContext.Current.CancellationToken);

        var result = await store.GetAsync(TestContext.Current.CancellationToken);

        result.AsT2.Message.Should().NotContain("secret-provider-name");
    }

    [Fact]
    public async Task GetAnchor_readsTheCycleAnchorOfTheDocument()
    {
        await collection.InsertOneAsync(NodeDocument(), cancellationToken: TestContext.Current.CancellationToken);

        (await store.GetAnchorAsync(TestContext.Current.CancellationToken)).AsT0.Should().Be(new DateOnly(2026, 9, 14));
    }

    [Fact]
    public async Task Update_setsOnlyTheChangedFields_andLeavesEveryOtherFieldAndUnknownElementUntouched()
    {
        var original = NodeDocument();
        original["fieldOfANewerVersion"] = new BsonDocument { { "kept", true } };
        await collection.InsertOneAsync(original, cancellationToken: TestContext.Current.CancellationToken);

        var result = await store.UpdateAsync(new SettingsChanges { PromoteThreshold = 5, CentsPerPoint = 0 }, TestContext.Current.CancellationToken);

        result.AsT0.PromoteThreshold.Should().Be(5);
        var expected = original.DeepClone().AsBsonDocument;
        expected["promoteThreshold"] = 5;
        expected["centsPerPoint"] = 0;
        expected["updatedAt"] = new BsonDateTime(new DateTime(2026, 9, 16, 8, 0, 0, DateTimeKind.Utc));
        expected["version"] = 1; // a document without a version reads as 0 and its first write makes it 1
        (await collection.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(TestContext.Current.CancellationToken)).ShouldBeBson(expected);
    }

    [Fact]
    public async Task Update_writesTheStructuresTheNodeServerWrites()
    {
        await collection.InsertOneAsync(NodeDocument(), cancellationToken: TestContext.Current.CancellationToken);
        var template = new AiPromptTemplate("s {{schema}}", "u {{input}}");

        await store.UpdateAsync(
            new SettingsChanges
            {
                Intervals = [new Interval("year", "Jaar", null, 365)],
                AiPromptTemplates = new AiPromptTemplates(template, template, template, template),
                BonusSchedule = [new BonusScheduleRow(new DateOnly(2026, 9, 16), new BonusAmounts(1, 2, 3, 4))],
                RewardGoals = new RewardGoals(null, 0),
                AiProvider = new AiProviderSettings(AiProviderType.None),
            },
            TestContext.Current.CancellationToken);

        var stored = await collection.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(TestContext.Current.CancellationToken);
        stored["intervals"].AsBsonArray.Single().AsBsonDocument.ShouldBeBson(new BsonDocument { { "key", "year" }, { "label", "Jaar" }, { "perCycle", BsonNull.Value }, { "periodDays", 365 } });
        stored["aiPromptTemplates"].AsBsonDocument["planRebalance"].AsBsonDocument.ShouldBeBson(new BsonDocument { { "system", "s {{schema}}" }, { "user", "u {{input}}" } });
        stored["bonusSchedule"].AsBsonArray.Single().AsBsonDocument.ShouldBeBson(new BsonDocument { { "from", "2026-09-16" }, { "weekDone", 1 }, { "weekOnTime", 2 }, { "cycleDone", 3 }, { "cycleOnTime", 4 } });
        stored["rewardGoals"].AsBsonDocument.ShouldBeBson(new BsonDocument { { "weekPoints", BsonNull.Value }, { "cyclePoints", 0 } });
        stored["aiProvider"].AsBsonDocument.ShouldBeBson(new BsonDocument { { "type", "none" } });
    }

    [Fact]
    public async Task Update_withoutADocument_isSettingsMissing_andCreatesNothing()
    {
        var result = await store.UpdateAsync(new SettingsChanges { PromoteThreshold = 5 }, TestContext.Current.CancellationToken);

        result.IsT1.Should().BeTrue();
        (await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task InsertIfMissing_insertsOnce_andNeverOverwrites()
    {
        var settings = SettingsDefaults.ForNewInstallation("Europe/Amsterdam", new DateOnly(2026, 9, 16), new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero));

        (await store.InsertIfMissingAsync(settings, TestContext.Current.CancellationToken)).AsT0.Should().BeTrue();
        (await store.InsertIfMissingAsync(settings with { PromoteThreshold = 9 }, TestContext.Current.CancellationToken)).AsT0.Should().BeFalse();

        var stored = await collection.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(TestContext.Current.CancellationToken);
        stored["_id"].AsObjectId.Should().Be(Id);
        stored["promoteThreshold"].AsInt32.Should().Be(2);
        (await store.GetAsync(TestContext.Current.CancellationToken)).AsT0.Should().BeEquivalentTo(settings);
    }
}
