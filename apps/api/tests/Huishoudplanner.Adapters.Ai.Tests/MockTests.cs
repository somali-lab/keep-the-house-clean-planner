using System.Text.Json;
using Huishoudplanner.Domain.Ai;

namespace Huishoudplanner.Adapters.Ai.Tests;

public class MockTests
{
    private static ChatRequest Named(string name, string user = "{}") => new("system", user, new ChatOptions(Name: name));

    [Fact]
    public async Task Fixture_answers_are_served_per_request_name_in_order_repeating_the_last()
    {
        var port = Helpers.Create(
            new AiProviderOptions(AiProviderType.Mock),
            responders: new Dictionary<string, MockResponder> { ["plan-proposal"] = MockResponder.Fixed("{\"a\":1}", "{\"a\":2}") });

        (await port.Ask()).TextOf().Should().Be("{\"a\":1}");
        (await port.Ask()).TextOf().Should().Be("{\"a\":2}");
        (await port.Ask()).TextOf().Should().Be("{\"a\":2}");
    }

    [Fact]
    public async Task Invalid_json_can_be_answered_first_to_exercise_the_re_prompt()
    {
        var port = Helpers.Create(
            new AiProviderOptions(AiProviderType.Mock),
            responders: new Dictionary<string, MockResponder> { ["plan-proposal"] = MockResponder.Fixed("{\"ok\":true}") },
            invalidFirst: true);

        (await port.Ask()).TextOf().Should().Be(MockChatClient.InvalidResponse);
        (await port.Ask()).TextOf().Should().Be("{\"ok\":true}");
    }

    [Fact]
    public async Task Function_responders_get_the_request_and_the_attempt_number_and_unknown_names_fail()
    {
        var port = Helpers.Create(
            new AiProviderOptions(AiProviderType.Mock),
            responders: new Dictionary<string, MockResponder>
            {
                ["explain"] = MockResponder.From((request, attempt) => JsonSerializer.Serialize(new { name = request.Name, attempt })),
            });

        (await port.Ask(Named("explain"))).TextOf().Should().Be("{\"name\":\"explain\",\"attempt\":0}");
        (await port.Ask(Named("explain"))).TextOf().Should().Be("{\"name\":\"explain\",\"attempt\":1}");
        (await port.Ask(Named("other"))).ErrorOf().Should().Be("Mock provider has no response for \"other\"");
    }

    [Fact]
    public async Task Default_responders_answer_the_connection_test()
    {
        var port = Helpers.Create(new AiProviderOptions(AiProviderType.Mock));

        (await port.Ask(Named(AiRequestNames.ConnectionTest))).TextOf().Should().Be("{\"ok\":true}");
    }

    [Fact]
    public async Task Default_plan_places_each_task_per_cycle_without_double_booking_and_respects_availability()
    {
        var payload = new
        {
            mode = "propose",
            tasks = new object[]
            {
                new { id = "t1", perCycle = 4 },
                new { id = "t2", perCycle = 28 },
                new { id = "t3", perCycle = (int?)null },
            },
            users = new object[]
            {
                new { id = "u1", unavailableWeekdays = new[] { 1 } },
                new { id = "u2", unavailableWeekdays = new[] { 1, 2, 3, 4, 5, 6, 0 } },
            },
        };
        var port = Helpers.Create(new AiProviderOptions(AiProviderType.Mock));

        var text = (await port.Ask(Named(AiRequestNames.PlanProposal, JsonSerializer.Serialize(payload)))).TextOf();

        using var document = JsonDocument.Parse(text);
        var slots = document.RootElement.GetProperty("slots").EnumerateArray().ToList();
        slots.Count(s => s.GetProperty("taskId").GetString() == "t1").Should().Be(4);
        slots.Count(s => s.GetProperty("taskId").GetString() == "t2").Should().Be(28);
        slots.Should().NotContain(s => s.GetProperty("taskId").GetString() == "t3");
        slots.Select(s => (s.GetProperty("taskId").GetString(), s.GetProperty("weekIndex").GetInt32(), s.GetProperty("weekday").GetInt32()))
            .Should().OnlyHaveUniqueItems();
        slots.Where(s => s.GetProperty("weekday").GetInt32() == 1)
            .Should().OnlyContain(s => s.GetProperty("assigneeId").ValueKind == JsonValueKind.Null);
        slots.Where(s => s.GetProperty("assigneeId").ValueKind == JsonValueKind.String && s.GetProperty("assigneeId").GetString() == "u2")
            .Should().BeEmpty();
        document.RootElement.GetProperty("rationale").GetArrayLength().Should().Be(4);
    }

    [Fact]
    public void Default_plan_is_exactly_the_node_answer_for_a_known_payload()
    {
        // Node: perCycle 2, task index 0 -> positions 0 and 14 -> (week 0, Monday) and (week 2, Monday); rotation u1, u2.
        var payload = JsonDocument.Parse("{\"tasks\":[{\"id\":\"t\",\"perCycle\":2}],\"users\":[{\"id\":\"u1\",\"unavailableWeekdays\":[]},{\"id\":\"u2\",\"unavailableWeekdays\":[]}]}").RootElement;

        var plan = DefaultMockResponders.DeterministicPlan(payload).ToJsonString();

        plan.Should().StartWith("{\"slots\":[{\"taskId\":\"t\",\"weekIndex\":0,\"weekday\":1,\"assigneeId\":\"u1\"},{\"taskId\":\"t\",\"weekIndex\":2,\"weekday\":1,\"assigneeId\":\"u2\"}],\"rationale\":[\"Week 1: taken zijn gelijkmatig verdeeld over de dagen.\"");
    }

    [Fact]
    public async Task Default_suggestions_include_an_unknown_interval_and_a_repeat_of_an_existing_name()
    {
        var payload = "{\"room\":\"Keuken\",\"existingTasks\":[{\"name\":\"Afzuigkap\"}],\"otherTasks\":[],\"intervals\":[]}";
        var port = Helpers.Create(new AiProviderOptions(AiProviderType.Mock));

        var text = (await port.Ask(Named(AiRequestNames.TaskSuggestions, payload))).TextOf();

        using var document = JsonDocument.Parse(text);
        var suggestions = document.RootElement.GetProperty("suggestions").EnumerateArray().ToList();
        suggestions.Select(s => s.GetProperty("name").GetString()).Should().Equal("Keuken: plinten afnemen", "Keuken: lampen afstoffen", "Keuken: gordijnen wassen", "Afzuigkap");
        suggestions[2].TryGetProperty("notes", out _).Should().BeFalse();
        suggestions[0].GetProperty("notes").GetString().Should().Be("Vochtige doek.");
    }

    [Fact]
    public async Task Default_explanation_counts_the_slots_per_week()
    {
        var payload = "{\"planName\":\"p\",\"users\":[],\"slots\":[{\"weekIndex\":0},{\"weekIndex\":0},{\"weekIndex\":3}]}";
        var port = Helpers.Create(new AiProviderOptions(AiProviderType.Mock));

        var text = (await port.Ask(Named(AiRequestNames.PlanExplanation, payload))).TextOf();

        text.Should().Be("{\"rationale\":[\"Week 1: 2 taken, verdeeld over de week.\",\"Week 2: 0 taken, verdeeld over de week.\",\"Week 3: 0 taken, verdeeld over de week.\",\"Week 4: 1 taken, verdeeld over de week.\"]}");
    }
}
