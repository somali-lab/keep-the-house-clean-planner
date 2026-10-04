using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests;

/// <summary>
/// The Mongo adapters of the badges on a real replica set: the documents of the Node server (the picture as binary), the order and the cursors of the
/// lists, writes that belong to a transaction, the compare-and-set of the awards, the unique keys, and the credit rule of the executions the rules count.
/// </summary>
public sealed class MongoBadgeStoresTests(MongoContainerFixture mongo)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset At = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);

    private static NewBadge Draft(string name, BadgeRule? rule = null, string? exampleKey = null, BadgeImageData? image = null, DateTimeOffset? at = null) =>
        new(name, string.Empty, rule ?? new BadgeRule(BadgeRuleType.Executions, [], 1), true, exampleKey, image, at ?? At);

    private static async Task<T> InTransaction<T>(BadgeHarness h, Func<CancellationToken, Task<T>> work)
    {
        var runner = h.Services.GetRequiredService<ForRunningTransactions>();
        var ran = await runner.RunAsync(async ct => TransactionOutcome.Commit(await work(ct)), Ct);
        return ran.AsT0;
    }

    private static BadgeImageData Png()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3 };
        return new BadgeImageData(bytes, BadgeImageType.Png, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
    }

    // ---- writes belong to a transaction

    [Fact]
    public async Task Writes_outsideATransactionAreRefusedAndWriteNothing()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badges = h.Services.GetRequiredService<ForStoringBadges>();
        var awards = h.Services.GetRequiredService<ForStoringBadgeAwards>();

        (await badges.InsertAsync(Draft("Weg"), Ct)).AsT1.Message.Should().StartWith("badges.no_transaction");
        (await badges.UpdateAsync(ObjectId.GenerateNewId().ToString(), new BadgeChanges(Name: "Weg"), At, Ct)).AsT2.Message.Should().StartWith("badges.no_transaction");
        (await badges.DeleteAsync(ObjectId.GenerateNewId().ToString(), Ct)).AsT2.Message.Should().StartWith("badges.no_transaction");
        (await awards.ApplyAsync(BadgeAwardPlan.Empty, At, Ct)).AsT1.Message.Should().StartWith("badgeAwards.no_transaction");
        (await h.Badges.CountDocumentsAsync(new BsonDocument(), cancellationToken: Ct)).Should().Be(0);
    }

    [Fact]
    public async Task AnAbortedTransaction_leavesNoBadge()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badges = h.Services.GetRequiredService<ForStoringBadges>();
        var runner = h.Services.GetRequiredService<ForRunningTransactions>();

        await runner.RunAsync(async ct =>
        {
            (await badges.InsertAsync(Draft("Weg"), ct)).IsT0.Should().BeTrue();
            return TransactionOutcome.Abort(0);
        }, Ct);

        (await h.Badges.CountDocumentsAsync(new BsonDocument(), cancellationToken: Ct)).Should().Be(0);
    }

    // ---- the document

    [Fact]
    public async Task Insert_storesTheDocumentOfTheNodeServer_withThePictureAsBinary_andTheTasksSorted()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badges = h.Services.GetRequiredService<ForStoringBadges>();
        var rule = new BadgeRule(BadgeRuleType.Minutes, [h.Mop, h.Toilet], 60);

        var inserted = (await InTransaction(h, ct => badges.InsertAsync(Draft("Dweilkampioen", rule, "example:mop", Png()), ct))).AsT0;

        var document = await h.Badges.Find(new BsonDocument("_id", ObjectId.Parse(inserted.Id))).SingleAsync(Ct);
        document.Names.Should().BeEquivalentTo("_id", "name", "description", "rule", "active", "exampleKey", "image", "createdAt", "updatedAt");
        document["rule"]["taskIds"].AsBsonArray.Select(v => v.AsObjectId.ToString()).Should().Equal(new[] { h.Mop, h.Toilet }.Order(StringComparer.Ordinal));
        (document["rule"]["type"].AsString, document["rule"]["threshold"].AsInt32, document["exampleKey"].AsString).Should().Be(("minutes", 60, "example:mop"));
        document["image"]["data"].AsBsonBinaryData.Bytes.Should().Equal(Png().Bytes);
        inserted.Image.Should().Be(Png().Info);
    }

    [Fact]
    public async Task ADocumentThatIsNotInTheShapeTheApplicationWrites_isAPortError_neverAnException()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badges = h.Services.GetRequiredService<ForStoringBadges>();
        var now = new BsonDateTime(At.UtcDateTime);
        var broken = ObjectId.GenerateNewId();
        await h.Badges.InsertOneAsync(
            new BsonDocument
            {
                { "_id", broken }, { "name", "Kapot" }, { "description", "" }, { "rule", new BsonDocument { { "type", "executions" }, { "threshold", 1 } } },
                { "active", true }, { "createdAt", now }, { "updatedAt", now },
            },
            cancellationToken: Ct);

        (await badges.ListAllAsync(null, Ct)).AsT1.Message.Should().StartWith("badges.unreadable");
        (await badges.ListAsync(null, null, 10, Ct)).AsT1.Message.Should().StartWith("badges.unreadable");
        (await badges.FindAsync(broken.ToString(), Ct)).AsT2.Message.Should().StartWith("badges.unreadable");
    }

    [Fact]
    public async Task ABadgeFromTheNodeServer_withoutOptionalFields_isRead()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badges = h.Services.GetRequiredService<ForStoringBadges>();
        var now = new BsonDateTime(At.UtcDateTime);
        var id = ObjectId.GenerateNewId();
        await h.Badges.InsertOneAsync(
            new BsonDocument
            {
                { "_id", id }, { "name", "Oud" }, { "description", "" }, { "rule", new BsonDocument { { "type", "onTimeWeeks" }, { "threshold", 4 } } },
                { "active", false }, { "createdAt", now }, { "updatedAt", now },
            },
            cancellationToken: Ct);

        var badge = (await badges.FindAsync(id.ToString(), Ct)).AsT0;

        (badge.Name, badge.Active, badge.ExampleKey, badge.Image, badge.Rule.Type, badge.Rule.Threshold).Should().Be(("Oud", false, null, null, BadgeRuleType.OnTimeWeeks, 4));
    }

    // ---- reads

    [Fact]
    public async Task List_isOldestFirst_pagedByACursor_andFilteredByActive()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badges = h.Services.GetRequiredService<ForStoringBadges>();
        foreach (var (name, minutes) in new[] { ("Drie", 2), ("Een", 0), ("Twee", 1) })
        {
            await InTransaction(h, ct => badges.InsertAsync(Draft(name, at: At.AddMinutes(minutes)), ct));
        }

        var all = (await badges.ListAsync(null, null, 10, Ct)).AsT0;
        var afterFirst = (await badges.ListAsync(null, BadgeCursor.After(all[0]), 10, Ct)).AsT0;
        var inactive = (await badges.ListAsync(false, null, 10, Ct)).AsT0;

        all.Select(b => b.Name).Should().Equal("Een", "Twee", "Drie");
        afterFirst.Select(b => b.Name).Should().Equal("Twee", "Drie");
        inactive.Should().BeEmpty();
        (await badges.ListAsync(null, null, 2, Ct)).AsT0.Should().HaveCount(2);
        (await badges.CountAsync(Ct)).AsT0.Should().Be(3);
    }

    [Fact]
    public async Task Find_byIdAndByExampleKey_andTheBadgesThatNameATask()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badges = h.Services.GetRequiredService<ForStoringBadges>();
        var named = (await InTransaction(h, ct => badges.InsertAsync(Draft("Toilet", new BadgeRule(BadgeRuleType.Executions, [h.Toilet], 1), "example:toilet"), ct))).AsT0;
        await InTransaction(h, ct => badges.InsertAsync(Draft("Alles"), ct));

        (await badges.FindAsync(named.Id, Ct)).AsT0.Name.Should().Be("Toilet");
        (await badges.FindAsync("nope", Ct)).IsT1.Should().BeTrue();
        (await badges.FindAsync(ObjectId.GenerateNewId().ToString(), Ct)).IsT1.Should().BeTrue();
        (await badges.FindByExampleKeyAsync("example:toilet", Ct)).AsT0.Id.Should().Be(named.Id);
        (await badges.FindByExampleKeyAsync("example:mop", Ct)).IsT1.Should().BeTrue();
        (await badges.FindNamingTaskAsync(h.Toilet, Ct)).AsT0.Select(b => b.Id).Should().Equal(named.Id);
        (await badges.FindNamingTaskAsync(h.Mop, Ct)).AsT0.Should().BeEmpty();
    }

    [Fact]
    public async Task TheExampleKey_isUnique_whereItIsAString()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badges = h.Services.GetRequiredService<ForStoringBadges>();
        await InTransaction(h, ct => badges.InsertAsync(Draft("Een", exampleKey: "example:toilet"), ct));
        await InTransaction(h, ct => badges.InsertAsync(Draft("Zonder een"), ct));
        await InTransaction(h, ct => badges.InsertAsync(Draft("Zonder twee"), ct));

        var runner = h.Services.GetRequiredService<ForRunningTransactions>();
        var duplicate = await runner.RunAsync(async ct => TransactionOutcome.Commit(await badges.InsertAsync(Draft("Dubbel", exampleKey: "example:toilet"), ct)), Ct);

        (await h.Badges.CountDocumentsAsync(new BsonDocument(), cancellationToken: Ct)).Should().Be(3, "badges without a key are not limited, a second badge with the key is refused");
        (duplicate.IsT2 || (duplicate.IsT0 && duplicate.AsT0.IsT1)).Should().BeTrue();
    }

    [Fact]
    public async Task TheImage_isReadOnlyByFindImage_andAnUnknownBadgeOrABadgeWithoutOneIsNotFound()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badges = h.Services.GetRequiredService<ForStoringBadges>();
        var withImage = (await InTransaction(h, ct => badges.InsertAsync(Draft("Plaatje", image: Png()), ct))).AsT0;
        var without = (await InTransaction(h, ct => badges.InsertAsync(Draft("Kaal"), ct))).AsT0;

        var image = (await badges.FindImageAsync(withImage.Id, Ct)).AsT0;

        image.Bytes.Should().Equal(Png().Bytes);
        (image.ContentType, image.Hash).Should().Be((BadgeImageType.Png, Png().Hash));
        (await badges.FindImageAsync(without.Id, Ct)).IsT1.Should().BeTrue();
        (await badges.FindImageAsync(ObjectId.GenerateNewId().ToString(), Ct)).IsT1.Should().BeTrue();
        (await badges.FindImageAsync("nope", Ct)).IsT1.Should().BeTrue();
    }

    // ---- update and delete

    [Fact]
    public async Task Update_setsTheGivenFieldsOnly_replacesAndClearsThePicture_andAnUnknownBadgeIsNotFound()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badges = h.Services.GetRequiredService<ForStoringBadges>();
        var badge = (await InTransaction(h, ct => badges.InsertAsync(Draft("Oud", new BadgeRule(BadgeRuleType.Executions, [h.Toilet], 3), image: Png()), ct))).AsT0;
        var later = At.AddHours(1);

        var renamed = (await InTransaction(h, ct => badges.UpdateAsync(badge.Id, new BadgeChanges(Name: "Nieuw", Active: false), later, ct))).AsT0;
        var cleared = (await InTransaction(h, ct => badges.UpdateAsync(badge.Id, new BadgeChanges(ClearImage: true), later, ct))).AsT0;
        var missing = await InTransaction(h, ct => badges.UpdateAsync(ObjectId.GenerateNewId().ToString(), new BadgeChanges(Name: "Weg"), later, ct));

        (renamed.Name, renamed.Active, renamed.Rule.Threshold, renamed.UpdatedAt, renamed.CreatedAt).Should().Be(("Nieuw", false, 3, later, At));
        renamed.Rule.TaskIds.Should().Equal(h.Toilet);
        renamed.Image.Should().NotBeNull();
        cleared.Image.Should().BeNull();
        (await badges.FindImageAsync(badge.Id, Ct)).IsT1.Should().BeTrue();
        missing.IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_removesTheBadge_andAnUnknownOneIsNotFound()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var badges = h.Services.GetRequiredService<ForStoringBadges>();
        var badge = (await InTransaction(h, ct => badges.InsertAsync(Draft("Weg"), ct))).AsT0;

        (await InTransaction(h, ct => badges.DeleteAsync(badge.Id, ct))).IsT0.Should().BeTrue();
        (await InTransaction(h, ct => badges.DeleteAsync(badge.Id, ct))).IsT1.Should().BeTrue();
        (await InTransaction(h, ct => badges.DeleteAsync("nope", ct))).IsT1.Should().BeTrue();
    }

    // ---- the awards

    private static BadgeAward Stored(ForStoringBadgeAwards awards, string badge, string person) =>
        awards.FindForEvaluationAsync([person], Ct).Result.AsT0.Single(a => a.BadgeId == badge);

    [Fact]
    public async Task Apply_insertsMovesAndRemoves_withTheDocumentOfTheNodeServer_andReportsWhatItDid()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var awards = h.Services.GetRequiredService<ForStoringBadgeAwards>();
        var badge = ObjectId.GenerateNewId().ToString();

        var created = (await InTransaction(h, ct => awards.ApplyAsync(new BadgeAwardPlan([new PlannedAward(badge, h.P1.Id, At)], [], []), At.AddMinutes(1), ct))).AsT0;

        var document = await h.Awards.Find(new BsonDocument("badgeId", ObjectId.Parse(badge))).SingleAsync(Ct);
        document.Names.Should().BeEquivalentTo("_id", "key", "badgeId", "personId", "awardedAt", "createdAt", "updatedAt");
        (document["key"].AsString, document["personId"].AsObjectId.ToString()).Should().Be(($"badge:{badge}:{h.P1.Id}", h.P1.Id));
        created.Should().ContainSingle().Which.Change.Should().Be(AwardChange.Created);

        var current = Stored(awards, badge, h.P1.Id);
        var moved = (await InTransaction(h, ct => awards.ApplyAsync(new BadgeAwardPlan([], [new PlannedMove(current, At.AddDays(-1))], []), At.AddMinutes(2), ct))).AsT0;
        var change = moved.Should().ContainSingle().Subject;
        (change.Change, change.Award.AwardedAt, change.Previous!.AwardedAt, change.Award.CreatedAt).Should().Be((AwardChange.Updated, At.AddDays(-1), At, At.AddMinutes(1)));

        var after = Stored(awards, badge, h.P1.Id);
        var removed = (await InTransaction(h, ct => awards.ApplyAsync(new BadgeAwardPlan([], [], [after]), At, ct))).AsT0;
        removed.Should().ContainSingle().Which.Change.Should().Be(AwardChange.Removed);
        (await h.AwardCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Apply_aMoveOrRemovalOfAnAwardThatChangedMeanwhile_isSkipped_notReported()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var awards = h.Services.GetRequiredService<ForStoringBadgeAwards>();
        var badge = ObjectId.GenerateNewId().ToString();
        await InTransaction(h, ct => awards.ApplyAsync(new BadgeAwardPlan([new PlannedAward(badge, h.P1.Id, At)], [], []), At, ct));
        var stale = Stored(awards, badge, h.P1.Id) with { AwardedAt = At.AddHours(5) };

        var result = (await InTransaction(h, ct => awards.ApplyAsync(new BadgeAwardPlan([], [new PlannedMove(stale, At.AddDays(-1))], [stale]), At, ct))).AsT0;

        result.Should().BeEmpty();
        Stored(awards, badge, h.P1.Id).AwardedAt.Should().Be(At);
    }

    [Fact]
    public async Task List_isOldestFirst_pagedByACursor_andOfOnePerson()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var awards = h.Services.GetRequiredService<ForStoringBadgeAwards>();
        var badge = ObjectId.GenerateNewId().ToString();
        await InTransaction(h, ct => awards.ApplyAsync(new BadgeAwardPlan([new PlannedAward(badge, h.P1.Id, At.AddDays(2)), new PlannedAward(badge, h.P2.Id, At.AddDays(1))], [], []), At, ct));

        var all = (await awards.ListAsync(new BadgeAwardQuery(null, 10, null), 10, Ct)).AsT0;
        var rest = (await awards.ListAsync(new BadgeAwardQuery(null, 10, BadgeAwardCursor.After(all[0])), 10, Ct)).AsT0;
        var ofP1 = (await awards.ListAsync(new BadgeAwardQuery(h.P1.Id, 10, null), 10, Ct)).AsT0;

        all.Select(a => a.PersonId).Should().Equal(h.P2.Id, h.P1.Id);
        rest.Select(a => a.PersonId).Should().Equal(h.P1.Id);
        ofP1.Select(a => a.PersonId).Should().Equal(h.P1.Id);
        (await awards.ListAsync(new BadgeAwardQuery("nope", 10, null), 10, Ct)).AsT0.Should().BeEmpty();
        (await awards.FindForEvaluationAsync([h.P2.Id], Ct)).AsT0.Should().ContainSingle();
        (await awards.FindForEvaluationAsync(null, Ct)).AsT0.Should().HaveCount(2);
        (await awards.FindForEvaluationAsync([], Ct)).AsT0.Should().BeEmpty();
    }

    // ---- the evidence

    [Fact]
    public async Task TheOnTimeWeeks_areTheBonusEntriesOfThePeople_oldestFirst()
    {
        await using var h = await BadgeHarness.StartAsync(mongo);
        var evidence = h.Services.GetRequiredService<ForReadingBadgeEvidence>();
        var entries = h.Database.GetCollection<BsonDocument>("pointEntries");
        foreach (var (kind, person, day) in new[] { ("bonus_week_ontime", h.P1.Id, 27), ("bonus_week_ontime", h.P1.Id, 20), ("bonus_week_done", h.P1.Id, 20), ("bonus_week_ontime", h.P2.Id, 20) })
        {
            await entries.InsertOneAsync(
                new BsonDocument
                {
                    { "_id", ObjectId.GenerateNewId() }, { "key", $"{kind}:{person}:{day}" }, { "kind", kind }, { "personId", ObjectId.Parse(person) }, { "amount", 3 },
                    { "date", new BsonDateTime(new DateTime(2026, 9, day, 0, 0, 0, DateTimeKind.Utc)) }, { "weekStart", BsonNull.Value }, { "createdAt", new BsonDateTime(At.UtcDateTime) }, { "updatedAt", new BsonDateTime(At.UtcDateTime) },
                },
                cancellationToken: Ct);
        }

        var ofP1 = (await evidence.FindOnTimeWeeksAsync([h.P1.Id], Ct)).AsT0;
        var everybody = (await evidence.FindOnTimeWeeksAsync(null, Ct)).AsT0;

        ofP1.Select(w => w.Date.Day).Should().Equal(20, 27);
        everybody.Should().HaveCount(3, "only the on-time week kind counts");
        (await evidence.FindOnTimeWeeksAsync([], Ct)).AsT0.Should().BeEmpty();
    }
}
