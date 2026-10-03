using Huishoudplanner.Domain.Badges;

namespace Huishoudplanner.Domain.Tests.Badges;

/// <summary>Scenarios of badges.test.ts that the golden vectors cannot express (purity, offsets, example invariants).</summary>
public class BadgeRulesTests
{
    private const string Task = "0123456789abcdef01234567";

    private static BadgeExecution Run(string id, DateTimeOffset at, int minutes = 10) => new(id, Task, minutes, at);

    [Fact]
    public void Evaluating_is_pure_the_same_data_gives_the_same_answer_and_the_input_is_not_changed()
    {
        BadgeExecution[] runs =
        [
            Run("e3", new DateTimeOffset(2026, 9, 18, 8, 0, 0, TimeSpan.Zero)),
            Run("e1", new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero)),
        ];
        var rule = new BadgeRule(BadgeRuleType.Executions, [], 2);
        var before = runs.ToArray();

        BadgeRules.Evaluate(rule, runs, []).Should().Be(BadgeRules.Evaluate(rule, runs, []));
        runs.Should().Equal(before);
    }

    [Fact]
    public void Executions_are_ordered_by_instant_also_when_the_offsets_differ()
    {
        // 10:00+02:00 is 08:00Z, earlier than 09:00Z although its text sorts later.
        var early = Run("a", new DateTimeOffset(2026, 9, 16, 10, 0, 0, TimeSpan.FromHours(2)));
        var late = Run("b", new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero));

        var outcome = BadgeRules.Evaluate(new BadgeRule(BadgeRuleType.Executions, [], 1), [late, early], []);

        outcome.AwardedAt.Should().Be(early.At);
    }

    [Fact]
    public void An_on_time_weeks_rule_has_no_tasks_and_ignores_executions()
    {
        var rule = BadgeRule.OnTimeWeeks(1);

        rule.TaskIds.Should().BeEmpty();
        BadgeRules.Evaluate(rule, [Run("e1", DateTimeOffset.UnixEpoch)], []).Should().Be(new BadgeOutcome(0, null));
    }

    [Fact]
    public void The_examples_have_the_stable_keys_and_texts_within_the_limits()
    {
        ExampleBadges.All.Select(e => e.Key).Should().Equal("example:on_time", "example:toilet", "example:mop");
        foreach (var example in ExampleBadges.All)
        {
            foreach (var language in Enum.GetValues<BadgeLanguage>())
            {
                var text = example.TextFor(language);
                text.Name.Length.Should().BeInRange(1, 60);
                text.Description.Length.Should().BeLessThanOrEqualTo(200);
            }

            example.Threshold.Should().BeInRange(1, example.RuleType == BadgeRuleType.OnTimeWeeks ? 1000 : 100_000);
            if (example.TaskNamePattern is not null)
            {
                example.Matches("any task name").Should().BeFalse("compiling the pattern must not throw");
            }
        }

        ExampleBadges.All[1].Nl.Name.Should().Be("Toiletjuffrouw");
        ExampleBadges.All[2].Nl.Name.Should().Be("Dweilkampioen");
    }

    [Fact]
    public void An_example_without_a_pattern_matches_no_task()
    {
        ExampleBadges.All[0].TaskNamePattern.Should().BeNull();
        ExampleBadges.All[0].Matches("toilet").Should().BeFalse();
    }

    [Fact]
    public void Image_types_have_their_content_type_and_an_svg_is_never_one()
    {
        BadgeImages.Sniff([0xff, 0xd8, 0xff]).Should().Be(BadgeImageType.Jpeg);
        BadgeImageType.Png.ContentType().Should().Be("image/png");
        BadgeImageType.Jpeg.ContentType().Should().Be("image/jpeg");
        BadgeImageType.Webp.ContentType().Should().Be("image/webp");
        BadgeImages.Sniff("<svg/>"u8).Should().BeNull();
    }
}
