using System.Globalization;

namespace Huishoudplanner.Domain.Settings;

/// <summary>
/// The ISO 4217 currencies the runtime knows and their number of fraction digits, read from the region data of the
/// platform (the counterpart of <c>Intl.supportedValuesOf('currency')</c>, requirements 4.12).
/// </summary>
public static class Currencies
{
    private static readonly Lazy<Dictionary<string, int>> Known = new(Load);

    public static bool IsKnown(string code) => Known.Value.ContainsKey(code);

    /// <summary>Money is whole cents, so only currencies with exactly two fraction digits can be used.</summary>
    public static bool HasTwoDecimals(string code) => Known.Value.TryGetValue(code, out var digits) && digits == 2;

    private static Dictionary<string, int> Load()
    {
        var digits = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                digits.TryAdd(region.ISOCurrencySymbol, culture.NumberFormat.CurrencyDecimalDigits);
            }
            catch (ArgumentException)
            {
                // A culture without region data has no currency to offer.
            }
        }

        return digits;
    }
}
