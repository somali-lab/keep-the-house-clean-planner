using System.Text.Json;
using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Tests.Support;
using static Huishoudplanner.Domain.Tests.Support.PointsVectorArguments;

namespace Huishoudplanner.Domain.Tests.Badges;

/// <summary>Runs every case of Vectors/badges.json against <see cref="BadgeRules"/>, <see cref="BadgeImages"/> and <see cref="ExampleBadges"/>.</summary>
public class BadgeVectorTests
{
    public static TheoryData<VectorCase> Cases => VectorCase.Load("badges");

    [Theory]
    [MemberData(nameof(Cases))]
    public void Vector_case_gives_the_expected_result(VectorCase vectorCase) =>
        VectorRunner.Run(vectorCase, Dispatch);

    [Fact]
    public void The_vector_file_has_all_61_cases() => Cases.Count.Should().Be(61);

    private static object? Dispatch(VectorCase c) => c.Function switch
    {
        "ruleCovers" => BadgeRules.Covers(ReadRule(c.Input.GetProperty("rule")), c.Input.NullableText("taskId")),
        "evaluateBadgeRule" => Evaluate(c),
        "sniffBadgeImageType" => BadgeImages.Sniff([.. c.Input.GetProperty("bytes").EnumerateArray().Select(b => (byte)b.GetInt32())])?.ContentType(),
        "exampleBadges" => ExampleBadges.All.Select(Shape).ToList(),
        "exampleBadgeMatches" => ExampleBadges.All.Single(e => e.Key == c.Text("key")).Matches(c.Text("taskName")),
        _ => throw new NotSupportedException($"No C# counterpart mapped for {c.Function}"),
    };

    private static object Evaluate(VectorCase c)
    {
        var executions = c.Input.GetProperty("executions").EnumerateArray()
            .Select(e => new BadgeExecution(e.GetProperty("id").GetString()!, e.NullableText("taskId"), e.GetProperty("minutes").GetInt32(), ParseInstant(e.GetProperty("at").GetString()!)))
            .ToList();
        var weeks = c.Input.GetProperty("onTimeWeekDates").EnumerateArray().Select(d => ParseInstant(d.GetString()!)).ToList();
        var outcome = BadgeRules.Evaluate(ReadRule(c.Input.GetProperty("rule")), executions, weeks);
        return new { outcome.Current, AwardedAt = Iso(outcome.AwardedAt) };
    }

    private static BadgeRule ReadRule(JsonElement e) => new(
        e.GetProperty("type").GetString() switch
        {
            "executions" => BadgeRuleType.Executions,
            "minutes" => BadgeRuleType.Minutes,
            "onTimeWeeks" => BadgeRuleType.OnTimeWeeks,
            var other => throw new NotSupportedException($"Unknown rule type {other}"),
        },
        e.TryGetProperty("taskIds", out var ids) ? [.. ids.EnumerateArray().Select(i => i.GetString()!)] : [],
        e.GetProperty("threshold").GetInt32());

    private static object Shape(ExampleBadge e) => new
    {
        e.Key,
        e.TaskNamePattern,
        Rule = new
        {
            Type = e.RuleType switch
            {
                BadgeRuleType.Executions => "executions",
                BadgeRuleType.Minutes => "minutes",
                _ => "onTimeWeeks",
            },
            e.Threshold,
        },
        Text = new
        {
            Nl = new { e.Nl.Name, e.Nl.Description },
            En = new { e.En.Name, e.En.Description },
        },
    };
}
