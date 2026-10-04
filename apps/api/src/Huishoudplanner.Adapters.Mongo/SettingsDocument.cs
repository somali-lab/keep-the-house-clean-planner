using System.Globalization;
using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Settings;
using MongoDB.Bson;

namespace Huishoudplanner.Adapters.Mongo;

/// <summary>
/// The <c>settings</c> document as the Node server writes it (apps/server/src/data/settings.ts): day keys are strings, the schedule
/// and the intervals are arrays of sub-documents, ids inside <c>dismissedPromotions</c> are <c>ObjectId</c>. Mapped by hand rather than
/// with a class map: the document has many optional parts, a write sets only the fields that changed, and everything the .NET
/// application does not know stays untouched, so the Node application keeps reading what the .NET one wrote.
/// </summary>
internal static class SettingsDocument
{
    public static readonly ObjectId SingletonId = new(SettingsIds.Singleton);

    public static HouseholdSettings Read(BsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new HouseholdSettings(
            CycleAnchorDate: Day(document["cycleAnchorDate"]),
            WeekStartsOn: document["weekStartsOn"].ToInt32(),
            Timezone: document["timezone"].AsString,
            VacationRanges: [.. Array(document, "vacationRanges").Select(r => new VacationRange(Day(r["from"]), Day(r["to"])))],
            Intervals: [.. Array(document, "intervals").Select(ReadInterval)],
            AiProvider: ReadProvider(document["aiProvider"].AsBsonDocument),
            AiPrompts: Optional(document, "aiPrompts", ReadPrompts),
            AiPromptTemplates: Optional(document, "aiPromptTemplates", ReadTemplates),
            CompletionControl: document.TryGetValue("completionControl", out var control) && control.IsString ? ReadControl(control.AsString) : null,
            PromoteThreshold: document["promoteThreshold"].ToInt32(),
            DismissedPromotions: [.. Array(document, "dismissedPromotions").Select(ReadPromotion)],
            BonusSchedule: document.TryGetValue("bonusSchedule", out var schedule) && schedule.IsBsonArray
                ? [.. schedule.AsBsonArray.Select(r => ReadRow(r.AsBsonDocument))]
                : null,
            BonusFloor: document.TryGetValue("bonusFloor", out var floor) && floor.IsString ? Day(floor) : null,
            CurrencyCode: document.TryGetValue("currencyCode", out var currency) && currency.IsString ? currency.AsString : null,
            CentsPerPoint: document.TryGetValue("centsPerPoint", out var cents) && cents.IsNumeric ? cents.ToInt32() : null,
            RewardGoals: Optional(document, "rewardGoals", g => new RewardGoals(NullableInt(g, "weekPoints"), NullableInt(g, "cyclePoints"))),
            CreatedAt: Instant(document, "createdAt"),
            UpdatedAt: Instant(document, "updatedAt"),
            Version: EntityVersioning.VersionOf(document));
    }

    /// <summary>The whole document of a new installation: the fields in the order of the Node server, optional fields only when set.</summary>
    public static BsonDocument ToDocument(HouseholdSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var document = new BsonDocument
        {
            { "_id", SingletonId },
            { "cycleAnchorDate", DayString(settings.CycleAnchorDate) },
            { "weekStartsOn", settings.WeekStartsOn },
            { "timezone", settings.Timezone },
            { "vacationRanges", VacationRanges(settings.VacationRanges) },
            { "intervals", Intervals(settings.Intervals) },
            { "aiProvider", Provider(settings.AiProvider) },
        };
        AddIf(document, "aiPrompts", settings.AiPrompts is { } prompts ? Prompts(prompts) : null);
        AddIf(document, "aiPromptTemplates", settings.AiPromptTemplates is { } templates ? Templates(templates) : null);
        AddIf(document, "completionControl", settings.CompletionControl is { } control ? SettingsAudit.ControlName(control) : null);
        document.Add("promoteThreshold", settings.PromoteThreshold);
        document.Add("dismissedPromotions", new BsonArray(settings.DismissedPromotions.Select(Promotion)));
        AddIf(document, "bonusSchedule", settings.BonusSchedule is { } schedule ? BonusSchedule(schedule) : null);
        AddIf(document, "bonusFloor", settings.BonusFloor is { } floor ? DayString(floor) : null);
        AddIf(document, "currencyCode", settings.CurrencyCode);
        AddIf(document, "centsPerPoint", settings.CentsPerPoint is { } cents ? new BsonInt32(cents) : null);
        AddIf(document, "rewardGoals", settings.RewardGoals is { } goals ? Goals(goals) : null);
        document.Add("createdAt", new BsonDateTime(settings.CreatedAt.UtcDateTime));
        document.Add("updatedAt", new BsonDateTime(settings.UpdatedAt.UtcDateTime));
        document.Add(EntityVersioning.Field, settings.Version);
        return document;
    }

    /// <summary>One <c>$set</c> element per changed field.</summary>
    public static IEnumerable<KeyValuePair<string, BsonValue>> ToSets(SettingsChanges changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.CycleAnchorDate is { } anchor)
        {
            yield return new("cycleAnchorDate", DayString(anchor));
        }

        if (changes.VacationRanges is { } ranges)
        {
            yield return new("vacationRanges", VacationRanges(ranges));
        }

        if (changes.Intervals is { } intervals)
        {
            yield return new("intervals", Intervals(intervals));
        }

        if (changes.AiProvider is { } provider)
        {
            yield return new("aiProvider", Provider(provider));
        }

        if (changes.AiPrompts is { } prompts)
        {
            yield return new("aiPrompts", Prompts(prompts));
        }

        if (changes.AiPromptTemplates is { } templates)
        {
            yield return new("aiPromptTemplates", Templates(templates));
        }

        if (changes.CompletionControl is { } control)
        {
            yield return new("completionControl", SettingsAudit.ControlName(control));
        }

        if (changes.PromoteThreshold is { } threshold)
        {
            yield return new("promoteThreshold", threshold);
        }

        if (changes.BonusSchedule is { } schedule)
        {
            yield return new("bonusSchedule", BonusSchedule(schedule));
        }

        if (changes.CurrencyCode is { } currency)
        {
            yield return new("currencyCode", currency);
        }

        if (changes.CentsPerPoint is { } cents)
        {
            yield return new("centsPerPoint", cents);
        }

        if (changes.RewardGoals is { } goals)
        {
            yield return new("rewardGoals", Goals(goals));
        }

        if (changes.DismissedPromotions is { } dismissed)
        {
            yield return new("dismissedPromotions", new BsonArray(dismissed.Select(Promotion)));
        }
    }

    public static DateOnly ReadAnchor(BsonDocument document) => Day(document["cycleAnchorDate"]);

    // ---- reading ---------------------------------------------------------------------------------------------------

    private static DateOnly Day(BsonValue value) => DateOnly.ParseExact(value.AsString, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTimeOffset Instant(BsonDocument document, string name) =>
        new(DateTime.SpecifyKind(document[name].ToUniversalTime(), DateTimeKind.Utc));

    private static IEnumerable<BsonDocument> Array(BsonDocument document, string name) =>
        document.TryGetValue(name, out var value) && value.IsBsonArray ? value.AsBsonArray.Select(v => v.AsBsonDocument) : [];

    private static T? Optional<T>(BsonDocument document, string name, Func<BsonDocument, T> read)
        where T : class =>
        document.TryGetValue(name, out var value) && value.IsBsonDocument ? read(value.AsBsonDocument) : null;

    private static int? NullableInt(BsonDocument document, string name) =>
        document.TryGetValue(name, out var value) && value.IsNumeric ? value.ToInt32() : null;

    private static string? OptionalString(BsonDocument document, string name) =>
        document.TryGetValue(name, out var value) && value.IsString ? value.AsString : null;

    private static Interval ReadInterval(BsonDocument interval) => new(
        interval["key"].AsString,
        interval["label"].AsString,
        NullableInt(interval, "perCycle"),
        interval["periodDays"].ToInt32());

    private static AiProviderSettings ReadProvider(BsonDocument provider) => new(
        ReadProviderType(provider["type"].AsString),
        OptionalString(provider, "endpoint"),
        OptionalString(provider, "model"),
        NullableInt(provider, "timeoutSeconds"));

    private static AiProviderType ReadProviderType(string name) => name switch
    {
        "none" => AiProviderType.None,
        "mock" => AiProviderType.Mock,
        "anthropic" => AiProviderType.Anthropic,
        "openai-compatible" => AiProviderType.OpenAiCompatible,
        "ollama" => AiProviderType.Ollama,
        _ => throw new FormatException($"Unknown AI provider type '{name}'."),
    };

    private static CompletionControl ReadControl(string name) => name switch
    {
        "circle" => CompletionControl.Circle,
        "thumb" => CompletionControl.Thumb,
        _ => throw new FormatException($"Unknown completion control '{name}'."),
    };

    private static AiPrompts ReadPrompts(BsonDocument prompts) => new(
        prompts["planProposal"].AsString,
        prompts["planRebalance"].AsString,
        prompts["taskSuggestions"].AsString,
        prompts["planExplanation"].AsString);

    private static AiPromptTemplate ReadTemplate(BsonValue template) => new(template["system"].AsString, template["user"].AsString);

    private static AiPromptTemplates ReadTemplates(BsonDocument templates) => new(
        ReadTemplate(templates["planProposal"]),
        ReadTemplate(templates["planRebalance"]),
        ReadTemplate(templates["taskSuggestions"]),
        ReadTemplate(templates["planExplanation"]));

    private static DismissedPromotion ReadPromotion(BsonDocument promotion) => new(
        promotion["planId"].AsObjectId.ToString(),
        promotion["taskId"].AsObjectId.ToString(),
        promotion["weekIndex"].ToInt32(),
        promotion["weekday"].ToInt32(),
        promotion["toWeekday"].ToInt32(),
        promotion.TryGetValue("toAssigneeId", out var assignee) && assignee.IsObjectId ? assignee.AsObjectId.ToString() : null,
        promotion["lastEvidenceId"].AsObjectId.ToString());

    private static BonusScheduleRow ReadRow(BsonDocument row) => new(
        Day(row["from"]),
        new BonusAmounts(row["weekDone"].ToInt32(), row["weekOnTime"].ToInt32(), row["cycleDone"].ToInt32(), row["cycleOnTime"].ToInt32()));

    // ---- writing ---------------------------------------------------------------------------------------------------

    private static string DayString(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static void AddIf(BsonDocument document, string name, BsonValue? value)
    {
        if (value is not null)
        {
            document.Add(name, value);
        }
    }

    private static BsonArray VacationRanges(IEnumerable<VacationRange> ranges) =>
        new(ranges.Select(r => new BsonDocument { { "from", DayString(r.From) }, { "to", DayString(r.To) } }));

    private static BsonArray Intervals(IEnumerable<Interval> intervals) => new(intervals.Select(i => new BsonDocument
    {
        { "key", i.Key },
        { "label", i.Label },
        { "perCycle", i.PerCycle is { } perCycle ? new BsonInt32(perCycle) : BsonNull.Value },
        { "periodDays", i.PeriodDays },
    }));

    private static BsonDocument Provider(AiProviderSettings provider)
    {
        var document = new BsonDocument { { "type", SettingsAudit.ProviderName(provider.Type) } };
        AddIf(document, "endpoint", provider.Endpoint);
        AddIf(document, "model", provider.Model);
        AddIf(document, "timeoutSeconds", provider.TimeoutSeconds is { } timeout ? new BsonInt32(timeout) : null);
        return document;
    }

    private static BsonDocument Prompts(AiPrompts prompts) => new()
    {
        { "planProposal", prompts.PlanProposal },
        { "planRebalance", prompts.PlanRebalance },
        { "taskSuggestions", prompts.TaskSuggestions },
        { "planExplanation", prompts.PlanExplanation },
    };

    private static BsonDocument Template(AiPromptTemplate template) => new() { { "system", template.System }, { "user", template.User } };

    private static BsonDocument Templates(AiPromptTemplates templates) => new()
    {
        { "planProposal", Template(templates.PlanProposal) },
        { "planRebalance", Template(templates.PlanRebalance) },
        { "taskSuggestions", Template(templates.TaskSuggestions) },
        { "planExplanation", Template(templates.PlanExplanation) },
    };

    private static BsonDocument Promotion(DismissedPromotion promotion) => new()
    {
        { "planId", ObjectId.Parse(promotion.PlanId) },
        { "taskId", ObjectId.Parse(promotion.TaskId) },
        { "weekIndex", promotion.WeekIndex },
        { "weekday", promotion.Weekday },
        { "toWeekday", promotion.ToWeekday },
        { "toAssigneeId", promotion.ToAssigneeId is { } id ? new BsonObjectId(ObjectId.Parse(id)) : BsonNull.Value },
        { "lastEvidenceId", ObjectId.Parse(promotion.LastEvidenceId) },
    };

    private static BsonArray BonusSchedule(IEnumerable<BonusScheduleRow> rows) => new(rows.Select(r => new BsonDocument
    {
        { "from", DayString(r.From) },
        { "weekDone", r.Amounts.WeekDone },
        { "weekOnTime", r.Amounts.WeekOnTime },
        { "cycleDone", r.Amounts.CycleDone },
        { "cycleOnTime", r.Amounts.CycleOnTime },
    }));

    private static BsonDocument Goals(RewardGoals goals) => new()
    {
        { "weekPoints", goals.WeekPoints is { } week ? new BsonInt32(week) : BsonNull.Value },
        { "cyclePoints", goals.CyclePoints is { } cycle ? new BsonInt32(cycle) : BsonNull.Value },
    };
}
