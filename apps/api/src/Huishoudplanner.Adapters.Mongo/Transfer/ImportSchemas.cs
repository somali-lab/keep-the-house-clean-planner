using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Limits;
using Huishoudplanner.Domain.Settings;
using MongoDB.Bson;
using static Huishoudplanner.Adapters.Mongo.Transfer.Shape;

namespace Huishoudplanner.Adapters.Mongo.Transfer;

/// <summary>
/// The shape of every document of an export file, as the Node server stores and exports it (<c>DOC_SCHEMAS</c> and <c>TYPED_FIELDS</c> of
/// <c>domain/transfer.ts</c>, over the schemas of <c>packages/shared/src/schemas</c>). The limits come from <see cref="HouseholdLimits"/>, which pins them to
/// the TypeScript constants.
/// </summary>
internal static class ImportSchemas
{
    private static readonly HouseholdLimits Limits = HouseholdLimits.Current;

    private static readonly Field CreatedAt = Req("createdAt", Date);

    private static readonly Field UpdatedAt = Req("updatedAt", Date);

    private static readonly Check Weekday = Int(0, 6);

    private static readonly Check WeekIndex = Int(0, 3);

    private static readonly Check Points = Int(Limits.Tasks.MinPoints, Limits.Tasks.MaxPoints);

    public static IReadOnlyDictionary<string, DocShape> Collections { get; } = new Dictionary<string, DocShape>(StringComparer.Ordinal)
    {
        [MongoCollections.Settings] = SettingsShape(),
        [MongoCollections.Users] = UserShape(),
        [MongoCollections.Rooms] = RoomShape(),
        [MongoCollections.Tasks] = TaskShape(),
        [MongoCollections.CyclePlans] = CyclePlanShape(),
        [MongoCollections.Cycles] = CycleShape(),
        [MongoCollections.Occurrences] = OccurrenceShape(),
        [MongoCollections.PointEntries] = RedemptionShape(),
        [MongoCollections.Badges] = BadgeShape(),
        [MongoCollections.AuditLog] = AuditShape(),
    };

    private static DocShape Document(params Field[] fields) => new(fields);

    private static Check DailyBudget => Doc(Req("weekday", Int(0)), Req("weekend", Int(0)));

    private static DocShape UserShape() => Document(
        Req("_id", Id),
        Req("name", NonBlank),
        Req("color", Matching(HexColor(), "invalid_color")),
        Req("active", Bool),
        Opt("role", Choice("admin", "planner", "member")),
        Req("unavailableWeekdays", Items(Weekday)),
        Req("dailyBudgetMinutes", DailyBudget),
        Opt("maxDailyMinutes", DailyBudget),
        Opt("browserNotifications", Doc(
            Req("enabled", Bool),
            Req("times", Items(Matching(TimeOfDay(), "invalid_time"), Limits.Notifications.MaxBrowserTimes, DuplicateStrings("duplicate_time"))))),
        CreatedAt,
        UpdatedAt);

    private static DocShape RoomShape() => Document(
        Req("_id", Id),
        Req("name", NonBlank),
        Req("sortOrder", Int()),
        Req("active", Bool),
        Req("virtual", Bool),
        CreatedAt,
        UpdatedAt);

    private static DocShape TaskShape() => Document(
        Req("_id", Id),
        Req("name", NonBlank),
        Req("roomId", Id),
        Req("intervalKey", Str(s => s.Length == 0 ? "required" : null)),
        Req("durationMinutes", Int(Limits.Tasks.MinDurationMinutes)),
        Opt("points", Points),
        Req("defaultAssigneeId", Nullable(Id)),
        Req("active", Bool),
        Req("notes", Text),
        Req("tags", Items(Text)),
        Req("lastCompletedAt", Nullable(Date)),
        CreatedAt,
        UpdatedAt);

    private static DocShape CyclePlanShape() => Document(
        Req("_id", Id),
        Req("name", NonBlank),
        Req("active", Bool),
        Req("slots", Items(Doc(
            Req("taskId", Id),
            Req("weekIndex", WeekIndex),
            Req("weekday", Weekday),
            Req("assigneeId", Nullable(Id)),
            Opt("sortOrder", Int())))),
        Req("weekThemes", Exactly(Limits.Calendar.PlanWeeks, Text)),
        Req("draft", Bool),
        Req("source", Choice("manual", "ai")),
        Req("proposalId", Nullable(Text)),
        Req("rationale", Nullable(Exactly(Limits.Calendar.PlanWeeks, Text))),
        Req("discarded", Bool),
        CreatedAt,
        UpdatedAt);

    private static DocShape CycleShape() => Document(
        Req("_id", Id),
        Req("index", Int()),
        Req("startDate", DayKey),
        Req("endDate", DayKey),
        Req("planId", Nullable(Id)),
        Req("generatedAt", Date),
        Req("generationRunId", Text));

    private static DocShape OccurrenceShape() => Document(
        Req("_id", Id),
        Req("taskId", Nullable(Id)),
        Req("cycleId", Id),
        Req("planId", Nullable(Id)),
        Req("date", Date),
        Req("plannedDate", Date),
        Req("assigneeId", Nullable(Id)),
        Req("status", Choice("open", "done", "skipped")),
        Req("statusBeforeCompletion", Nullable(Choice("open", "skipped"))),
        Req("completedAt", Nullable(Date)),
        Req("completedBy", Nullable(Id)),
        Req("skipReason", Nullable(Text)),
        Req("durationMinutesSnapshot", Int(Limits.Tasks.MinDurationMinutes)),
        Req("taskNameSnapshot", Text),
        Opt("roomIdSnapshot", Nullable(Id)),
        Opt("roomNameSnapshot", Nullable(Text)),
        Req("origin", Choice("generated", "adhoc")),
        Opt("recordedDone", Bool),
        Opt("requestId", Nullable(Text)),
        Opt("pointsSnapshot", Nullable(Points)),
        Opt("pointsOverride", Nullable(Points)),
        Opt("periodOwnerId", Nullable(Id)),
        CreatedAt,
        UpdatedAt);

    /// <summary>A redemption (requirements 4.12): the only kind of ledger entry that travels in an export.</summary>
    private static DocShape RedemptionShape() => Document(
        Req("_id", Id),
        Req("key", Matching(RedemptionKey(), "invalid_redemption_key")),
        Req("kind", Equal("redemption")),
        Req("personId", Id),
        Req("amount", Int(max: -1)),
        Req("date", Date),
        Req("weekStart", Date),
        Opt("periodStart", Null),
        Req("occurrenceId", Null),
        Req("taskId", Null),
        Req("titleSnapshot", Text),
        Req("source", Choice("live", "backfill", "recompute")),
        Req("note", Nullable(MaxLength(Limits.Points.MaxRedemptionNoteLength))),
        Req("centsPerPointSnapshot", Int(Limits.Points.MinCentsPerPoint, Limits.Points.MaxCentsPerPoint)),
        Opt("currencyCodeSnapshot", CurrencyCode),
        Req("requestId", Nullable(Matching(RequestKey(), "invalid_request_key"))),
        CreatedAt,
        UpdatedAt);

    /// <summary>A badge definition (ADR-0014). The bytes of its image are a real <c>Binary</c>; they are checked against size, hash and type after the shape.</summary>
    private static DocShape BadgeShape()
    {
        var taskIds = Req("taskIds", Items(Id, Limits.Badges.MaxRuleTasks));
        Check rule = (value, path, issues) =>
        {
            if (!value.IsBsonDocument)
            {
                issues.Add(path, "expected_object");
                return;
            }

            var type = value.AsBsonDocument.GetValue("type", BsonNull.Value);
            switch (type.IsString ? type.AsString : null)
            {
                case "executions" or "minutes":
                    Doc(Req("type", Text), taskIds, Req("threshold", Int(1, Limits.Badges.MaxThreshold)))(value, path, issues);
                    break;
                case "onTimeWeeks":
                    Doc(Req("type", Text), Req("threshold", Int(1, Limits.Badges.MaxOnTimeWeeksThreshold)))(value, path, issues);
                    break;
                default:
                    issues.Add(path + ".type", "invalid_enum");
                    break;
            }
        };

        return Document(
            Req("_id", Id),
            Req("name", MaxLength(Limits.Badges.MaxNameLength, nonBlank: true)),
            Req("description", MaxLength(Limits.Badges.MaxDescriptionLength)),
            Req("rule", rule),
            Req("active", Bool),
            Opt("exampleKey", Nullable(Str(s => s.Length is < 1 or > 100 ? "invalid_length" : null))),
            Opt("image", Nullable(Doc(
                Req("data", Binary),
                Req("contentType", Choice([.. Limits.Badges.ImageTypes])),
                Req("size", Int(1, Limits.Badges.MaxImageBytes)),
                Req("hash", Matching(Sha256Hex(), "invalid_hash"))))),
            CreatedAt,
            UpdatedAt);
    }

    private static DocShape AuditShape() => Document(
        Req("_id", Id),
        Req("at", Date),
        Req("actorId", Id),
        Req("entity", Choice([.. AuditNames.EntityNames])),
        Req("entityId", Id),
        Req("action", Choice([.. Enum.GetValues<AuditAction>().Select(AuditNames.ToWire)])),
        Req("before", AnyDocument),
        Req("after", AnyDocument),
        Req("source", Choice([.. AuditNames.SourceNames])),
        Opt("meta", AnyDocument));

    private static DocShape SettingsShape()
    {
        var bonusAmount = Int(Limits.Bonuses.MinPoints, Limits.Bonuses.MaxPoints);
        var goal = Nullable(Int(Limits.Rewards.MinGoalPoints, Limits.Rewards.MaxGoalPoints));
        Check vacationRange = (value, path, issues) =>
        {
            Doc(Req("from", DayKey), Req("to", DayKey))(value, path, issues);
            if (value.IsBsonDocument && value.AsBsonDocument.TryGetValue("from", out var from) && value.AsBsonDocument.TryGetValue("to", out var to)
                && from.IsString && to.IsString && DayKeys.IsDayKey(from.AsString) && DayKeys.IsDayKey(to.AsString)
                && string.CompareOrdinal(from.AsString, to.AsString) > 0)
            {
                issues.Add(path + ".to", SettingsRules.VacationRangeInverted);
            }
        };
        Check template(string placeholder, string code) => Str(s => s.Length is < 1 or > 20000 ? "invalid_length" : s.Contains(placeholder, StringComparison.Ordinal) ? null : code);
        var promptTemplate = Doc(
            Req("system", template("{{schema}}", SettingsRules.SchemaPlaceholderRequired)),
            Req("user", template("{{input}}", SettingsRules.InputPlaceholderRequired)));
        var prompt = MaxLength(Limits.Ai.PromptMaxLength);

        return Document(
            Req("_id", Id),
            Req("cycleAnchorDate", Str(s => !DayKeys.IsDayKey(s) ? "invalid_day_key" : DayKeys.IsMonday(s) ? null : SettingsRules.AnchorNotMonday)),
            Req("weekStartsOn", Int(1, 1)),
            Req("timezone", Str(s => s.Length == 0 ? "required" : null)),
            Req("vacationRanges", Items(vacationRange)),
            Req("intervals", Items(
                Doc(
                    Req("key", Str(s => s.Trim().Length == 0 ? "required" : s.Length > Limits.Tasks.IntervalKeyMaxLength ? "too_long" : null)),
                    Req("label", NonBlank),
                    Req("perCycle", Nullable(Int(1))),
                    Req("periodDays", Int(1))),
                whole: items => HasDuplicate(items, "key") ? SettingsRules.DuplicateIntervalKey : null)),
            Req("aiProvider", Doc(
                Req("type", Choice("none", "mock", "anthropic", "openai-compatible", "ollama")),
                Opt("endpoint", Str(s => Uri.TryCreate(s, UriKind.Absolute, out _) ? null : "invalid_url")),
                Opt("model", Str(s => s.Length == 0 ? "required" : null)),
                Opt("timeoutSeconds", Int(Limits.Ai.MinTimeoutSeconds, Limits.Ai.MaxTimeoutSeconds)))),
            Opt("aiPrompts", Doc(Req("planProposal", prompt), Req("planRebalance", prompt), Req("taskSuggestions", prompt), Req("planExplanation", prompt))),
            Opt("aiPromptTemplates", Doc(
                Req("planProposal", promptTemplate), Req("planRebalance", promptTemplate), Req("taskSuggestions", promptTemplate), Req("planExplanation", promptTemplate))),
            Opt("completionControl", Choice("circle", "thumb")),
            Req("promoteThreshold", Int(2)),
            Req("dismissedPromotions", Items(Doc(
                Req("planId", Id),
                Req("taskId", Id),
                Req("weekIndex", WeekIndex),
                Req("weekday", Weekday),
                Req("toWeekday", Weekday),
                Req("toAssigneeId", Nullable(Id)),
                Req("lastEvidenceId", Id)))),
            Opt("bonusSchedule", Items(
                Doc(
                    Req("weekDone", bonusAmount),
                    Req("weekOnTime", bonusAmount),
                    Req("cycleDone", bonusAmount),
                    Req("cycleOnTime", bonusAmount),
                    Req("from", DayKey)),
                whole: rows => IsSortedByFrom(rows) ? null : "bonus_schedule_not_sorted")),
            Opt("bonusFloor", DayKey),
            Opt("currencyCode", CurrencyCode),
            Opt("centsPerPoint", Int(Limits.Points.MinCentsPerPoint, Limits.Points.MaxCentsPerPoint)),
            Opt("rewardGoals", Doc(Req("weekPoints", goal), Req("cyclePoints", goal))),
            CreatedAt,
            UpdatedAt);
    }

    /// <summary>ISO 4217: three capitals the platform knows as a currency with exactly two fraction digits, because money is whole cents.</summary>
    private static Check CurrencyCode => Str(code =>
        code.Length != 3 || !code.All(char.IsAsciiLetterUpper) || !Currencies.IsKnown(code)
            ? SettingsRules.InvalidCurrencyCode
            : Currencies.HasTwoDecimals(code) ? null : SettingsRules.CurrencyNotTwoDecimals);

    private static Func<BsonArray, string?> DuplicateStrings(string code) => items =>
    {
        var texts = items.Where(i => i.IsString).Select(i => i.AsString).ToList();
        return texts.Distinct(StringComparer.Ordinal).Count() == texts.Count ? null : code;
    };

    private static bool HasDuplicate(BsonArray items, string field)
    {
        var keys = items.Where(i => i.IsBsonDocument && i.AsBsonDocument.TryGetValue(field, out var v) && v.IsString).Select(i => i.AsBsonDocument[field].AsString).ToList();
        return keys.Distinct(StringComparer.Ordinal).Count() != keys.Count;
    }

    /// <summary>Strictly ascending by <c>from</c> (a bonus schedule is sorted with unique days).</summary>
    private static bool IsSortedByFrom(BsonArray rows)
    {
        string? previous = null;
        foreach (var row in rows)
        {
            if (!row.IsBsonDocument || !row.AsBsonDocument.TryGetValue("from", out var from) || !from.IsString)
            {
                continue;
            }

            if (previous is not null && string.CompareOrdinal(previous, from.AsString) >= 0)
            {
                return false;
            }

            previous = from.AsString;
        }

        return true;
    }
}
