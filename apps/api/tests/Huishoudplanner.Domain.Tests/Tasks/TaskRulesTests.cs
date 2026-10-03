using Huishoudplanner.Domain.Tasks;

namespace Huishoudplanner.Domain.Tests.Tasks;

public sealed class TaskRulesTests
{
    private const string Id = "0123456789abcdef01234567";

    private static CreateTaskCommand Valid => new("Badkamer", Id, "1w", 30);

    [Fact]
    public void AValidCreateCommand_hasNoErrors()
    {
        TaskRules.Validate(Valid with { Points = 0, DefaultAssigneeId = Id, Tags = ["a"] }).Should().BeNull();
    }

    [Theory]
    [InlineData("name", "")]
    [InlineData("name", "  ")]
    [InlineData("roomId", "nope")]
    [InlineData("intervalKey", "")]
    [InlineData("defaultAssigneeId", "0123")]
    public void ACreateCommandWithABadField_namesThatField(string field, string value)
    {
        var command = field switch
        {
            "name" => Valid with { Name = value },
            "roomId" => Valid with { RoomId = value },
            "intervalKey" => Valid with { IntervalKey = value },
            _ => Valid with { DefaultAssigneeId = value },
        };

        TaskRules.Validate(command)!.Errors.Keys.Should().Equal(field);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void TheDuration_isAtLeastOneMinute(int minutes, bool invalid)
    {
        (TaskRules.Validate(Valid with { DurationMinutes = minutes }) is not null).Should().Be(invalid);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1000, false)]
    [InlineData(1001, true)]
    public void ThePoints_runFromZeroToAThousand(int points, bool invalid)
    {
        (TaskRules.Validate(Valid with { Points = points }) is not null).Should().Be(invalid);
    }

    [Fact]
    public void ABlankTag_isKeyedByItsIndex()
    {
        TaskRules.Validate(Valid with { Tags = ["a", "", "  "] })!.Errors.Keys.Should().BeEquivalentTo("tags.1", "tags.2");
    }

    [Fact]
    public void AnEmptyPatch_isValid_andAnAssigneeOfNull_isAValue()
    {
        TaskRules.Validate(new TaskPatch()).Should().BeNull();
        TaskRules.Validate(new TaskPatch(DefaultAssignee: new AssigneeChoice(null))).Should().BeNull();
    }

    [Fact]
    public void APatchWithBadValues_namesEveryField()
    {
        var patch = new TaskPatch("  ", "x", "", 0, 2000, new AssigneeChoice("y"), Tags: [" "]);

        TaskRules.Validate(patch)!.Errors.Keys.Should().BeEquivalentTo(
            "name", "roomId", "intervalKey", "durationMinutes", "points", "defaultAssigneeId", "tags.0");
    }

    [Theory]
    [InlineData("0123456789abcdef01234567", true)]
    [InlineData("0123456789ABCDEF01234567", true)]
    [InlineData("0123456789abcdef0123456", false)]
    [InlineData("0123456789abcdef0123456g", false)]
    [InlineData(null, false)]
    public void AnId_is24HexCharacters(string? id, bool valid)
    {
        TaskRules.IsId(id).Should().Be(valid);
    }
}
