namespace Huishoudplanner.Domain.Notifications;

/// <summary>What the morning message of one person is composed from.</summary>
/// <param name="Name">The person.</param>
/// <param name="Mine">Open occurrences today assigned to this person.</param>
/// <param name="Anyone">Open occurrences today for "wie dan ook" (the same number for everyone).</param>
/// <param name="Overdue">Household-wide overdue tasks from the due engine.</param>
public sealed record MorningCounts(string Name, int Mine, int Anyone, int Overdue);

/// <summary>Port of <c>morningMessage</c> and <c>MORNING_TITLE</c> of apps/server/src/domain/notify/morning.ts.</summary>
public static class MorningMessage
{
    public const string Title = "Keep the House Clean";

    /// <summary>The Dutch morning text; null when there is nothing to report.</summary>
    public static string? Compose(MorningCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (counts.Mine == 0 && counts.Anyone == 0 && counts.Overdue == 0)
        {
            return null;
        }

        var parts = new List<string> { $"Goedemorgen {counts.Name}!" };
        if (counts.Mine > 0 && counts.Anyone > 0)
        {
            parts.Add($"Vandaag staan er {Taken(counts.Mine)} voor je klaar en {Taken(counts.Anyone)} voor wie dan ook.");
        }
        else if (counts.Mine > 0)
        {
            parts.Add($"Vandaag staan er {Taken(counts.Mine)} voor je klaar.");
        }
        else if (counts.Anyone > 0)
        {
            parts.Add($"Vandaag staan er {Taken(counts.Anyone)} klaar voor wie dan ook.");
        }
        else
        {
            parts.Add("Vandaag staat er niets voor je gepland.");
        }

        if (counts.Overdue > 0)
        {
            parts.Add(counts.Overdue == 1 ? "1 taak is achterstallig." : $"{counts.Overdue} taken zijn achterstallig.");
        }

        return string.Join(' ', parts);
    }

    private static string Taken(int n) => n == 1 ? "1 taak" : $"{n} taken";
}
