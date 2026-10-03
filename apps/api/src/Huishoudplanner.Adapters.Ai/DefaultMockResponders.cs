using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Huishoudplanner.Domain.Ai;

namespace Huishoudplanner.Adapters.Ai;

/// <summary>
/// The deterministic answers of the mock provider ("Demo" in the interface), ported one to one from the Node
/// <c>mockResponders.ts</c>. The user message of each request is the prompt payload as JSON, which is parsed back here.
/// </summary>
public static class DefaultMockResponders
{
    private static readonly int[] WeekdaysMondayFirst = [1, 2, 3, 4, 5, 6, 0];

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IReadOnlyDictionary<string, MockResponder> Responders { get; } = new Dictionary<string, MockResponder>
    {
        [AiRequestNames.ConnectionTest] = MockResponder.Fixed("{\"ok\":true}"),
        [AiRequestNames.PlanProposal] = MockResponder.From((request, _) => Serialize(DeterministicPlan(Parse(request.User)))),
        [AiRequestNames.TaskSuggestions] = MockResponder.From((request, _) => Serialize(DeterministicTaskSuggestions(Parse(request.User)))),
        [AiRequestNames.PlanExplanation] = MockResponder.From((request, _) => Serialize(DeterministicExplanation(Parse(request.User)))),
    };

    /// <summary>
    /// A plan that always passes the hard rules: each task placed perCycle times, evenly spread, never twice on a day,
    /// assigned to someone available that day (rotating between people) or to "either person" when nobody is.
    /// </summary>
    public static JsonObject DeterministicPlan(JsonElement payload)
    {
        var users = Items(payload, "users").ToList();
        var slots = new JsonArray();
        var taskIndex = 0;
        foreach (var task in Items(payload, "tasks"))
        {
            var count = Math.Min(PerCycle(task), 28);
            var offset = taskIndex * 3 % 28;
            for (var i = 0; i < count; i++)
            {
                var position = (((i * 28) / count) + offset) % 28;
                var weekIndex = position / 7;
                var weekday = WeekdaysMondayFirst[position % 7];
                string? assigneeId = null;
                for (var k = 0; k < users.Count; k++)
                {
                    var user = users[(i + taskIndex + k) % users.Count];
                    if (!user.GetProperty("unavailableWeekdays").EnumerateArray().Any(d => d.GetInt32() == weekday))
                    {
                        assigneeId = user.GetProperty("id").GetString();
                        break;
                    }
                }

                slots.Add(new JsonObject
                {
                    ["taskId"] = task.GetProperty("id").GetString(),
                    ["weekIndex"] = weekIndex,
                    ["weekday"] = weekday,
                    ["assigneeId"] = assigneeId,
                });
            }

            taskIndex++;
        }

        return new JsonObject
        {
            ["slots"] = slots,
            ["rationale"] = new JsonArray(
                "Week 1: taken zijn gelijkmatig verdeeld over de dagen.",
                "Week 2: niemand krijgt taken op een dag dat hij of zij niet kan.",
                "Week 3: herhalingen van dezelfde taak liggen ver uit elkaar.",
                "Week 4: de verdeling tussen de personen is zo gelijk mogelijk gehouden."),
        };
    }

    /// <summary>
    /// Fixed suggestions, including one with an unknown interval key and one that repeats an existing name, so the
    /// server-side filtering is exercised too.
    /// </summary>
    public static JsonObject DeterministicTaskSuggestions(JsonElement payload)
    {
        var room = payload.GetProperty("room").GetString();
        var existing = Items(payload, "existingTasks").Select(t => t.GetProperty("name").GetString()).FirstOrDefault();
        var suggestions = new JsonArray(
            new JsonObject { ["name"] = $"{room}: plinten afnemen", ["intervalKey"] = "4wk", ["durationMinutes"] = 15, ["notes"] = "Vochtige doek." },
            new JsonObject { ["name"] = $"{room}: lampen afstoffen", ["intervalKey"] = "quarter", ["durationMinutes"] = 10, ["notes"] = string.Empty },
            new JsonObject { ["name"] = $"{room}: gordijnen wassen", ["intervalKey"] = "twice-a-year", ["durationMinutes"] = 60 });
        if (!string.IsNullOrEmpty(existing))
        {
            suggestions.Add(new JsonObject { ["name"] = existing, ["intervalKey"] = "1w", ["durationMinutes"] = 20 });
        }

        return new JsonObject { ["suggestions"] = suggestions };
    }

    public static JsonObject DeterministicExplanation(JsonElement payload)
    {
        var slots = Items(payload, "slots").ToList();
        var rationale = new JsonArray();
        for (var week = 0; week < 4; week++)
        {
            var count = slots.Count(s => s.GetProperty("weekIndex").GetInt32() == week);
            rationale.Add(string.Create(CultureInfo.InvariantCulture, $"Week {week + 1}: {count} taken, verdeeld over de week."));
        }

        return new JsonObject { ["rationale"] = rationale };
    }

    private static JsonElement Parse(string user)
    {
        try
        {
            return JsonDocument.Parse(user).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ModelUnavailableException("Mock provider could not read the request payload");
        }
    }

    private static JsonElement[] Items(JsonElement payload, string property) =>
        payload.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray()] : [];

    private static int PerCycle(JsonElement task) =>
        task.TryGetProperty("perCycle", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private static string Serialize(JsonObject value) => value.ToJsonString(Compact);
}
