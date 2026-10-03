using System.Text.Json;
using System.Text.Json.Nodes;
using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Planning;
using Huishoudplanner.Domain.Tests.Support;

namespace Huishoudplanner.Domain.Tests.Planning;

/// <summary>Runs every case of Vectors/validation.json against <see cref="PlanValidator"/>.</summary>
public class PlanValidationVectorTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static TheoryData<VectorCase> Cases => VectorCase.Load("validation");

    [Theory]
    [MemberData(nameof(Cases))]
    public void Vector_case_gives_the_expected_result(VectorCase vectorCase) =>
        VectorRunner.Run(vectorCase, Dispatch);

    [Fact]
    public void The_vector_file_has_all_65_cases() => Cases.Count.Should().Be(65);

    private static object? Dispatch(VectorCase c) => c.Function switch
    {
        "isWeekendDay" => PlanValidator.IsWeekendDay(c.WholeNumber("weekday")),
        "budgetFor" => PlanValidator.BudgetFor(ToUser(c.Input.GetProperty("user")), c.WholeNumber("weekday")),
        "validatePlan" => ValidatePlan(c.Input),
        _ => throw new NotSupportedException($"No C# counterpart mapped for {c.Function}"),
    };

    private static JsonObject ValidatePlan(JsonElement input)
    {
        var result = PlanValidator.Validate(
            new PlanDraft(input.GetProperty("slots").EnumerateArray().Select(ToSlot).ToList()),
            input.GetProperty("tasks").EnumerateArray().Select(ToTask).ToList(),
            input.GetProperty("users").EnumerateArray().Select(ToUser).ToList(),
            input.GetProperty("intervals").EnumerateArray().Select(ToInterval).ToList());
        return new JsonObject
        {
            ["errors"] = new JsonArray(result.Errors.Select(ToNode).ToArray()),
            ["warnings"] = new JsonArray(result.Warnings.Select(ToNode).ToArray()),
            ["summary"] = JsonSerializer.SerializeToNode(result.Summary, Web),
        };
    }

    /// <summary>Only the fields an issue carries are written, like the TypeScript object literals.</summary>
    private static JsonObject ToNode(PlanIssue issue)
    {
        var node = new JsonObject { ["code"] = issue.Code };
        Add(node, "slotIndex", issue.SlotIndex);
        node["taskId"] = issue.TaskId;
        node["userId"] = issue.UserId;
        Add(node, "weekIndex", issue.WeekIndex);
        Add(node, "weekday", issue.Weekday);
        node["period"] = issue.Period?.ToString().ToLowerInvariant();
        Add(node, "placed", issue.Placed);
        Add(node, "required", issue.Required);
        Add(node, "minutes", issue.Minutes);
        Add(node, "budget", issue.Budget);
        foreach (var absent in node.Where(p => p.Value is null).Select(p => p.Key).ToList())
        {
            node.Remove(absent);
        }

        return node;
    }

    private static void Add(JsonObject node, string name, int? value) => node[name] = value;

    private static PlanSlot ToSlot(JsonElement s) => new(
        s.GetProperty("taskId").GetString()!,
        s.GetProperty("weekIndex").GetInt32(),
        s.GetProperty("weekday").GetInt32(),
        s.GetProperty("assigneeId").GetString());

    private static PlanTask ToTask(JsonElement t) => new(
        t.GetProperty("_id").GetString()!,
        t.GetProperty("name").GetString()!,
        t.GetProperty("intervalKey").GetString()!,
        t.GetProperty("durationMinutes").GetInt32(),
        t.GetProperty("active").GetBoolean());

    private static PlanUser ToUser(JsonElement u) => new(
        u.GetProperty("_id").GetString()!,
        u.GetProperty("name").GetString()!,
        u.GetProperty("active").GetBoolean(),
        u.GetProperty("unavailableWeekdays").EnumerateArray().Select(d => d.GetInt32()).ToList(),
        ToMinutes(u.GetProperty("dailyBudgetMinutes")),
        ToMinutes(u.GetProperty("maxDailyMinutes")));

    private static DayMinutes ToMinutes(JsonElement m) => new(m.GetProperty("weekday").GetInt32(), m.GetProperty("weekend").GetInt32());

    private static Interval ToInterval(JsonElement i) => new(
        i.GetProperty("key").GetString()!,
        i.GetProperty("label").GetString()!,
        i.GetProperty("perCycle") is { ValueKind: JsonValueKind.Number } per ? per.GetInt32() : null,
        i.GetProperty("periodDays").GetInt32());
}
