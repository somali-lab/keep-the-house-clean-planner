namespace Huishoudplanner.Domain.Points;

/// <summary>
/// Points to money (requirements 4.12; port of <c>pointsToCents</c> in <c>packages/shared/src/points.ts</c>). Money is always
/// whole cents: points times the factor, with no rounding. Display formatting (<c>formatCents</c>, <c>Intl.NumberFormat</c>) stays
/// in the web app; the currency checks are <c>Settings.Currencies</c>.
/// </summary>
public static class PointsMoney
{
    /// <summary>Whole points times whole cents per point; a negative balance gives negative cents. An overflow of 64 bits throws <see cref="OverflowException"/>.</summary>
    public static long PointsToCents(long points, long centsPerPoint) => checked(points * centsPerPoint);
}
