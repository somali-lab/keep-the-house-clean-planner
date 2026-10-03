using System.Globalization;
using System.Text.Json;
using Huishoudplanner.Domain.CyclePlans;
using OneOf;

namespace Huishoudplanner.Domain.Ai;

/// <summary>What a model proposed: the slots and one rationale sentence per week (the shape of <c>aiPlanOutputSchema</c>).</summary>
public sealed record ProposedPlan(IReadOnlyList<CyclePlanSlot> Slots, IReadOnlyList<string> Rationale);

/// <summary>A suggestion as the model wrote it, before the filtering (<c>aiTaskSuggestionsOutputSchema</c>).</summary>
public sealed record RawTaskSuggestion(string Name, string IntervalKey, double DurationMinutes, string? Notes);

/// <summary>A suggested task that passed the filtering.</summary>
public sealed record TaskSuggestion(string Name, string IntervalKey, int DurationMinutes, string Notes);

/// <summary>
/// Reads and checks what a model answered (the Zod schemas of <c>packages/shared/src/schemas/ai.ts</c>, by hand). Problems are listed as
/// <c>path: message</c> like the Node server does, so they can be fed back to the model; the message texts are this port's own.
/// </summary>
public static class AiOutputs
{
    /// <summary>The error of an answer that is no JSON, as it is fed back to the model on the re-prompt.</summary>
    public const string PlanNotJson = "The answer was not valid JSON.";

    public const string InvalidFormatMessage = "The AI answer did not match the expected format";

    private const int RationaleCount = 4;

    /// <summary>The plan answer: its slots and the four rationale sentences, or the list of problems.</summary>
    public static OneOf<ProposedPlan, IReadOnlyList<string>> ParsePlan(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (!ModelJson.Extract(raw).TryPickT0(out var root, out _))
        {
            return OneOf<ProposedPlan, IReadOnlyList<string>>.FromT1([PlanNotJson]);
        }

        var errors = new List<string>();
        var slots = new List<CyclePlanSlot>();
        var rationale = new List<string>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return OneOf<ProposedPlan, IReadOnlyList<string>>.FromT1(["(root): expected object"]);
        }

        if (!root.TryGetProperty("slots", out var slotsElement) || slotsElement.ValueKind != JsonValueKind.Array)
        {
            errors.Add("slots: expected array");
        }
        else
        {
            var index = 0;
            foreach (var item in slotsElement.EnumerateArray())
            {
                ReadSlot(item, $"slots.{index.ToString(CultureInfo.InvariantCulture)}", slots, errors);
                index++;
            }
        }

        ReadRationale(root, "rationale", rationale, errors);
        return errors.Count > 0
            ? OneOf<ProposedPlan, IReadOnlyList<string>>.FromT1(errors)
            : new ProposedPlan(slots, rationale);
    }

    /// <summary>The raw suggestions, or <see cref="AiInvalidResponse"/> when the answer is no JSON or has the wrong shape.</summary>
    public static OneOf<IReadOnlyList<RawTaskSuggestion>, AiInvalidResponse> ParseTaskSuggestions(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (!ModelJson.Extract(raw).TryPickT0(out var root, out _))
        {
            return NotJson();
        }

        var errors = new List<string>();
        var result = new List<RawTaskSuggestion>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add("(root): expected object");
        }
        else if (!root.TryGetProperty("suggestions", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            errors.Add("suggestions: expected array");
        }
        else
        {
            var index = 0;
            foreach (var item in list.EnumerateArray())
            {
                var path = $"suggestions.{index.ToString(CultureInfo.InvariantCulture)}";
                index++;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"{path}: expected object");
                    continue;
                }

                var name = RequiredString(item, "name", path, errors);
                var intervalKey = RequiredString(item, "intervalKey", path, errors);
                double? duration = null;
                if (!item.TryGetProperty("durationMinutes", out var durationElement) || durationElement.ValueKind != JsonValueKind.Number)
                {
                    errors.Add($"{path}.durationMinutes: expected number");
                }
                else
                {
                    duration = durationElement.GetDouble();
                }

                string? notes = null;
                if (item.TryGetProperty("notes", out var notesElement))
                {
                    if (notesElement.ValueKind == JsonValueKind.String)
                    {
                        notes = notesElement.GetString();
                    }
                    else
                    {
                        errors.Add($"{path}.notes: expected string");
                    }
                }

                if (name is not null && intervalKey is not null && duration is { } minutes)
                {
                    result.Add(new RawTaskSuggestion(name, intervalKey, minutes, notes));
                }
            }
        }

        return errors.Count > 0
            ? new AiInvalidResponse(InvalidFormatMessage, errors)
            : OneOf<IReadOnlyList<RawTaskSuggestion>, AiInvalidResponse>.FromT0(result);
    }

    /// <summary>The four sentences of an explanation, or <see cref="AiInvalidResponse"/>.</summary>
    public static OneOf<IReadOnlyList<string>, AiInvalidResponse> ParseExplanation(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (!ModelJson.Extract(raw).TryPickT0(out var root, out _))
        {
            return NotJson();
        }

        var errors = new List<string>();
        var rationale = new List<string>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add("(root): expected object");
        }
        else
        {
            ReadRationale(root, "rationale", rationale, errors);
        }

        return errors.Count > 0
            ? new AiInvalidResponse(InvalidFormatMessage, errors)
            : OneOf<IReadOnlyList<string>, AiInvalidResponse>.FromT0(rationale);
    }

    /// <summary>
    /// The suggestions that are usable: a known interval, a whole duration of at least a minute, a non-empty name that does not exist in the room
    /// (or earlier in the answer), compared case-insensitively. Notes are trimmed; no notes is an empty text.
    /// </summary>
    public static IReadOnlyList<TaskSuggestion> FilterSuggestions(
        IEnumerable<RawTaskSuggestion> suggestions,
        IReadOnlyCollection<string> knownIntervalKeys,
        IEnumerable<string> existingNames)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        ArgumentNullException.ThrowIfNull(knownIntervalKeys);
        ArgumentNullException.ThrowIfNull(existingNames);
        var known = new HashSet<string>(knownIntervalKeys, StringComparer.Ordinal);
        var seen = new HashSet<string>(existingNames.Select(NormaliseName), StringComparer.Ordinal);
        var result = new List<TaskSuggestion>();
        foreach (var suggestion in suggestions)
        {
            var name = suggestion.Name.Trim();
            var key = NormaliseName(name);
            if (name.Length == 0 || seen.Contains(key))
            {
                continue;
            }

            if (!known.Contains(suggestion.IntervalKey))
            {
                continue;
            }

            var minutes = suggestion.DurationMinutes;
            if (minutes != Math.Floor(minutes) || minutes < 1 || minutes > int.MaxValue)
            {
                continue;
            }

            seen.Add(key);
            result.Add(new TaskSuggestion(name, suggestion.IntervalKey, (int)minutes, suggestion.Notes?.Trim() ?? string.Empty));
        }

        return result;
    }

    private static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");

    /// <summary>The comparison form of a task name (<c>trim().toLocaleLowerCase('nl')</c>).</summary>
    public static string NormaliseName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Trim().ToLower(Dutch);
    }

    private static AiInvalidResponse NotJson() => new(ModelJson.NotJsonMessage, []);

    private static void ReadSlot(JsonElement item, string path, List<CyclePlanSlot> slots, List<string> errors)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}: expected object");
            return;
        }

        var taskId = ReadId(item, "taskId", path, nullable: false, errors, out _);
        var assigneeId = ReadId(item, "assigneeId", path, nullable: true, errors, out var assigneeOk);
        var weekIndex = ReadInteger(item, "weekIndex", path, 0, 3, required: true, errors);
        var weekday = ReadInteger(item, "weekday", path, 0, 6, required: true, errors);
        var sortOrder = ReadInteger(item, "sortOrder", path, int.MinValue, int.MaxValue, required: false, errors);
        if (taskId is not null && assigneeOk && weekIndex is { } week && weekday is { } day)
        {
            slots.Add(new CyclePlanSlot(taskId, week, day, assigneeId, sortOrder ?? 0));
        }
    }

    private static string? ReadId(JsonElement item, string name, string path, bool nullable, List<string> errors, out bool ok)
    {
        ok = false;
        if (!item.TryGetProperty(name, out var element))
        {
            errors.Add($"{path}.{name}: required");
            return null;
        }

        if (element.ValueKind == JsonValueKind.Null && nullable)
        {
            ok = true;
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{path}.{name}: expected string{(nullable ? " or null" : string.Empty)}");
            return null;
        }

        var value = element.GetString()!;
        if (!CyclePlanRules.IsId(value))
        {
            errors.Add($"{path}.{name}: invalid_object_id");
            return null;
        }

        ok = true;
        return value.ToLowerInvariant();
    }

    private static int? ReadInteger(JsonElement item, string name, string path, int min, int max, bool required, List<string> errors)
    {
        if (!item.TryGetProperty(name, out var element))
        {
            if (required)
            {
                errors.Add($"{path}.{name}: required");
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var number) || number != Math.Floor(number))
        {
            errors.Add($"{path}.{name}: expected integer");
            return null;
        }

        if (number < min || number > max)
        {
            errors.Add($"{path}.{name}: out of range {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}");
            return null;
        }

        return (int)number;
    }

    private static string? RequiredString(JsonElement item, string name, string path, List<string> errors)
    {
        if (item.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        errors.Add($"{path}.{name}: expected string");
        return null;
    }

    private static void ReadRationale(JsonElement root, string name, List<string> rationale, List<string> errors)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{name}: expected array");
            return;
        }

        var items = element.EnumerateArray().ToList();
        if (items.Count != RationaleCount)
        {
            errors.Add($"{name}: expected exactly {RationaleCount.ToString(CultureInfo.InvariantCulture)} items");
            return;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var path = $"{name}.{i.ToString(CultureInfo.InvariantCulture)}";
            if (items[i].ValueKind != JsonValueKind.String)
            {
                errors.Add($"{path}: expected string");
            }
            else if (items[i].GetString() is { Length: 0 })
            {
                errors.Add($"{path}: must not be empty");
            }
            else
            {
                rationale.Add(items[i].GetString()!);
            }
        }
    }
}
