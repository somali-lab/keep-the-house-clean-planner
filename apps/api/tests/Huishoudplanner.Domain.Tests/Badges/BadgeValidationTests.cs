using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Limits;

namespace Huishoudplanner.Domain.Tests.Badges;

/// <summary>The value rules of a badge: the zod schemas of <c>schemas/badges.ts</c> and <c>decodeBadgeImage</c> of the Node server.</summary>
public class BadgeValidationTests
{
    private static readonly BadgeLimits Limits = HouseholdLimits.Current.Badges;

    private const string Task = "0123456789abcdef01234567";

    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 0];
    private static readonly byte[] Jpeg = [0xff, 0xd8, 0xff, 0xe0, 0, 0];
    private static readonly byte[] Webp = [0x52, 0x49, 0x46, 0x46, 1, 0, 0, 0, 0x57, 0x45, 0x42, 0x50, 0x56, 0x50, 0x38, 0x20];
    private static readonly byte[] Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray();

    private static Dictionary<string, string[]> NoErrors() => [];

    private static BadgeImageInput Image(byte[] bytes, string type) => new(type, Convert.ToBase64String(bytes));

    // ---- name and description

    [Theory]
    [InlineData("  Toiletjuffrouw ", "Toiletjuffrouw")]
    [InlineData("x", "x")]
    public void Name_isTrimmed(string name, string expected)
    {
        var errors = NoErrors();

        BadgeValidation.CheckName(name, Limits, errors).Should().Be(expected);

        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Name_thatIsEmptyAfterTrimming_isRefused(string? name)
    {
        var errors = NoErrors();

        BadgeValidation.CheckName(name, Limits, errors).Should().BeNull();

        errors.Should().ContainKey("name");
    }

    [Fact]
    public void Name_isBoundedAt60Characters()
    {
        var errors = NoErrors();

        BadgeValidation.CheckName(new string('x', 60), Limits, errors).Should().NotBeNull();
        BadgeValidation.CheckName(new string('x', 61), Limits, errors).Should().BeNull();

        errors.Should().ContainKey("name");
    }

    [Fact]
    public void Description_isBoundedAt200Characters_andEmptyWhenAbsent()
    {
        var errors = NoErrors();

        BadgeValidation.CheckDescription(null, Limits, errors).Should().BeEmpty();
        BadgeValidation.CheckDescription(new string('x', 200), Limits, errors).Should().HaveLength(200);
        errors.Should().BeEmpty();
        BadgeValidation.CheckDescription(new string('x', 201), Limits, errors).Should().BeNull();
        errors.Should().ContainKey("description");
    }

    // ---- rule

    [Theory]
    [InlineData(BadgeRuleType.Minutes, 100_000, true)]
    [InlineData(BadgeRuleType.Minutes, 100_001, false)]
    [InlineData(BadgeRuleType.Executions, 0, false)]
    [InlineData(BadgeRuleType.Executions, 1, true)]
    [InlineData(BadgeRuleType.OnTimeWeeks, 1000, true)]
    [InlineData(BadgeRuleType.OnTimeWeeks, 1001, false)]
    public void RuleThreshold_isBounded(BadgeRuleType type, int threshold, bool valid)
    {
        var errors = NoErrors();

        var rule = BadgeValidation.CheckRule(new BadgeRuleInput(type, [], threshold), Limits, errors);

        (rule is not null).Should().Be(valid);
        errors.ContainsKey("rule.threshold").Should().Be(!valid);
    }

    [Fact]
    public void RuleTasks_areStoredOnce_inStableOrder_andInLowerCase()
    {
        var errors = NoErrors();
        const string other = "ffffffffffffffffffffffff";

        var rule = BadgeValidation.CheckRule(new BadgeRuleInput(BadgeRuleType.Executions, [other.ToUpperInvariant(), Task, other], 3), Limits, errors);

        rule!.TaskIds.Should().Equal(Task, other);
        (rule.Type, rule.Threshold).Should().Be((BadgeRuleType.Executions, 3));
    }

    [Fact]
    public void AnOnTimeWeeksRule_hasNoTasks()
    {
        var errors = NoErrors();

        var rule = BadgeValidation.CheckRule(new BadgeRuleInput(BadgeRuleType.OnTimeWeeks, [Task], 4), Limits, errors);

        rule!.TaskIds.Should().BeEmpty();
    }

    [Fact]
    public void RuleTasks_mustBeIds_andAtMost500()
    {
        var errors = NoErrors();

        BadgeValidation.CheckRule(new BadgeRuleInput(BadgeRuleType.Executions, ["nope"], 1), Limits, errors).Should().BeNull();
        errors["rule.taskIds"].Should().Equal("invalid_object_id");

        errors.Clear();
        var many = Enumerable.Range(0, 501).Select(i => i.ToString("x24", System.Globalization.CultureInfo.InvariantCulture)).ToList();
        BadgeValidation.CheckRule(new BadgeRuleInput(BadgeRuleType.Executions, many, 1), Limits, errors).Should().BeNull();
        errors.Should().ContainKey("rule.taskIds");
    }

    [Fact]
    public void ARuleIsRequired()
    {
        var errors = NoErrors();

        BadgeValidation.CheckRule(null, Limits, errors).Should().BeNull();

        errors.Should().ContainKey("rule");
    }

    // ---- images

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("webp")]
    public void PngJpegAndWebp_areAccepted_withTheHashOfTheirBytes(string kind)
    {
        var (bytes, type) = kind switch
        {
            "png" => (Png, "image/png"),
            "jpeg" => (Jpeg, "image/jpeg"),
            _ => (Webp, "image/webp"),
        };
        var errors = NoErrors();

        var image = BadgeValidation.CheckImage(Image(bytes, type), Limits, errors);

        errors.Should().BeEmpty();
        image!.Bytes.Should().Equal(bytes);
        image.Info.Should().Be(new BadgeImageInfo(image.ContentType, bytes.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))));
        image.Info.Version.Should().HaveLength(12).And.Be(image.Hash[..12]);
    }

    [Fact]
    public void AnImageOfExactly256KB_isAccepted_andOneByteMoreIsNot()
    {
        var errors = NoErrors();
        var exact = new byte[256 * 1024];
        Png.CopyTo(exact, 0);
        var over = new byte[256 * 1024 + 1];
        Png.CopyTo(over, 0);

        BadgeValidation.CheckImage(Image(exact, "image/png"), Limits, errors).Should().NotBeNull();
        errors.Should().BeEmpty();
        BadgeValidation.CheckImage(Image(over, "image/png"), Limits, errors).Should().BeNull();
        errors["image.data"].Should().Equal(BadgeValidation.ImageTooLarge);
    }

    [Fact]
    public void TheBase64LimitHoldsExactly256KB()
    {
        BadgeValidation.MaxBase64Length(Limits).Should().Be(349_528);
    }

    [Theory]
    [InlineData("not base64!", "image.data", BadgeValidation.InvalidBase64)]
    [InlineData("", "image.data", BadgeValidation.InvalidBase64)]
    public void Text_thatIsNotBase64_isRefused(string data, string field, string message)
    {
        var errors = NoErrors();

        BadgeValidation.CheckImage(new BadgeImageInput("image/png", data), Limits, errors).Should().BeNull();

        errors[field].Should().Equal(message);
    }

    [Fact]
    public void AnSvg_isRefused_becauseItCanCarryScript()
    {
        var errors = NoErrors();

        BadgeValidation.CheckImage(Image(Svg, "image/png"), Limits, errors).Should().BeNull();

        errors["image.data"].Should().Equal(BadgeValidation.UnsupportedImageType);
    }

    [Fact]
    public void TextThatIsNotAnImage_isRefused()
    {
        var errors = NoErrors();

        BadgeValidation.CheckImage(Image("hello world, not an image"u8.ToArray(), "image/png"), Limits, errors).Should().BeNull();

        errors["image.data"].Should().Equal(BadgeValidation.UnsupportedImageType);
    }

    [Fact]
    public void ADeclaredTypeThatDiffersFromTheRealOne_isAMismatch()
    {
        var errors = NoErrors();

        BadgeValidation.CheckImage(Image(Png, "image/jpeg"), Limits, errors).Should().BeNull();

        errors["image.contentType"].Should().Equal(BadgeValidation.ImageTypeMismatch);
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("image/gif")]
    [InlineData("")]
    public void AContentTypeOutsidePngJpegAndWebp_isRefused(string type)
    {
        var errors = NoErrors();

        BadgeValidation.CheckImage(Image(Svg, type), Limits, errors).Should().BeNull();

        errors.Should().ContainKey("image.contentType");
    }

    [Fact]
    public void NoImage_isNoError()
    {
        var errors = NoErrors();

        BadgeValidation.CheckImage(null, Limits, errors).Should().BeNull();

        errors.Should().BeEmpty();
    }
}
