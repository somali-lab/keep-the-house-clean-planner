using Huishoudplanner.Domain.Ai;
using Huishoudplanner.Domain.CyclePlans;

namespace Huishoudplanner.Domain.Tests.Ai;

/// <summary>What a model may answer (aiPlanOutputSchema, aiTaskSuggestionsOutputSchema, aiExplanationOutputSchema) and the filtering of suggestions.</summary>
public class AiOutputsTests
{
    private const string Task = "0123456789abcdef01234567";
    private const string User = "76543210fedcba9876543210";

    private static string Plan(string slots, string rationale = "[\"a\",\"b\",\"c\",\"d\"]") => $"{{\"slots\":{slots},\"rationale\":{rationale}}}";

    [Fact]
    public void A_valid_plan_is_read_with_lowercase_ids_a_null_assignee_and_a_default_sort_order()
    {
        var raw = Plan($"[{{\"taskId\":\"{Task.ToUpperInvariant()}\",\"weekIndex\":2,\"weekday\":6,\"assigneeId\":null}},{{\"taskId\":\"{Task}\",\"weekIndex\":0,\"weekday\":0,\"assigneeId\":\"{User}\",\"sortOrder\":-3}}]");

        var result = AiOutputs.ParsePlan(raw);

        result.IsT0.Should().BeTrue();
        result.AsT0.Slots.Should().Equal(new CyclePlanSlot(Task, 2, 6, null, 0), new CyclePlanSlot(Task, 0, 0, User, -3));
        result.AsT0.Rationale.Should().Equal("a", "b", "c", "d");
    }

    [Fact]
    public void A_plan_inside_a_code_fence_is_accepted()
    {
        AiOutputs.ParsePlan("```json\n" + Plan("[]") + "\n```").IsT0.Should().BeTrue();
    }

    [Theory]
    [InlineData("Hier is je plan: {slots")]
    [InlineData("")]
    public void An_answer_that_is_not_json_is_the_documented_error(string raw)
    {
        AiOutputs.ParsePlan(raw).AsT1.Should().Equal("The answer was not valid JSON.");
    }

    [Fact]
    public void Every_problem_of_a_plan_is_listed_with_its_path()
    {
        var raw = Plan(
            $"[{{\"taskId\":\"nope\",\"weekIndex\":4,\"weekday\":-1,\"assigneeId\":5}},{{\"taskId\":\"{Task}\",\"weekIndex\":1.5,\"weekday\":1}},7]",
            "[\"only one\"]");

        var errors = AiOutputs.ParsePlan(raw).AsT1;

        errors.Should().Contain(e => e.StartsWith("slots.0.taskId:", StringComparison.Ordinal) && e.Contains("invalid_object_id", StringComparison.Ordinal));
        errors.Should().Contain(e => e.StartsWith("slots.0.weekIndex:", StringComparison.Ordinal));
        errors.Should().Contain(e => e.StartsWith("slots.0.weekday:", StringComparison.Ordinal));
        errors.Should().Contain(e => e.StartsWith("slots.0.assigneeId:", StringComparison.Ordinal));
        errors.Should().Contain(e => e.StartsWith("slots.1.weekIndex:", StringComparison.Ordinal));
        errors.Should().Contain(e => e.StartsWith("slots.1.assigneeId:", StringComparison.Ordinal));
        errors.Should().Contain(e => e.StartsWith("slots.2:", StringComparison.Ordinal));
        errors.Should().Contain(e => e.StartsWith("rationale:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public void A_plan_that_is_not_an_object_is_refused(string raw)
    {
        AiOutputs.ParsePlan(raw).AsT1.Should().ContainSingle().Which.Should().StartWith("(root):");
    }

    [Fact]
    public void A_plan_without_slots_or_rationale_is_refused()
    {
        var errors = AiOutputs.ParsePlan("{}").AsT1;

        errors.Should().Equal("slots: expected array", "rationale: expected array");
    }

    [Theory]
    [InlineData("[\"a\",\"b\",\"c\"]")]
    [InlineData("[\"a\",\"b\",\"c\",\"d\",\"e\"]")]
    [InlineData("[\"a\",\"b\",\"c\",\"\"]")]
    [InlineData("[\"a\",\"b\",\"c\",4]")]
    public void The_rationale_must_be_exactly_four_non_empty_sentences(string rationale)
    {
        AiOutputs.ParsePlan(Plan("[]", rationale)).IsT1.Should().BeTrue();
    }

    [Fact]
    public void Task_suggestions_are_read_as_the_model_wrote_them()
    {
        var raw = "{\"suggestions\":[{\"name\":\"Oven\",\"intervalKey\":\"4wk\",\"durationMinutes\":12.5,\"notes\":\" n \"},{\"name\":\"Raam\",\"intervalKey\":\"1w\",\"durationMinutes\":5}]}";

        var result = AiOutputs.ParseTaskSuggestions(raw).AsT0;

        result.Should().Equal(new RawTaskSuggestion("Oven", "4wk", 12.5, " n "), new RawTaskSuggestion("Raam", "1w", 5, null));
    }

    [Fact]
    public void Task_suggestions_that_are_no_json_or_of_the_wrong_shape_are_an_invalid_response()
    {
        var notJson = AiOutputs.ParseTaskSuggestions("geen json").AsT1;
        var wrong = AiOutputs.ParseTaskSuggestions("{\"suggestions\":[{\"name\":1,\"intervalKey\":\"x\"},3]}").AsT1;
        var none = AiOutputs.ParseTaskSuggestions("{}").AsT1;

        notJson.Message.Should().Be("The AI answer was not valid JSON");
        notJson.Errors.Should().BeEmpty();
        wrong.Message.Should().Be("The AI answer did not match the expected format");
        wrong.Errors.Should().Contain(e => e.StartsWith("suggestions.0.name:", StringComparison.Ordinal))
            .And.Contain(e => e.StartsWith("suggestions.0.durationMinutes:", StringComparison.Ordinal))
            .And.Contain(e => e.StartsWith("suggestions.1:", StringComparison.Ordinal));
        none.Errors.Should().Equal("suggestions: expected array");
    }

    [Fact]
    public void An_explanation_needs_exactly_four_sentences()
    {
        AiOutputs.ParseExplanation("{\"rationale\":[\"1\",\"2\",\"3\",\"4\"]}").AsT0.Should().Equal("1", "2", "3", "4");
        AiOutputs.ParseExplanation("{\"rationale\":[\"1\",\"2\",\"3\"]}").AsT1.Errors.Should().ContainSingle();
        AiOutputs.ParseExplanation("nee").AsT1.Message.Should().Be("The AI answer was not valid JSON");
        AiOutputs.ParseExplanation("[]").AsT1.Errors.Should().ContainSingle().Which.Should().StartWith("(root):");
    }

    [Fact]
    public void Filtering_drops_unknown_intervals_bad_durations_empty_names_and_duplicates_case_insensitively()
    {
        RawTaskSuggestion[] raw =
        [
            new("Oven reinigen", "4wk", 30, null),
            new("oven REINIGEN ", "4wk", 30, null),
            new("KEUKEN: AANRECHT", "1w", 10, null),
            new("Koelkast", "yearly", 40, null),
            new("Vriezer", "quarter", 0, null),
            new("Afzuigkap", "4wk", 12.5, null),
            new("   ", "1w", 5, null),
            new("Raam", "1w", 30.0, "  dweilen  "),
        ];

        var result = AiOutputs.FilterSuggestions(raw, ["1w", "4wk", "quarter"], ["Keuken: aanrecht"]);

        result.Should().Equal(new TaskSuggestion("Oven reinigen", "4wk", 30, string.Empty), new TaskSuggestion("Raam", "1w", 30, "dweilen"));
    }
}
