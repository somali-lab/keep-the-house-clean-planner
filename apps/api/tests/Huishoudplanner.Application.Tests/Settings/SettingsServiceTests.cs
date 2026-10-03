using Huishoudplanner.Application.Settings;
using Huishoudplanner.Application.Tests.Tasks;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Application.Tests.Settings;

/// <summary>
/// The use cases of settings.test.ts (the scenarios that need no HTTP) with fake driven ports: every <c>OneOf</c> variant,
/// the audit input, and no write for a no-op.
/// </summary>
public class SettingsServiceTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly Actor Admin = new(new string('a', 24), Role.Admin, ActorSource.Ui);
    private static readonly BonusAmounts Amounts = new(5, 3, 20, 10);

    private sealed class Rig
    {
        public Rig(HouseholdSettings? settings = null, bool missing = false, string[]? intervalsInUse = null, string now = "2026-09-16T08:00:00Z")
        {
            Store = new FakeSettingsStore(missing ? null : settings ?? SettingsSamples.Seeded());
            Audit = new FakeAudit();
            Usage = new FakeTaskStore();
            Usage.ExtraIntervalsInUse.AddRange(intervalsInUse ?? []);
            Transactions = new FakeTransactions(Store, Audit);
            Clock = new FixedClock(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture));
            Service = new SettingsService(Store, Usage, Audit, Transactions, Clock);
        }

        public FakeSettingsStore Store { get; }

        public FakeAudit Audit { get; }

        public FakeTaskStore Usage { get; }

        public FakeTransactions Transactions { get; }

        public FixedClock Clock { get; }

        public SettingsService Service { get; }

        public async Task<SettingsView> Updated(SettingsPatch patch) => (await Service.UpdateAsync(Admin, patch, Ct)).AsT0;

        public AuditEntry OnlyEntry
        {
            get
            {
                Audit.Entries.Should().ContainSingle();
                return Audit.Entries[0];
            }
        }
    }

    private static void NothingWritten(Rig rig)
    {
        rig.Store.Writes.Should().Be(0);
        rig.Audit.Entries.Should().BeEmpty();
    }

    // ---- read -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_returns_the_seeded_settings_with_the_defaults_filled_in()
    {
        var rig = new Rig();

        var view = (await rig.Service.GetAsync(Ct)).AsT0;

        view.Id.Should().Be("000000000000000000000001");
        view.Settings.CycleAnchorDate.Should().Be(new DateOnly(2026, 9, 14));
        view.Settings.Timezone.Should().Be("Europe/Amsterdam");
        view.Settings.Intervals.Should().Equal(DueCalculator.DefaultIntervals);
        view.Settings.AiProvider.Type.Should().Be(AiProviderType.None);
        view.BonusSchedule.Should().BeEmpty();
        view.CurrencyCode.Should().Be("EUR");
        view.CentsPerPoint.Should().Be(0);
        view.RewardGoals.Should().Be(new RewardGoals(null, null));
        view.BonusesInForce.Should().Be(BonusAmounts.None);
    }

    [Fact]
    public async Task Get_flags_schedule_rows_that_start_after_today_and_names_the_amounts_in_force()
    {
        var past = new BonusScheduleRow(new DateOnly(2026, 9, 1), new BonusAmounts(1, 1, 1, 1));
        var today = new BonusScheduleRow(new DateOnly(2026, 9, 16), Amounts);
        var future = new BonusScheduleRow(new DateOnly(2026, 9, 17), new BonusAmounts(9, 9, 9, 9));
        var rig = new Rig(SettingsSamples.Seeded() with { BonusSchedule = [past, today, future] });

        var view = (await rig.Service.GetAsync(Ct)).AsT0;

        view.BonusSchedule.Select(r => r.StartsInFuture).Should().Equal(false, false, true);
        view.BonusesInForce.Should().Be(Amounts);
    }

    [Fact]
    public async Task Get_uses_the_household_timezone_for_today()
    {
        // 23:30 UTC on the 16th is already the 17th in Amsterdam, so the row of the 17th is in force.
        var row = new BonusScheduleRow(new DateOnly(2026, 9, 17), Amounts);
        var rig = new Rig(SettingsSamples.Seeded() with { BonusSchedule = [row] }, now: "2026-09-16T23:30:00Z");

        var view = (await rig.Service.GetAsync(Ct)).AsT0;

        view.BonusSchedule.Single().StartsInFuture.Should().BeFalse();
        view.BonusesInForce.Should().Be(Amounts);
    }

    [Fact]
    public async Task Get_returns_the_stored_values_instead_of_the_defaults()
    {
        var rig = new Rig(SettingsSamples.Seeded() with { CurrencyCode = "USD", CentsPerPoint = 5, RewardGoals = new RewardGoals(50, 0) });

        var view = (await rig.Service.GetAsync(Ct)).AsT0;

        (view.CurrencyCode, view.CentsPerPoint, view.RewardGoals).Should().Be(("USD", 5, new RewardGoals(50, 0)));
    }

    [Fact]
    public async Task Get_without_a_settings_document_is_settings_missing()
    {
        var rig = new Rig(missing: true);

        (await rig.Service.GetAsync(Ct)).IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task Get_passes_a_port_error_on()
    {
        var rig = new Rig();
        rig.Store.Failure = new PortError("down");

        (await rig.Service.GetAsync(Ct)).AsT2.Message.Should().Be("down");
    }

    // ---- validation -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_with_an_anchor_that_is_not_a_monday_is_a_validation_error_and_writes_nothing()
    {
        var rig = new Rig();

        var result = await rig.Service.UpdateAsync(Admin, new SettingsPatch { CycleAnchorDate = new DateOnly(2026, 9, 15) }, Ct);

        result.AsT1.Errors.Should().BeEquivalentTo(new Dictionary<string, string[]> { ["cycleAnchorDate"] = ["anchor_not_monday"] });
        NothingWritten(rig);
        rig.Transactions.Runs.Should().Be(0);
    }

    [Fact]
    public async Task Update_with_an_inverted_vacation_range_is_a_validation_error()
    {
        var rig = new Rig();
        var patch = new SettingsPatch { VacationRanges = [new VacationRange(new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 24))] };

        var result = await rig.Service.UpdateAsync(Admin, patch, Ct);

        result.AsT1.Errors.Should().ContainKey("vacationRanges.0.to").WhoseValue.Should().Equal("vacation_range_inverted");
        NothingWritten(rig);
    }

    [Fact]
    public async Task Update_with_duplicate_interval_keys_is_a_validation_error()
    {
        var rig = new Rig();
        var patch = new SettingsPatch { Intervals = [.. DueCalculator.DefaultIntervals, DueCalculator.DefaultIntervals[0]] };

        var result = await rig.Service.UpdateAsync(Admin, patch, Ct);

        result.AsT1.Errors.Should().ContainKey("intervals").WhoseValue.Should().Equal("duplicate_interval_key");
        NothingWritten(rig);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(901)]
    public async Task Update_with_an_ai_timeout_outside_10_to_900_seconds_is_a_validation_error(int timeout)
    {
        var rig = new Rig();
        var patch = new SettingsPatch { AiProvider = new AiProviderSettings(AiProviderType.Ollama, null, "qwen3:8b", timeout) };

        var result = await rig.Service.UpdateAsync(Admin, patch, Ct);

        result.AsT1.Errors.Should().ContainKey("aiProvider.timeoutSeconds");
    }

    [Fact]
    public async Task Update_with_a_prompt_template_that_lacks_a_placeholder_names_the_field_and_the_message()
    {
        var rig = new Rig();
        var ok = new AiPromptTemplate("Systeem {{schema}}", "Gebruiker {{input}}");
        var patch = new SettingsPatch
        {
            AiPromptTemplates = new AiPromptTemplates(new AiPromptTemplate("zonder schema", "Gebruiker {{input}}"), ok, new AiPromptTemplate("Systeem {{schema}}", "zonder invoer"), ok),
        };

        var result = await rig.Service.UpdateAsync(Admin, patch, Ct);

        var errors = result.AsT1.Errors;
        errors["aiPromptTemplates.planProposal.system"].Should().Equal("schema_placeholder_required");
        errors["aiPromptTemplates.taskSuggestions.user"].Should().Equal("input_placeholder_required");
        errors.Should().HaveCount(2);
    }

    // ---- writes and audit -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_changes_the_anchor_with_one_audit_entry_that_holds_the_changed_field_only()
    {
        var rig = new Rig();

        var view = await rig.Updated(new SettingsPatch { CycleAnchorDate = new DateOnly(2026, 9, 7) });

        view.Settings.CycleAnchorDate.Should().Be(new DateOnly(2026, 9, 7));
        rig.Store.Writes.Should().Be(1);
        var entry = rig.OnlyEntry;
        entry.Entity.Should().Be(AuditEntity.Settings);
        entry.Action.Should().Be(AuditAction.Update);
        entry.EntityId.Should().Be("000000000000000000000001");
        entry.Actor.Should().Be(AuditActor.From(Admin));
        entry.Before.Should().Be(AuditObject.Of(("cycleAnchorDate", "2026-09-14")));
        entry.After.Should().Be(AuditObject.Of(("cycleAnchorDate", "2026-09-07")));
        entry.Meta.Should().BeNull();
    }

    [Fact]
    public async Task Update_manages_vacation_ranges_with_an_audited_array()
    {
        var rig = new Rig();
        var range = new VacationRange(new DateOnly(2026, 12, 24), new DateOnly(2026, 12, 31));

        await rig.Updated(new SettingsPatch { VacationRanges = [range] });

        rig.OnlyEntry.Before.Should().Be(AuditObject.Of(("vacationRanges", new AuditArray([]))));
        rig.OnlyEntry.After.Should().Be(AuditObject.Of(("vacationRanges", new AuditArray([AuditObject.Of(("from", "2026-12-24"), ("to", "2026-12-31"))]))));
    }

    [Fact]
    public async Task Update_adds_a_new_interval_without_code_changes()
    {
        var rig = new Rig();
        var intervals = new List<Interval>(DueCalculator.DefaultIntervals) { SettingsSamples.Year };

        var view = await rig.Updated(new SettingsPatch { Intervals = intervals });

        view.Settings.Intervals.Should().Contain(SettingsSamples.Year);
        rig.OnlyEntry.After["intervals"].Should().BeOfType<AuditArray>().Which.Items.Should().HaveCount(intervals.Count);
        rig.Usage.IntervalReads.Should().Be(0, "nothing was removed, so no task needs to be asked");
    }

    [Fact]
    public async Task Update_audits_an_interval_without_a_count_per_cycle_with_an_explicit_null()
    {
        var rig = new Rig();

        await rig.Updated(new SettingsPatch { Intervals = [.. DueCalculator.DefaultIntervals, SettingsSamples.Year] });

        var last = rig.OnlyEntry.After["intervals"].Should().BeOfType<AuditArray>().Which.Items[^1].Should().BeOfType<AuditObject>().Subject;
        last["perCycle"].Should().BeOfType<AuditNull>();
    }

    [Fact]
    public async Task Update_blocks_removing_an_interval_that_tasks_use_and_names_the_keys()
    {
        var rig = new Rig(intervalsInUse: ["year", "1w"]);
        var withYear = SettingsSamples.Seeded() with { Intervals = [.. DueCalculator.DefaultIntervals, SettingsSamples.Year] };
        rig.Store.Document = withYear;

        var result = await rig.Service.UpdateAsync(Admin, new SettingsPatch { Intervals = DueCalculator.DefaultIntervals }, Ct);

        result.AsT2.Keys.Should().Equal("year");
        NothingWritten(rig);
        rig.Store.Document.Should().Be(withYear);
    }

    [Fact]
    public async Task Update_allows_removing_an_unused_interval()
    {
        var rig = new Rig(intervalsInUse: ["1w"]);

        var view = await rig.Updated(new SettingsPatch { Intervals = [.. DueCalculator.DefaultIntervals.Where(i => i.Key != "quarter")] });

        view.Settings.Intervals.Select(i => i.Key).Should().NotContain("quarter");
        rig.Store.Writes.Should().Be(1);
    }

    [Fact]
    public async Task Update_passes_a_failing_interval_usage_read_on_and_writes_nothing()
    {
        var rig = new Rig();
        rig.Usage.Failure = new PortError("tasks down");

        var result = await rig.Service.UpdateAsync(Admin, new SettingsPatch { Intervals = [DueCalculator.DefaultIntervals[0]] }, Ct);

        result.AsT5.Message.Should().Be("tasks down");
        NothingWritten(rig);
    }

    [Fact]
    public async Task Update_stores_an_ollama_provider_and_audits_the_nested_change_only()
    {
        var rig = new Rig();
        var provider = new AiProviderSettings(AiProviderType.Ollama, "http://host.docker.internal:11434", "qwen3:8b", 240);

        var view = await rig.Updated(new SettingsPatch { AiProvider = provider });

        view.Settings.AiProvider.Should().Be(provider);
        rig.OnlyEntry.Before.Should().Be(AuditObject.Of(("aiProvider", AuditObject.Of(("type", "none")))));
        rig.OnlyEntry.After.Should().Be(AuditObject.Of(("aiProvider", AuditObject.Of(
            ("type", "ollama"), ("endpoint", "http://host.docker.internal:11434"), ("model", "qwen3:8b"), ("timeoutSeconds", 240)))));
    }

    [Fact]
    public async Task Update_stores_prompt_templates_that_keep_their_placeholders()
    {
        var rig = new Rig();
        var template = new AiPromptTemplate("Systeem {{schema}}", "Gebruiker {{input}}");
        var templates = new AiPromptTemplates(template, template, template, template);

        var view = await rig.Updated(new SettingsPatch { AiPromptTemplates = templates });

        view.Settings.AiPromptTemplates.Should().Be(templates);
        rig.OnlyEntry.Before.Count.Should().Be(0);
        rig.OnlyEntry.After.Keys.Should().Equal("aiPromptTemplates");
    }

    [Fact]
    public async Task Update_changes_the_completion_control_and_the_promote_threshold()
    {
        var rig = new Rig();

        var view = await rig.Updated(new SettingsPatch { CompletionControl = CompletionControl.Thumb, PromoteThreshold = 3 });

        (view.Settings.CompletionControl, view.Settings.PromoteThreshold).Should().Be((CompletionControl.Thumb, 3));
        rig.OnlyEntry.Before.Should().Be(AuditObject.Of(("completionControl", "circle"), ("promoteThreshold", 2)));
        rig.OnlyEntry.After.Should().Be(AuditObject.Of(("completionControl", "thumb"), ("promoteThreshold", 3)));
    }

    [Fact]
    public async Task Update_changes_the_ai_prompts_and_audits_the_changed_prompt_only()
    {
        var rig = new Rig();
        var prompts = SettingsDefaults.AiPrompts with { PlanProposal = "Maak een plan." };

        await rig.Updated(new SettingsPatch { AiPrompts = prompts });

        rig.OnlyEntry.Before.Should().Be(AuditObject.Of(("aiPrompts", AuditObject.Of(("planProposal", SettingsDefaults.AiPrompts.PlanProposal)))));
        rig.OnlyEntry.After.Should().Be(AuditObject.Of(("aiPrompts", AuditObject.Of(("planProposal", "Maak een plan.")))));
    }

    [Fact]
    public async Task Update_never_changes_what_a_client_cannot_send()
    {
        var dismissed = new DismissedPromotion(new string('1', 24), new string('2', 24), 0, 1, 2, null, new string('3', 24));
        var rig = new Rig(SettingsSamples.Seeded() with { DismissedPromotions = [dismissed], BonusFloor = new DateOnly(2026, 8, 1) });

        var view = await rig.Updated(new SettingsPatch { PromoteThreshold = 5 });

        view.Settings.DismissedPromotions.Should().Equal(dismissed);
        view.Settings.BonusFloor.Should().Be(new DateOnly(2026, 8, 1));
        view.Settings.Timezone.Should().Be("Europe/Amsterdam");
        view.Settings.WeekStartsOn.Should().Be(1);
    }

    // ---- no-ops ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_with_an_empty_patch_writes_and_audits_nothing()
    {
        var rig = new Rig();

        var view = await rig.Updated(new SettingsPatch());

        view.Settings.Should().Be(SettingsSamples.Seeded());
        NothingWritten(rig);
    }

    [Fact]
    public async Task Update_with_values_that_equal_the_stored_ones_writes_and_audits_nothing()
    {
        var rig = new Rig();
        var patch = new SettingsPatch
        {
            PromoteThreshold = 2,
            CycleAnchorDate = new DateOnly(2026, 9, 14),
            VacationRanges = [],
            Intervals = DueCalculator.DefaultIntervals,
            AiProvider = new AiProviderSettings(AiProviderType.None),
            AiPrompts = SettingsDefaults.AiPrompts,
            CompletionControl = CompletionControl.Circle,
        };

        await rig.Updated(patch);

        NothingWritten(rig);
    }

    [Fact]
    public async Task Update_with_the_default_currency_and_cents_when_none_are_stored_writes_and_audits_nothing()
    {
        var rig = new Rig();

        await rig.Updated(new SettingsPatch { CurrencyCode = "EUR", CentsPerPoint = 0, RewardGoals = new RewardGoals(null, null) });

        NothingWritten(rig);
    }

    [Fact]
    public async Task Update_with_the_stored_currency_cents_and_goals_writes_and_audits_nothing()
    {
        var rig = new Rig(SettingsSamples.Seeded() with { CurrencyCode = "USD", CentsPerPoint = 5, RewardGoals = new RewardGoals(50, 0) });

        await rig.Updated(new SettingsPatch { CurrencyCode = "USD", CentsPerPoint = 5, RewardGoals = new RewardGoals(50, 0) });

        NothingWritten(rig);
    }

    // ---- currency, cents, goals -----------------------------------------------------------------------------------

    [Fact]
    public async Task Update_changing_currency_and_cents_is_one_settings_update_with_old_and_new_value()
    {
        var rig = new Rig();

        var view = await rig.Updated(new SettingsPatch { CurrencyCode = "USD", CentsPerPoint = 5 });

        (view.CurrencyCode, view.CentsPerPoint).Should().Be(("USD", 5));
        rig.OnlyEntry.Before.Count.Should().Be(0, "nothing was stored before, so the values appear in after only");
        rig.OnlyEntry.After.Should().Be(AuditObject.Of(("currencyCode", "USD"), ("centsPerPoint", 5)));
    }

    [Fact]
    public async Task Update_changing_the_cents_back_to_zero_after_a_value_is_audited_with_both_sides()
    {
        var rig = new Rig(SettingsSamples.Seeded() with { CentsPerPoint = 5 });

        await rig.Updated(new SettingsPatch { CentsPerPoint = 0 });

        rig.OnlyEntry.Before.Should().Be(AuditObject.Of(("centsPerPoint", 5)));
        rig.OnlyEntry.After.Should().Be(AuditObject.Of(("centsPerPoint", 0)));
    }

    [Theory]
    [InlineData("XXX", "invalid_currency_code")]
    [InlineData("eur", "invalid_currency_code")]
    [InlineData("EURO", "invalid_currency_code")]
    [InlineData("JPY", "currency_not_two_decimals")]
    [InlineData("KWD", "currency_not_two_decimals")]
    public async Task Update_refuses_a_currency_that_is_not_a_known_two_decimal_currency(string code, string message)
    {
        var rig = new Rig();

        var result = await rig.Service.UpdateAsync(Admin, new SettingsPatch { CurrencyCode = code }, Ct);

        result.AsT1.Errors["currencyCode"].Should().Equal(message);
        NothingWritten(rig);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10001)]
    public async Task Update_refuses_cents_per_point_outside_0_to_10000(int cents)
    {
        var rig = new Rig();

        var result = await rig.Service.UpdateAsync(Admin, new SettingsPatch { CentsPerPoint = cents }, Ct);

        result.AsT1.Errors.Should().ContainKey("centsPerPoint");
    }

    [Fact]
    public async Task Update_sets_reward_goals_and_audits_them_with_explicit_nulls()
    {
        var rig = new Rig();

        var view = await rig.Updated(new SettingsPatch { RewardGoals = new RewardGoals(100, null) });

        view.RewardGoals.Should().Be(new RewardGoals(100, null));
        rig.OnlyEntry.After.Should().Be(AuditObject.Of(("rewardGoals", AuditObject.Of(("weekPoints", 100), ("cyclePoints", AuditNull.Instance)))));
    }

    [Fact]
    public async Task Update_changing_one_reward_goal_audits_that_goal_only()
    {
        var rig = new Rig(SettingsSamples.Seeded() with { RewardGoals = new RewardGoals(100, null) });

        await rig.Updated(new SettingsPatch { RewardGoals = new RewardGoals(100, 400) });

        rig.OnlyEntry.Before.Should().Be(AuditObject.Of(("rewardGoals", AuditObject.Of(("cyclePoints", AuditNull.Instance)))));
        rig.OnlyEntry.After.Should().Be(AuditObject.Of(("rewardGoals", AuditObject.Of(("cyclePoints", 400)))));
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(null, 100001)]
    public async Task Update_refuses_reward_goals_outside_0_to_100000(int? week, int? cycle)
    {
        var rig = new Rig();

        var result = await rig.Service.UpdateAsync(Admin, new SettingsPatch { RewardGoals = new RewardGoals(week, cycle) }, Ct);

        result.AsT1.Errors.Should().NotBeEmpty();
    }

    // ---- period bonuses (ADR-0012) --------------------------------------------------------------------------------

    [Fact]
    public async Task Update_with_all_zero_amounts_on_an_empty_schedule_writes_and_audits_nothing()
    {
        var rig = new Rig();

        await rig.Updated(new SettingsPatch { PeriodBonuses = BonusAmounts.None });

        NothingWritten(rig);
    }

    [Fact]
    public async Task Update_writes_a_row_from_today_audited_against_an_empty_schedule()
    {
        var rig = new Rig();

        var view = await rig.Updated(new SettingsPatch { PeriodBonuses = Amounts });

        var row = new BonusScheduleRow(new DateOnly(2026, 9, 16), Amounts);
        view.Settings.BonusSchedule.Should().Equal(row);
        view.BonusesInForce.Should().Be(Amounts);
        var rowAudit = AuditObject.Of(("from", "2026-09-16"), ("weekDone", 5), ("weekOnTime", 3), ("cycleDone", 20), ("cycleOnTime", 10));
        rig.OnlyEntry.Before.Should().Be(AuditObject.Of(("bonusSchedule", new AuditArray([]))));
        rig.OnlyEntry.After.Should().Be(AuditObject.Of(("bonusSchedule", new AuditArray([rowAudit]))));
    }

    [Fact]
    public async Task Update_with_the_same_amounts_again_writes_nothing_and_a_change_replaces_the_row_that_starts_today()
    {
        var rig = new Rig();
        await rig.Updated(new SettingsPatch { PeriodBonuses = Amounts });
        rig.Store.Writes.Should().Be(1);

        await rig.Updated(new SettingsPatch { PeriodBonuses = Amounts });
        rig.Store.Writes.Should().Be(1);
        rig.Audit.Entries.Should().HaveCount(1);

        var changed = Amounts with { WeekDone = 6 };
        var view = await rig.Updated(new SettingsPatch { PeriodBonuses = changed });

        view.Settings.BonusSchedule.Should().Equal(new BonusScheduleRow(new DateOnly(2026, 9, 16), changed));
        rig.Audit.Entries.Should().HaveCount(2);
    }

    [Fact]
    public async Task Update_on_a_later_day_keeps_the_earlier_rows_so_an_ended_period_keeps_its_amounts()
    {
        var rig = new Rig();
        await rig.Updated(new SettingsPatch { PeriodBonuses = Amounts });
        rig.Clock.Now = DateTimeOffset.Parse("2026-09-30T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var next = new BonusAmounts(1, 2, 3, 4);

        var view = await rig.Updated(new SettingsPatch { PeriodBonuses = next });

        view.Settings.BonusSchedule.Should().Equal(
            new BonusScheduleRow(new DateOnly(2026, 9, 16), Amounts),
            new BonusScheduleRow(new DateOnly(2026, 9, 30), next));
    }

    [Fact]
    public async Task Update_switching_a_kind_off_is_a_row_with_a_zero()
    {
        var rig = new Rig();
        await rig.Updated(new SettingsPatch { PeriodBonuses = Amounts });
        rig.Clock.Now = DateTimeOffset.Parse("2026-09-30T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        var view = await rig.Updated(new SettingsPatch { PeriodBonuses = Amounts with { CycleOnTime = 0 } });

        view.Settings.BonusSchedule![1].Amounts.CycleOnTime.Should().Be(0);
    }

    [Theory]
    [InlineData(1001, 0, 0, 0)]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(0, 0, 0, 1001)]
    public async Task Update_refuses_amounts_outside_0_to_1000(int weekDone, int weekOnTime, int cycleDone, int cycleOnTime)
    {
        var rig = new Rig();

        var result = await rig.Service.UpdateAsync(Admin, new SettingsPatch { PeriodBonuses = new BonusAmounts(weekDone, weekOnTime, cycleDone, cycleOnTime) }, Ct);

        result.AsT1.Errors.Should().NotBeEmpty();
        NothingWritten(rig);
    }

    [Fact]
    public async Task Update_accepts_the_maximum_amount_of_1000()
    {
        var rig = new Rig();

        var view = await rig.Updated(new SettingsPatch { PeriodBonuses = new BonusAmounts(1000, 0, 0, 0) });

        view.Settings.BonusSchedule.Should().ContainSingle();
    }

    [Fact]
    public async Task Update_answers_bonus_schedule_conflict_when_concurrent_writers_keep_winning_a_schedule_write()
    {
        var rig = new Rig();
        rig.Transactions.ConflictAfterWork = new ConflictError("write_conflict", "A concurrent change won the write; retry the request.");

        var result = await rig.Service.UpdateAsync(Admin, new SettingsPatch { PeriodBonuses = Amounts }, Ct);

        result.AsT3.Code.Should().Be("bonus_schedule_conflict");
        rig.Store.Document.Should().Be(SettingsSamples.Seeded(), "the lost attempt is rolled back");
        rig.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_keeps_the_runner_conflict_code_for_a_write_that_does_not_touch_the_schedule()
    {
        var rig = new Rig();
        rig.Transactions.ConflictAfterWork = new ConflictError("write_conflict", "A concurrent change won the write; retry the request.");

        var result = await rig.Service.UpdateAsync(Admin, new SettingsPatch { PromoteThreshold = 4 }, Ct);

        result.AsT3.Code.Should().Be("write_conflict");
    }

    // ---- failures -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_without_a_settings_document_is_settings_missing()
    {
        var rig = new Rig(missing: true);

        var result = await rig.Service.UpdateAsync(Admin, new SettingsPatch { PromoteThreshold = 3 }, Ct);

        result.IsT4.Should().BeTrue();
        rig.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_passes_a_store_port_error_on()
    {
        var rig = new Rig();
        rig.Store.Failure = new PortError("down");

        var result = await rig.Service.UpdateAsync(Admin, new SettingsPatch { PromoteThreshold = 3 }, Ct);

        result.AsT5.Message.Should().Be("down");
    }

    [Fact]
    public async Task Update_rolls_the_settings_back_when_the_audit_entry_cannot_be_recorded()
    {
        var rig = new Rig();
        rig.Audit.Failure = new PortError("audit down");

        var result = await rig.Service.UpdateAsync(Admin, new SettingsPatch { PromoteThreshold = 3 }, Ct);

        result.AsT5.Message.Should().Be("audit down");
        rig.Store.Document.Should().Be(SettingsSamples.Seeded(), "the entity write and its audit entry commit together or not at all");
    }

    [Fact]
    public async Task Update_runs_the_read_and_the_write_inside_one_transaction()
    {
        var rig = new Rig();

        await rig.Updated(new SettingsPatch { PromoteThreshold = 3 });

        rig.Transactions.Runs.Should().Be(1);
    }
}
