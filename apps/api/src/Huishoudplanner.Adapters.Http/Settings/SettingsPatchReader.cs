using System.Text.Json;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Adapters.Http.Settings;

/// <summary>
/// Reads the JSON body of <c>PATCH /settings</c> into a <see cref="SettingsPatch"/>. Only the shape is checked here (the JSON type of each
/// field, required parts, the enum names, the day key format); the rules (ranges, placeholders, currency) belong to
/// <see cref="SettingsRules"/>. Every problem is collected with its zod style path (<c>vacationRanges.0.to</c>) so the answer names all
/// of them. Unknown fields are ignored, like zod strips them.
/// </summary>
internal sealed class SettingsPatchReader
{
    private const string RequiredMessage = "required";
    private const string ExpectedObject = "expected_object";
    private const string ExpectedArray = "expected_array";
    private const string ExpectedString = "expected_string";
    private const string ExpectedInteger = "expected_integer";
    private const string InvalidDayKey = "invalid_day_key";
    private const string InvalidEnum = "invalid_enum";

    private readonly Dictionary<string, List<string>> errors = new(StringComparer.Ordinal);

    public static (SettingsPatch? Patch, ValidationErrors? Errors) Read(JsonElement body)
    {
        var reader = new SettingsPatchReader();
        var patch = reader.ReadPatch(body);
        return reader.errors.Count == 0
            ? (patch, null)
            : (null, new ValidationErrors(reader.errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal)));
    }

    private SettingsPatch ReadPatch(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            Fail("body", ExpectedObject);
            return new SettingsPatch();
        }

        return new SettingsPatch
        {
            CycleAnchorDate = OptionalValue(body, "cycleAnchorDate", ReadDay),
            VacationRanges = OptionalRef(body, "vacationRanges", (e, path) => ReadArray(e, path, ReadVacationRange)),
            Intervals = OptionalRef(body, "intervals", (e, path) => ReadArray(e, path, ReadInterval)),
            AiProvider = OptionalRef(body, "aiProvider", ReadProvider),
            AiPrompts = OptionalRef(body, "aiPrompts", ReadPrompts),
            AiPromptTemplates = OptionalRef(body, "aiPromptTemplates", ReadTemplates),
            CompletionControl = OptionalValue(body, "completionControl", ReadControl),
            PromoteThreshold = OptionalValue(body, "promoteThreshold", ReadInt),
            PeriodBonuses = OptionalRef(body, "periodBonuses", ReadAmounts),
            CurrencyCode = OptionalRef(body, "currencyCode", ReadText),
            CentsPerPoint = OptionalValue(body, "centsPerPoint", ReadInt),
            RewardGoals = OptionalRef(body, "rewardGoals", ReadGoals),
        };
    }

    private void Fail(string path, string message)
    {
        if (!errors.TryGetValue(path, out var list))
        {
            errors[path] = list = [];
        }

        list.Add(message);
    }

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    /// <summary>A field that may be absent; a present field (also an explicit null) must have the right type.</summary>
    private static T? OptionalValue<T>(JsonElement parent, string name, Func<JsonElement, string, T?> read)
        where T : struct => parent.TryGetProperty(name, out var element) ? read(element, name) : null;

    private static T? OptionalRef<T>(JsonElement parent, string name, Func<JsonElement, string, T?> read, string path = "")
        where T : class => parent.TryGetProperty(name, out var element) ? read(element, Join(path, name)) : null;

    private T? Required<T>(JsonElement parent, string name, Func<JsonElement, string, T?> read, string path)
        where T : class
    {
        if (parent.TryGetProperty(name, out var element))
        {
            return read(element, Join(path, name));
        }

        Fail(Join(path, name), RequiredMessage);
        return null;
    }

    private DateOnly? ReadDay(JsonElement element, string path)
    {
        var text = ReadText(element, path);
        if (text is null)
        {
            return null;
        }

        if (!DayKeys.IsDayKey(text))
        {
            Fail(path, InvalidDayKey);
            return null;
        }

        return DayKeys.Parse(text);
    }

    private string? ReadText(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        Fail(path, ExpectedString);
        return null;
    }

    private int? ReadInt(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
        {
            return value;
        }

        Fail(path, ExpectedInteger);
        return null;
    }

    /// <summary>An integer or an explicit null; a missing key is a problem (<c>required</c>).</summary>
    private (bool Ok, int? Value) ReadNullableIntRequired(JsonElement parent, string name, string path)
    {
        var at = Join(path, name);
        if (!parent.TryGetProperty(name, out var element))
        {
            Fail(at, RequiredMessage);
            return (false, null);
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return (true, null);
        }

        var value = ReadInt(element, at);
        return (value is not null, value);
    }

    private List<T>? ReadArray<T>(JsonElement element, string path, Func<JsonElement, string, T?> readItem)
        where T : class
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            Fail(path, ExpectedArray);
            return null;
        }

        var items = new List<T>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (readItem(item, $"{path}.{index}") is { } read)
            {
                items.Add(read);
            }

            index++;
        }

        return items;
    }

    private bool ExpectObject(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        Fail(path, ExpectedObject);
        return false;
    }

    private VacationRange? ReadVacationRange(JsonElement element, string path)
    {
        if (!ExpectObject(element, path))
        {
            return null;
        }

        var from = RequiredDay(element, "from", path);
        var to = RequiredDay(element, "to", path);
        return from is { } f && to is { } t ? new VacationRange(f, t) : null;
    }

    private DateOnly? RequiredDay(JsonElement parent, string name, string path)
    {
        if (parent.TryGetProperty(name, out var element))
        {
            return ReadDay(element, Join(path, name));
        }

        Fail(Join(path, name), RequiredMessage);
        return null;
    }

    private string? RequiredText(JsonElement parent, string name, string path)
    {
        if (parent.TryGetProperty(name, out var element))
        {
            return ReadText(element, Join(path, name));
        }

        Fail(Join(path, name), RequiredMessage);
        return null;
    }

    private int? RequiredInt(JsonElement parent, string name, string path)
    {
        if (parent.TryGetProperty(name, out var element))
        {
            return ReadInt(element, Join(path, name));
        }

        Fail(Join(path, name), RequiredMessage);
        return null;
    }

    private Interval? ReadInterval(JsonElement element, string path)
    {
        if (!ExpectObject(element, path))
        {
            return null;
        }

        // Key and label are trimmed before they are checked and stored, like the zod schema does.
        var key = RequiredText(element, "key", path)?.Trim();
        var label = RequiredText(element, "label", path)?.Trim();
        var (perCycleOk, perCycle) = ReadNullableIntRequired(element, "perCycle", path);
        var periodDays = RequiredInt(element, "periodDays", path);
        return key is not null && label is not null && perCycleOk && periodDays is { } days ? new Interval(key, label, perCycle, days) : null;
    }

    private AiProviderSettings? ReadProvider(JsonElement element, string path)
    {
        if (!ExpectObject(element, path))
        {
            return null;
        }

        AiProviderType? type = null;
        if (RequiredText(element, "type", path) is { } name)
        {
            type = SettingsWire.ParseProvider(name);
            if (type is null)
            {
                Fail(Join(path, "type"), InvalidEnum);
            }
        }

        var endpoint = OptionalRef(element, "endpoint", ReadText, path);
        var model = OptionalRef(element, "model", ReadText, path);
        var timeout = element.TryGetProperty("timeoutSeconds", out var timeoutElement) ? ReadInt(timeoutElement, Join(path, "timeoutSeconds")) : null;
        return type is { } t ? new AiProviderSettings(t, endpoint, model, timeout) : null;
    }

    private AiPrompts? ReadPrompts(JsonElement element, string path)
    {
        if (!ExpectObject(element, path))
        {
            return null;
        }

        var proposal = RequiredText(element, "planProposal", path);
        var rebalance = RequiredText(element, "planRebalance", path);
        var suggestions = RequiredText(element, "taskSuggestions", path);
        var explanation = RequiredText(element, "planExplanation", path);
        return proposal is not null && rebalance is not null && suggestions is not null && explanation is not null
            ? new AiPrompts(proposal, rebalance, suggestions, explanation)
            : null;
    }

    private AiPromptTemplate? ReadTemplate(JsonElement element, string path)
    {
        if (!ExpectObject(element, path))
        {
            return null;
        }

        var system = RequiredText(element, "system", path);
        var user = RequiredText(element, "user", path);
        return system is not null && user is not null ? new AiPromptTemplate(system, user) : null;
    }

    private AiPromptTemplates? ReadTemplates(JsonElement element, string path)
    {
        if (!ExpectObject(element, path))
        {
            return null;
        }

        var proposal = Required(element, "planProposal", ReadTemplate, path);
        var rebalance = Required(element, "planRebalance", ReadTemplate, path);
        var suggestions = Required(element, "taskSuggestions", ReadTemplate, path);
        var explanation = Required(element, "planExplanation", ReadTemplate, path);
        return proposal is not null && rebalance is not null && suggestions is not null && explanation is not null
            ? new AiPromptTemplates(proposal, rebalance, suggestions, explanation)
            : null;
    }

    private CompletionControl? ReadControl(JsonElement element, string path)
    {
        if (ReadText(element, path) is not { } name)
        {
            return null;
        }

        if (SettingsWire.ParseControl(name) is { } control)
        {
            return control;
        }

        Fail(path, InvalidEnum);
        return null;
    }

    private BonusAmounts? ReadAmounts(JsonElement element, string path)
    {
        if (!ExpectObject(element, path))
        {
            return null;
        }

        var weekDone = RequiredInt(element, "weekDone", path);
        var weekOnTime = RequiredInt(element, "weekOnTime", path);
        var cycleDone = RequiredInt(element, "cycleDone", path);
        var cycleOnTime = RequiredInt(element, "cycleOnTime", path);
        return weekDone is { } wd && weekOnTime is { } wo && cycleDone is { } cd && cycleOnTime is { } co
            ? new BonusAmounts(wd, wo, cd, co)
            : null;
    }

    private RewardGoals? ReadGoals(JsonElement element, string path)
    {
        if (!ExpectObject(element, path))
        {
            return null;
        }

        var (weekOk, week) = ReadNullableIntRequired(element, "weekPoints", path);
        var (cycleOk, cycle) = ReadNullableIntRequired(element, "cyclePoints", path);
        return weekOk && cycleOk ? new RewardGoals(week, cycle) : null;
    }
}
