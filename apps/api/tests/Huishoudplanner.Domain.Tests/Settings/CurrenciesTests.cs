using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Domain.Tests.Settings;

public class CurrenciesTests
{
    [Theory]
    [InlineData("EUR")]
    [InlineData("USD")]
    [InlineData("GBP")]
    [InlineData("JPY")]
    [InlineData("KWD")]
    public void Known_currencies_are_recognised(string code) => Currencies.IsKnown(code).Should().BeTrue();

    [Theory]
    [InlineData("XXX")]
    [InlineData("ABC")]
    [InlineData("eur")]
    [InlineData("")]
    public void Unknown_codes_are_not_currencies(string code) => Currencies.IsKnown(code).Should().BeFalse();

    [Theory]
    [InlineData("EUR", true)]
    [InlineData("USD", true)]
    [InlineData("GBP", true)]
    [InlineData("JPY", false)]
    [InlineData("KWD", false)]
    public void Only_currencies_with_two_fraction_digits_can_hold_money_in_whole_cents(string code, bool twoDecimals) =>
        Currencies.HasTwoDecimals(code).Should().Be(twoDecimals);
}
