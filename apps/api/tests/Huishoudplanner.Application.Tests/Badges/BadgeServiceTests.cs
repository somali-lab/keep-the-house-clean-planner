using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Errors;

namespace Huishoudplanner.Application.Tests.Badges;

/// <summary>
/// The badge definitions (<c>badges.test.ts</c>: definitions, images, examples, the deleted task, the badge limit and the history): every result
/// variant, the audit input and "no write on a no-op". The awards a change causes are in <c>BadgeAwardServiceTests</c> and the integration tests.
/// </summary>
public sealed class BadgeServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 0];
    private static readonly byte[] Jpeg = [0xff, 0xd8, 0xff, 0xe0, 0, 0];

    private static BadgeImageInput Image(byte[] bytes, string type) => new(type, Convert.ToBase64String(bytes));

    private static BadgeRuleInput Rule(int threshold, params string[] tasks) => new(BadgeRuleType.Executions, tasks, threshold);

    private static CreateBadgeCommand Create(string name, BadgeRuleInput? rule = null, BadgeImageInput? image = null, bool? active = null, string? description = null) =>
        new(name, description, rule ?? Rule(1), active, image);

    private static async Task<Badge> Created(BadgeWorld w, CreateBadgeCommand command) => (await w.Service.CreateAsync(BadgeWorld.Admin, command, Ct)).AsT0;

    // ---- create

    [Fact]
    public async Task Create_storesATrimmedBadge_withTheDefaults_andAuditsItOnce()
    {
        var w = new BadgeWorld();

        var badge = await Created(w, Create("  Toiletjuffrouw  ", Rule(10, w.Toilet.Id)));

        badge.Should().BeEquivalentTo(new { Name = "Toiletjuffrouw", Description = "", Active = true, ExampleKey = (string?)null, Image = (BadgeImageInfo?)null, CreatedAt = BadgeWorld.Now });
        badge.Rule.TaskIds.Should().Equal(w.Toilet.Id);
        var entry = w.Audit.Entries.Should().ContainSingle().Subject;
        (entry.Entity, entry.Action, entry.EntityId, entry.Actor).Should().Be((AuditEntity.Badge, AuditAction.Create, badge.Id, AuditActor.From(BadgeWorld.Admin)));
        entry.After["name"].Should().Be(new AuditString("Toiletjuffrouw"));
        entry.After["active"].Should().Be(new AuditBool(true));
        entry.Meta.Should().BeNull();
    }

    [Fact]
    public async Task Create_keepsTheTasksOfTheRuleOnceAndInAStableOrder()
    {
        var w = new BadgeWorld();

        var badge = await Created(w, Create("Dubbel", Rule(3, w.Mop.Id, w.Toilet.Id, w.Mop.Id)));

        badge.Rule.TaskIds.Should().Equal(new[] { w.Toilet.Id, w.Mop.Id }.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Create_withAnImage_storesTheBytes_andRecordsOnlyItsTypeSizeAndHash()
    {
        var w = new BadgeWorld();

        var badge = await Created(w, Create("Plaatje", image: Image(Png, "image/png")));

        badge.Image.Should().BeEquivalentTo(new { ContentType = BadgeImageType.Png, Size = Png.Length });
        w.Badges.Images[badge.Id].Bytes.Should().Equal(Png);
        var after = w.Audit.Entries.Single().After["image"].Should().BeOfType<AuditObject>().Subject;
        after.Keys.Should().BeEquivalentTo("contentType", "size", "hash");
    }

    [Fact]
    public async Task Create_isFollowedByTheEvaluationOfTheAwards_andNeverFailsBecauseOfIt()
    {
        var w = new BadgeWorld();
        w.Awards.Failure = new PortError("badgeAwards.failed: down");

        var result = await w.Service.CreateAsync(BadgeWorld.Admin, Create("Alles"), Ct);

        result.IsT0.Should().BeTrue("the badge is committed; a failing evaluation is logged and repaired by the next reconciliation");
        w.Badges.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Create_withInvalidFields_isAValidationErrorKeyedByField_andWritesNothing()
    {
        var w = new BadgeWorld();

        var result = await w.Service.CreateAsync(
            BadgeWorld.Admin,
            new CreateBadgeCommand(new string('x', 61), new string('y', 201), new BadgeRuleInput(BadgeRuleType.Minutes, ["nope"], 0), null, new BadgeImageInput("image/gif", "AAAA")),
            Ct);

        result.AsT1.Errors.Keys.Should().BeEquivalentTo("name", "description", "rule.threshold", "rule.taskIds", "image.contentType");
        w.Badges.Items.Should().BeEmpty();
        w.Audit.Entries.Should().BeEmpty();
        w.Transactions.Runs.Should().Be(0);
    }

    [Fact]
    public async Task Create_aRuleThatNamesOnlyUnknownTasks_isRefused_butUnknownOnesAreDroppedFromAnOtherwiseKnownRule()
    {
        var w = new BadgeWorld();
        const string gone = "ffffffffffffffffffffffff";

        var refused = await w.Service.CreateAsync(BadgeWorld.Admin, Create("Weg", Rule(1, gone)), Ct);
        var half = await Created(w, Create("Half", Rule(2, w.Toilet.Id, gone)));

        refused.AsT1.Errors["rule.taskIds"].Should().Equal(BadgeValidation.UnknownTask);
        half.Rule.TaskIds.Should().Equal(w.Toilet.Id);
        w.Badges.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Create_atTheLimit_isRefusedWith409Data_andWritesNothing()
    {
        var w = new BadgeWorld();
        for (var i = 0; i < 100; i++)
        {
            w.Seed($"Badge {i}", BadgeRule.OnTimeWeeks(1), active: false);
        }

        var result = await w.Service.CreateAsync(BadgeWorld.Admin, Create("Te veel"), Ct);

        result.AsT2.Limit.Should().Be(100);
        w.Badges.Items.Should().HaveCount(100);
        w.Audit.Entries.Should().BeEmpty();
        w.Transactions.Aborts.Should().Be(1);
    }

    [Fact]
    public async Task Create_aFailingAuditWrite_leavesNoBadge()
    {
        var w = new BadgeWorld();
        w.Audit.Failure = new PortError("audit down");

        var result = await w.Service.CreateAsync(BadgeWorld.Admin, Create("Alles"), Ct);

        result.IsT4.Should().BeTrue();
        w.Badges.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_aConflictOfTheTransaction_isReturned()
    {
        var w = new BadgeWorld();
        w.Transactions.ConflictInsteadOfRunning = new ConflictError("write_conflict", "kept winning");

        var result = await w.Service.CreateAsync(BadgeWorld.Admin, Create("Alles"), Ct);

        result.AsT3.Code.Should().Be("write_conflict");
    }

    // ---- update

    [Fact]
    public async Task Update_changesTheGivenFields_andAuditsTheChangedFieldsOnly()
    {
        var w = new BadgeWorld();
        var badge = w.Seed("Toiletjuffrouw", BadgeWorld.Executions(10, w.Toilet.Id));

        var result = await w.Service.UpdateAsync(BadgeWorld.Admin, badge.Id, new BadgePatch("Toiletkoningin", Rule: Rule(12, w.Toilet.Id)), Ct);

        result.AsT0.Should().BeEquivalentTo(new { Name = "Toiletkoningin", UpdatedAt = BadgeWorld.Now });
        result.AsT0.Rule.Threshold.Should().Be(12);
        var entry = w.Entries(AuditEntity.Badge, AuditAction.Update).Should().ContainSingle().Subject;
        entry.Before.Should().Be(AuditObject.Of(("name", "Toiletjuffrouw"), ("rule", AuditObject.Of(("threshold", 10)))));
        entry.After.Should().Be(AuditObject.Of(("name", "Toiletkoningin"), ("rule", AuditObject.Of(("threshold", 12)))));
    }

    [Fact]
    public async Task Update_thatChangesNothing_writesAndAuditsNothing_andEvaluatesNothing()
    {
        var w = new BadgeWorld();
        var badge = w.Seed("Orde", BadgeWorld.Executions(3, w.Toilet.Id, w.Mop.Id));
        var writes = w.Badges.Writes;

        var result = await w.Service.UpdateAsync(
            BadgeWorld.Admin,
            badge.Id,
            new BadgePatch("Orde", Rule: Rule(3, w.Mop.Id, w.Toilet.Id), Active: true),
            Ct);

        result.AsT0.Should().Be(badge);
        w.Badges.Writes.Should().Be(writes);
        w.Audit.Entries.Should().BeEmpty();
        w.Awards.Applies.Should().Be(0);
    }

    [Fact]
    public async Task Update_theSamePictureAgain_isNothing_andRemovingItIsAChange()
    {
        var w = new BadgeWorld();
        var badge = await Created(w, Create("Plaatje", image: Image(Png, "image/png")));
        w.Audit.Entries.Clear();
        var writes = w.Badges.Writes;

        var same = await w.Service.UpdateAsync(BadgeWorld.Admin, badge.Id, new BadgePatch(Image: new BadgeImageChange(Image(Png, "image/png"))), Ct);
        var removed = await w.Service.UpdateAsync(BadgeWorld.Admin, badge.Id, new BadgePatch(Image: new BadgeImageChange(null)), Ct);

        same.AsT0.Image.Should().Be(badge.Image);
        w.Badges.Writes.Should().Be(writes + 1);
        removed.AsT0.Image.Should().BeNull();
        w.Badges.Images.Should().BeEmpty();
        w.Audit.Entries.Should().ContainSingle().Which.Before["image"].Should().BeOfType<AuditObject>();
    }

    [Fact]
    public async Task Update_aChangedPicture_isAuditedByItsHash_neverByItsBytes()
    {
        var w = new BadgeWorld();
        var badge = await Created(w, Create("Plaatje", image: Image(Png, "image/png")));
        w.Audit.Entries.Clear();

        var result = await w.Service.UpdateAsync(BadgeWorld.Admin, badge.Id, new BadgePatch(Image: new BadgeImageChange(Image(Jpeg, "image/jpeg"))), Ct);

        result.AsT0.Image!.Hash.Should().NotBe(badge.Image!.Hash);
        var entry = w.Audit.Entries.Single();
        entry.Before["image"].Should().BeOfType<AuditObject>().Which["hash"].Should().Be(new AuditString(badge.Image.Hash));
        entry.After["image"].Should().BeOfType<AuditObject>().Which["contentType"].Should().Be(new AuditString("image/jpeg"));
        w.Badges.Images[badge.Id].Bytes.Should().Equal(Jpeg);
    }

    [Theory]
    [InlineData("rule")]
    [InlineData("active")]
    public async Task Update_ofTheRuleOrTheActiveFlag_evaluatesTheAwardsAgain(string field)
    {
        var w = new BadgeWorld();
        var badge = w.Seed("Toilet", BadgeWorld.Executions(field == "active" ? 1 : 5, w.Toilet.Id));
        w.Done(BadgeWorld.P1, w.Toilet.Id, BadgeWorld.Now);
        if (field == "active")
        {
            w.SeedAward(badge, BadgeWorld.P1, BadgeWorld.Now);
        }

        var patch = field == "rule" ? new BadgePatch(Rule: Rule(1, w.Toilet.Id)) : new BadgePatch(Active: false);
        var result = await w.Service.UpdateAsync(BadgeWorld.Admin, badge.Id, patch, Ct);

        result.IsT0.Should().BeTrue();
        w.Entries(AuditEntity.BadgeAward, AuditAction.Recompute).Should().ContainSingle().Which.Meta!["trigger"].Should().Be(new AuditString("badge"));
    }

    [Fact]
    public async Task Update_ofTheName_doesNotTouchTheAwards()
    {
        var w = new BadgeWorld();
        var badge = w.Seed("Toilet", BadgeWorld.Executions(1, w.Toilet.Id));

        await w.Service.UpdateAsync(BadgeWorld.Admin, badge.Id, new BadgePatch("Anders"), Ct);

        w.Awards.Applies.Should().Be(0);
        w.Evidence.ExecutionReads.Should().Be(0);
    }

    [Fact]
    public async Task Update_ofAnUnknownOrMalformedBadge_isNotFoundOrAValidationError()
    {
        var w = new BadgeWorld();

        var unknown = await w.Service.UpdateAsync(BadgeWorld.Admin, "ffffffffffffffffffffffff", new BadgePatch("Weg"), Ct);
        var malformed = await w.Service.UpdateAsync(BadgeWorld.Admin, "nope", new BadgePatch("Weg"), Ct);

        unknown.IsT1.Should().BeTrue();
        malformed.AsT2.Errors.Should().ContainKey("id");
        w.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_aRuleOfOnlyUnknownTasks_isRefused_andNothingIsWritten()
    {
        var w = new BadgeWorld();
        var badge = w.Seed("Half", BadgeWorld.Executions(2, w.Toilet.Id));

        var result = await w.Service.UpdateAsync(BadgeWorld.Admin, badge.Id, new BadgePatch(Rule: Rule(2, "ffffffffffffffffffffffff")), Ct);

        result.AsT2.Errors["rule.taskIds"].Should().Equal(BadgeValidation.UnknownTask);
        w.Badges.Items.Single().Should().Be(badge);
    }

    [Fact]
    public async Task Update_withInvalidFields_writesNothing()
    {
        var w = new BadgeWorld();
        var badge = w.Seed("Ok", BadgeWorld.Executions(1));

        var result = await w.Service.UpdateAsync(BadgeWorld.Admin, badge.Id, new BadgePatch(Name: "  ", Rule: Rule(0)), Ct);

        result.AsT2.Errors.Keys.Should().BeEquivalentTo("name", "rule.threshold");
        w.Transactions.Runs.Should().Be(0);
    }

    // ---- delete

    [Fact]
    public async Task Delete_removesTheBadge_audits_itsRemovedFields_andWithdrawsTheAwardsWithItsName()
    {
        var w = new BadgeWorld();
        var badge = w.Seed("Toiletjuffrouw", BadgeWorld.Executions(1, w.Toilet.Id));
        w.SeedAward(badge, BadgeWorld.P1, BadgeWorld.Now);
        w.Done(BadgeWorld.P1, w.Toilet.Id, BadgeWorld.Now);

        var result = await w.Service.DeleteAsync(BadgeWorld.Admin, badge.Id, Ct);

        result.IsT0.Should().BeTrue();
        w.Badges.Items.Should().BeEmpty();
        w.Awards.Items.Should().BeEmpty();
        w.Entries(AuditEntity.Badge, AuditAction.Delete).Should().ContainSingle().Which.Before["name"].Should().Be(new AuditString("Toiletjuffrouw"));
        var summary = w.Entries(AuditEntity.BadgeAward, AuditAction.Recompute).Should().ContainSingle().Subject;
        var changes = ((AuditArray)summary.Meta!["changes"]!).Items.Cast<AuditObject>().Single();
        (changes["change"], changes["badgeName"]).Should().Be((new AuditString("removed"), new AuditString("Toiletjuffrouw")));
    }

    [Fact]
    public async Task Delete_ofAnUnknownBadge_isNotFound_andAMalformedIdAValidationError()
    {
        var w = new BadgeWorld();

        (await w.Service.DeleteAsync(BadgeWorld.Admin, "ffffffffffffffffffffffff", Ct)).IsT1.Should().BeTrue();
        (await w.Service.DeleteAsync(BadgeWorld.Admin, "nope", Ct)).IsT2.Should().BeTrue();
        w.Audit.Entries.Should().BeEmpty();
    }

    // ---- examples

    [Fact]
    public async Task AddExamples_createsTheThreeExamplesInOrder_withTheTasksTheyAreAbout_andAuditsEachWithItsKey()
    {
        var w = new BadgeWorld();

        var added = (await w.Service.AddExamplesAsync(BadgeWorld.Admin, BadgeLanguage.Nl, Ct)).AsT0;

        added.Skipped.Should().Be(0);
        added.Created.Select(b => (b.Name, b.ExampleKey, b.Active)).Should().Equal(
            ("Alles op tijd", "example:on_time", true),
            ("Toiletjuffrouw", "example:toilet", true),
            ("Dweilkampioen", "example:mop", true));
        added.Created[0].Rule.Should().BeEquivalentTo(BadgeRule.OnTimeWeeks(4));
        added.Created[1].Rule.Should().BeEquivalentTo(new { Type = BadgeRuleType.Executions, TaskIds = new[] { w.Toilet.Id }, Threshold = 10 });
        added.Created[2].Rule.Should().BeEquivalentTo(new { Type = BadgeRuleType.Minutes, TaskIds = new[] { w.Mop.Id }, Threshold = 300 });
        var entries = w.Entries(AuditEntity.Badge, AuditAction.Create).ToList();
        entries.Should().HaveCount(3);
        entries[1].Meta.Should().Be(AuditObject.Of(("example", "example:toilet")));
    }

    [Fact]
    public async Task AddExamples_again_createsNothing_writesNothing_andAuditsNothing()
    {
        var w = new BadgeWorld();
        await w.Service.AddExamplesAsync(BadgeWorld.Admin, BadgeLanguage.Nl, Ct);
        var writes = w.Badges.Writes;
        var entries = w.Audit.Entries.Count;

        var again = (await w.Service.AddExamplesAsync(BadgeWorld.Admin, BadgeLanguage.En, Ct)).AsT0;

        again.Created.Should().BeEmpty();
        again.Skipped.Should().Be(3);
        w.Badges.Writes.Should().Be(writes);
        w.Audit.Entries.Should().HaveCount(entries);
        w.Badges.Items.Should().HaveCount(3);
    }

    [Fact]
    public async Task AddExamples_doesNotBringARenamedExampleBackAsADuplicate()
    {
        var w = new BadgeWorld();
        var created = (await w.Service.AddExamplesAsync(BadgeWorld.Admin, BadgeLanguage.Nl, Ct)).AsT0.Created;
        var toilet = created.Single(b => b.ExampleKey == "example:toilet");
        var renamed = (await w.Service.UpdateAsync(BadgeWorld.Admin, toilet.Id, new BadgePatch("Toiletkoningin", Rule: Rule(2, w.Toilet.Id)), Ct)).AsT0;

        var again = (await w.Service.AddExamplesAsync(BadgeWorld.Admin, BadgeLanguage.Nl, Ct)).AsT0;

        renamed.ExampleKey.Should().Be("example:toilet");
        again.Should().BeEquivalentTo(new { Skipped = 3 });
        w.Badges.Items.Select(b => b.Name).Should().Equal("Alles op tijd", "Toiletkoningin", "Dweilkampioen");
    }

    [Fact]
    public async Task AddExamples_inEnglish_writesEnglishNames()
    {
        var w = new BadgeWorld();

        var added = (await w.Service.AddExamplesAsync(BadgeWorld.Admin, BadgeLanguage.En, Ct)).AsT0;

        added.Created.Select(b => b.Name).Should().Equal("Always on time", "Toilet Champion", "Mop Champion");
    }

    [Fact]
    public async Task AddExamples_aboutTasksThatDoNotExist_areInactive_soTheyCannotCountEveryTask()
    {
        var w = new BadgeWorld();
        w.Tasks.Items.Clear();

        var added = (await w.Service.AddExamplesAsync(BadgeWorld.Admin, BadgeLanguage.Nl, Ct)).AsT0;

        added.Created.Select(b => (b.ExampleKey, b.Active)).Should().Equal(("example:on_time", true), ("example:toilet", false), ("example:mop", false));
        added.Created[1].Rule.TaskIds.Should().BeEmpty();
    }

    [Fact]
    public async Task AddExamples_findsTheirTasksAmongTheActiveTasksOnly()
    {
        var w = new BadgeWorld();
        w.Tasks.Items.Clear();
        w.NewTask("Toilet schoonmaken", active: false);
        var wc = w.NewTask("WC poetsen");

        var added = (await w.Service.AddExamplesAsync(BadgeWorld.Admin, BadgeLanguage.Nl, Ct)).AsT0;

        added.Created.Single(b => b.ExampleKey == "example:toilet").Rule.TaskIds.Should().Equal(wc.Id);
    }

    [Fact]
    public async Task AddExamples_storesTheTasksOfAnExampleInTheStableOrder()
    {
        var w = new BadgeWorld();
        var second = w.NewTask("WC poetsen");

        var added = (await w.Service.AddExamplesAsync(BadgeWorld.Admin, BadgeLanguage.Nl, Ct)).AsT0;

        added.Created.Single(b => b.ExampleKey == "example:toilet").Rule.TaskIds.Should().Equal(new[] { w.Toilet.Id, second.Id }.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AddExamples_atTheLimit_isRefused_andNothingIsCreated()
    {
        var w = new BadgeWorld();
        for (var i = 0; i < 100; i++)
        {
            w.Seed($"Badge {i}", BadgeRule.OnTimeWeeks(1), active: false);
        }

        var result = await w.Service.AddExamplesAsync(BadgeWorld.Admin, BadgeLanguage.Nl, Ct);

        result.AsT1.Limit.Should().Be(100);
        w.Badges.Items.Should().HaveCount(100);
        w.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task AddExamples_evaluatesTheAwardsOnlyWhenSomethingWasCreated()
    {
        var w = new BadgeWorld();

        await w.Service.AddExamplesAsync(BadgeWorld.Admin, BadgeLanguage.Nl, Ct);
        var runs = w.Transactions.Runs;
        await w.Service.AddExamplesAsync(BadgeWorld.Admin, BadgeLanguage.Nl, Ct);

        w.Transactions.Runs.Should().Be(runs + 1, "only the example transaction ran the second time, no reconciliation followed");
    }

    // ---- a deleted task leaves the rules

    [Fact]
    public async Task RemoveTaskFromRules_removesTheTask_deactivatesARuleLeftWithoutTasks_andAuditsBothAsTaskDeleted()
    {
        var w = new BadgeWorld();
        var both = w.Seed("Twee taken", BadgeWorld.Executions(5, w.Toilet.Id, w.Mop.Id));
        var only = w.Seed("Eén taak", BadgeWorld.Executions(1, w.Toilet.Id), minutesAfter: 1);
        var onTime = w.Seed("Op tijd", BadgeRule.OnTimeWeeks(2), minutesAfter: 2);
        var other = w.Seed("Dweilen", BadgeWorld.Executions(1, w.Mop.Id), minutesAfter: 3);
        w.SeedAward(only, BadgeWorld.P1, BadgeWorld.Now);
        w.Done(BadgeWorld.P1, w.Toilet.Id, BadgeWorld.Now);

        var result = await w.Service.RemoveTaskFromRulesAsync(BadgeWorld.Admin, w.Toilet.Id, Ct);

        result.IsT0.Should().BeTrue();
        w.Badges.Items.Single(b => b.Id == both.Id).Should().BeEquivalentTo(new { Active = true, Rule = new { TaskIds = new[] { w.Mop.Id }, Threshold = 5 } });
        w.Badges.Items.Single(b => b.Id == only.Id).Should().BeEquivalentTo(new { Active = false, Rule = new { TaskIds = Array.Empty<string>() } });
        w.Badges.Items.Single(b => b.Id == onTime.Id).Should().Be(onTime);
        w.Badges.Items.Single(b => b.Id == other.Id).Should().Be(other);
        var updates = w.Entries(AuditEntity.Badge, AuditAction.Update).ToList();
        updates.Should().HaveCount(2);
        updates.Should().OnlyContain(e => e.Meta!["reason"] is AuditString && ((AuditString)e.Meta["reason"]!).Value == "task_deleted" && e.Meta["taskId"] is AuditObjectId);
        w.Awards.Items.Should().BeEmpty("the badge without tasks is inactive, so its award goes");
    }

    [Fact]
    public async Task RemoveTaskFromRules_ofATaskNoRuleNames_changesNothing()
    {
        var w = new BadgeWorld();
        w.Seed("Dweilen", BadgeWorld.Executions(1, w.Mop.Id));

        var result = await w.Service.RemoveTaskFromRulesAsync(BadgeWorld.Admin, w.Toilet.Id, Ct);

        result.IsT0.Should().BeTrue();
        w.Audit.Entries.Should().BeEmpty();
        w.Transactions.Runs.Should().Be(1);
    }

    [Fact]
    public async Task RemoveTaskFromRules_withAMalformedId_isAValidationError()
    {
        var w = new BadgeWorld();

        (await w.Service.RemoveTaskFromRulesAsync(BadgeWorld.Admin, "nope", Ct)).IsT1.Should().BeTrue();
    }

    // ---- reads

    [Fact]
    public async Task List_isPagedOldestFirst_withAnOpaqueCursor()
    {
        var w = new BadgeWorld();
        for (var i = 0; i < 5; i++)
        {
            w.Seed($"Badge {i}", BadgeWorld.Executions(1), minutesAfter: i);
        }

        var first = (await w.Service.ListAsync(null, 2, null, Ct)).AsT0;
        var second = (await w.Service.ListAsync(null, 2, first.NextCursor, Ct)).AsT0;
        var last = (await w.Service.ListAsync(null, 2, second.NextCursor, Ct)).AsT0;

        first.Items.Select(b => b.Name).Should().Equal("Badge 0", "Badge 1");
        second.Items.Select(b => b.Name).Should().Equal("Badge 2", "Badge 3");
        last.Items.Select(b => b.Name).Should().Equal("Badge 4");
        last.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task List_canBeLimitedToActiveOrInactiveBadges()
    {
        var w = new BadgeWorld();
        w.Seed("Aan", BadgeWorld.Executions(1));
        w.Seed("Uit", BadgeWorld.Executions(1), active: false, minutesAfter: 1);

        (await w.Service.ListAsync(true, null, null, Ct)).AsT0.Items.Select(b => b.Name).Should().Equal("Aan");
        (await w.Service.ListAsync(false, null, null, Ct)).AsT0.Items.Select(b => b.Name).Should().Equal("Uit");
    }

    [Theory]
    [InlineData(0, null, "limit")]
    [InlineData(101, null, "limit")]
    [InlineData(null, "garbage", "cursor")]
    public async Task List_refusesABadLimitOrCursor(int? limit, string? cursor, string field)
    {
        var w = new BadgeWorld();

        (await w.Service.ListAsync(null, limit, cursor, Ct)).AsT1.Errors.Should().ContainKey(field);
    }

    [Fact]
    public async Task Awards_arePagedOldestFirst_andCanBeLimitedToOnePerson()
    {
        var w = new BadgeWorld();
        var badge = w.Seed("Toilet", BadgeWorld.Executions(1));
        w.SeedAward(badge, BadgeWorld.P1, BadgeWorld.Now.AddDays(-1));
        w.SeedAward(badge, BadgeWorld.P2, BadgeWorld.Now.AddDays(-2));

        var all = (await w.Service.AwardsAsync(null, 1, null, Ct)).AsT0;
        var rest = (await w.Service.AwardsAsync(null, 1, all.NextCursor, Ct)).AsT0;
        var ofP1 = (await w.Service.AwardsAsync(BadgeWorld.P1, null, null, Ct)).AsT0;

        all.Items.Single().PersonId.Should().Be(BadgeWorld.P2);
        rest.Items.Single().PersonId.Should().Be(BadgeWorld.P1);
        rest.NextCursor.Should().BeNull();
        ofP1.Items.Single().PersonId.Should().Be(BadgeWorld.P1);
    }

    [Fact]
    public async Task Awards_refuseABadPersonLimitOrCursor()
    {
        var w = new BadgeWorld();

        var result = await w.Service.AwardsAsync("nope", 0, "garbage", Ct);

        result.AsT1.Errors.Keys.Should().BeEquivalentTo("personId", "limit", "cursor");
    }

    [Fact]
    public async Task Progress_showsTheStandingOfAPersonOnTheActiveBadges_alsoWhenNotEarnedYet()
    {
        var w = new BadgeWorld();
        var toilet = w.Seed("Toilet", BadgeWorld.Executions(3, w.Toilet.Id));
        w.Seed("Inactief", BadgeWorld.Executions(1), active: false, minutesAfter: 1);
        w.Done(BadgeWorld.P1, w.Toilet.Id, BadgeWorld.Now);
        w.Done(BadgeWorld.P1, w.Mop.Id, BadgeWorld.Now);
        w.Done(BadgeWorld.P2, w.Toilet.Id, BadgeWorld.Now);

        var progress = (await w.Service.ProgressAsync(BadgeWorld.P1, Ct)).AsT0;

        progress.PersonId.Should().Be(BadgeWorld.P1);
        progress.Items.Should().Equal(new BadgeProgressItem(toilet.Id, 1, 3, null));
        w.Evidence.ExecutionReadsFor.Should().ContainSingle().Which.Should().Equal(BadgeWorld.P1);
    }

    [Fact]
    public async Task Progress_ofABadgeThatIsEarned_carriesTheMoment_andCanExceedTheThreshold()
    {
        var w = new BadgeWorld();
        var badge = w.Seed("Toilet", BadgeWorld.Executions(1, w.Toilet.Id));
        w.Done(BadgeWorld.P1, w.Toilet.Id, BadgeWorld.Now.AddDays(1));
        w.Done(BadgeWorld.P1, w.Toilet.Id, BadgeWorld.Now);

        var item = (await w.Service.ProgressAsync(BadgeWorld.P1, Ct)).AsT0.Items.Single();

        item.Should().Be(new BadgeProgressItem(badge.Id, 2, 1, BadgeWorld.Now));
    }

    [Fact]
    public async Task Progress_readsNoExecutions_forBadgesThatDoNotCountThem()
    {
        var w = new BadgeWorld();
        w.Seed("Op tijd", BadgeRule.OnTimeWeeks(2));
        w.Evidence.Weeks.Add(new OnTimeWeek(BadgeWorld.P1, BadgeWorld.Now));

        var item = (await w.Service.ProgressAsync(BadgeWorld.P1, Ct)).AsT0.Items.Single();

        item.Current.Should().Be(1);
        w.Evidence.ExecutionReads.Should().Be(0);
        w.Evidence.WeekReads.Should().Be(1);
    }

    [Fact]
    public async Task Progress_withAMalformedPersonId_isAValidationError()
    {
        var w = new BadgeWorld();

        (await w.Service.ProgressAsync("nope", Ct)).AsT1.Errors.Should().ContainKey("personId");
    }

    [Fact]
    public async Task Image_isServedFromTheStore_andAnUnknownOrMalformedBadgeIsNotFound()
    {
        var w = new BadgeWorld();
        var badge = await Created(w, Create("Plaatje", image: Image(Png, "image/png")));

        var found = (await w.Service.ImageAsync(badge.Id, Ct)).AsT0;
        var missing = await w.Service.ImageAsync("ffffffffffffffffffffffff", Ct);
        var malformed = await w.Service.ImageAsync("nope", Ct);

        found.Bytes.Should().Equal(Png);
        missing.IsT1.Should().BeTrue();
        malformed.IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task AStoreFailure_isAPortError_ForEveryRead()
    {
        var w = new BadgeWorld();
        w.Badges.Failure = new PortError("down");
        w.Awards.Failure = new PortError("down");

        (await w.Service.ListAsync(null, null, null, Ct)).IsT2.Should().BeTrue();
        (await w.Service.AwardsAsync(null, null, null, Ct)).IsT2.Should().BeTrue();
        (await w.Service.ProgressAsync(BadgeWorld.P1, Ct)).IsT2.Should().BeTrue();
        (await w.Service.ImageAsync(BadgeWorld.P1, Ct)).IsT2.Should().BeTrue();
    }
}
