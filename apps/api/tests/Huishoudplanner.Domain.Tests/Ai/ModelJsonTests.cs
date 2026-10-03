using Huishoudplanner.Domain.Ai;

namespace Huishoudplanner.Domain.Tests.Ai;

public class ModelJsonTests
{
    [Fact]
    public void Extract_plain_json_returns_the_document()
    {
        var result = ModelJson.Extract("{\"ok\":true}");

        result.IsT0.Should().BeTrue();
        result.AsT0.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("```json\n{\"a\":1}\n```")]
    [InlineData("  ```\n{\"a\":1}\n```  ")]
    [InlineData("```JSON {\"a\":1} ```")]
    public void Extract_strips_a_code_fence_around_the_json(string raw)
    {
        var result = ModelJson.Extract(raw);

        result.IsT0.Should().BeTrue();
        result.AsT0.GetProperty("a").GetInt32().Should().Be(1);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("")]
    [InlineData("```json\nnope\n```")]
    [InlineData("{\"a\":")]
    public void Extract_reports_unusable_output_without_echoing_it(string raw)
    {
        var result = ModelJson.Extract(raw);

        result.IsT1.Should().BeTrue();
        result.AsT1.Message.Should().Be("The AI answer was not valid JSON");
    }

    [Fact]
    public void Extract_result_stays_valid_after_the_input_goes_away()
    {
        var element = ModelJson.Extract("[1,2,3]").AsT0;

        element.GetArrayLength().Should().Be(3);
    }
}
