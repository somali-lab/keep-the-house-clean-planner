using Huishoudplanner.Domain.Notifications;

namespace Huishoudplanner.Domain.Tests.Notifications;

/// <summary>The morningMessage cases of apps/server/test/notify.test.ts.</summary>
public sealed class MorningMessageTests
{
    [Theory]
    [InlineData(0, 0, 0, null)]
    [InlineData(1, 0, 0, "Goedemorgen Anna! Vandaag staan er 1 taak voor je klaar.")]
    [InlineData(3, 2, 1, "Goedemorgen Anna! Vandaag staan er 3 taken voor je klaar en 2 taken voor wie dan ook. 1 taak is achterstallig.")]
    [InlineData(0, 1, 0, "Goedemorgen Anna! Vandaag staan er 1 taak klaar voor wie dan ook.")]
    [InlineData(0, 0, 4, "Goedemorgen Anna! Vandaag staat er niets voor je gepland. 4 taken zijn achterstallig.")]
    [InlineData(0, 3, 0, "Goedemorgen Anna! Vandaag staan er 3 taken klaar voor wie dan ook.")]
    public void Compose_followsTheDutchTexts(int mine, int anyone, int overdue, string? expected)
    {
        MorningMessage.Compose(new MorningCounts("Anna", mine, anyone, overdue)).Should().Be(expected);
    }

    [Fact]
    public void Title_isTheAppName() => MorningMessage.Title.Should().Be("Keep the House Clean");
}
