using System.Text.RegularExpressions;

namespace Huishoudplanner.Domain.Badges;

public enum BadgeLanguage
{
    Nl,
    En,
}

public sealed record BadgeText(string Name, string Description);

/// <summary>An example of the "add example badges" action (ADR-0014); names, thresholds and tasks stay editable afterwards.</summary>
/// <param name="Key">Stable key: the example is created once, however often the action runs.</param>
/// <param name="TaskNamePattern">
/// A case-insensitive regular expression for the names of the tasks the example is about; <see langword="null"/> or no match
/// leaves the badge inactive until tasks are chosen.
/// </param>
/// <param name="RuleType">The rule kind.</param>
/// <param name="Threshold">The rule threshold.</param>
/// <param name="Nl">Dutch name and description.</param>
/// <param name="En">English name and description.</param>
public sealed record ExampleBadge(string Key, string? TaskNamePattern, BadgeRuleType RuleType, int Threshold, BadgeText Nl, BadgeText En)
{
    public BadgeText TextFor(BadgeLanguage language) => language == BadgeLanguage.Nl ? Nl : En;

    /// <summary>
    /// Whether a task name matches the pattern, like the TypeScript <c>new RegExp(pattern, 'i').test(name)</c>. The ECMAScript
    /// option keeps <c>\b</c> and <c>\w</c> ASCII-only, as in JavaScript. An example without a pattern matches nothing.
    /// </summary>
    public bool Matches(string taskName)
    {
        ArgumentNullException.ThrowIfNull(taskName);
        return TaskNamePattern is not null
            && Regex.IsMatch(taskName, TaskNamePattern, RegexOptions.IgnoreCase | RegexOptions.ECMAScript, TimeSpan.FromSeconds(1));
    }
}

/// <summary>TS <c>EXAMPLE_BADGES</c>.</summary>
public static class ExampleBadges
{
    public static IReadOnlyList<ExampleBadge> All { get; } =
    [
        new ExampleBadge(
            "example:on_time",
            null,
            BadgeRuleType.OnTimeWeeks,
            4,
            new BadgeText("Alles op tijd", "Vier weken alles op tijd gedaan."),
            new BadgeText("Always on time", "Did everything on time for four weeks.")),
        new ExampleBadge(
            "example:toilet",
            @"toilet|\bwc\b",
            BadgeRuleType.Executions,
            10,
            new BadgeText("Toiletjuffrouw", "Het toilet 10 keer schoongemaakt."),
            new BadgeText("Toilet Champion", "Cleaned the toilet 10 times.")),
        new ExampleBadge(
            "example:mop",
            @"dweil|zwabber|\bmop",
            BadgeRuleType.Minutes,
            300,
            new BadgeText("Dweilkampioen", "300 minuten gedweild."),
            new BadgeText("Mop Champion", "Mopped for 300 minutes.")),
    ];
}
