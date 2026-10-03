using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Settings;
using Huishoudplanner.Domain.Users;

namespace Huishoudplanner.Domain.Points;

/// <summary>The sums of the entries of one person in a range (what the store aggregates): the balance, the executions, the bonuses and the redemptions.</summary>
public sealed record PointTotal(string PersonId, long Points, int Executions, long BonusPoints, long Redeemed);

/// <summary>Earned, redeemed and the balance in whole cents at the factor in force now.</summary>
public sealed record BalanceMoney(long Earned, long Redeemed, long Balance);

/// <summary>The balance of one person over the range (requirements 4.12 and 8).</summary>
public sealed record PersonBalance(
    string PersonId,
    long Points,
    long Earned,
    long Redeemed,
    BalanceMoney? Money,
    int Executions,
    long BonusPoints);

/// <summary>The answer of <c>GET /points/balances</c>; <see cref="From"/> and <see cref="To"/> are <see langword="null"/> when absent (the whole ledger).</summary>
public sealed record PointsBalances(
    DateOnly? From,
    DateOnly? To,
    string CurrencyCode,
    int CentsPerPoint,
    IReadOnlyList<PersonBalance> Balances);

/// <summary>The position after an entry in the list order: newest <c>date</c> first, then by id.</summary>
public sealed record PointEntryCursor(DateTimeOffset Date, string Id)
{
    public static PointEntryCursor After(PointEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new PointEntryCursor(entry.Date, entry.Id);
    }

    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new object[] { Date.ToUnixTimeMilliseconds(), Id }));

    /// <summary>False for anything this application did not produce.</summary>
    public static bool TryDecode(string? value, out PointEntryCursor cursor)
    {
        cursor = null!;
        if (string.IsNullOrEmpty(value) || value.Length > 100)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(value));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 2 ||
                root[0].ValueKind != JsonValueKind.Number || !root[0].TryGetInt64(out var millis) ||
                root[1].ValueKind != JsonValueKind.String || !PointsRules.IsId(root[1].GetString()))
            {
                return false;
            }

            cursor = new PointEntryCursor(DateTimeOffset.FromUnixTimeMilliseconds(millis), root[1].GetString()!.ToLowerInvariant());
            return true;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>What <c>GET /points/entries</c> asks of the use case: one person, the days <see cref="From"/> to <see cref="To"/> (both included).</summary>
public sealed record PointEntriesRequest(string PersonId, DateOnly From, DateOnly To, int? Limit = null, string? Cursor = null);

/// <summary>The query the store answers: the entries of one person dated in [<see cref="From"/>, <see cref="ToExclusive"/>), newest date first, then by id, after the cursor.</summary>
public sealed record PointEntryQuery(string PersonId, DateTimeOffset From, DateTimeOffset ToExclusive, PointEntryCursor? After, int Take);

/// <summary>A ledger entry as the API shows it (Node <c>toPointEntryView</c>): the calendar dates as day keys in the household timezone instead of instants.</summary>
public sealed record PointEntryView(PointEntry Entry, DateOnly Date, DateOnly WeekStart, DateOnly? PeriodStart)
{
    public static PointEntryView From(PointEntry entry, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(zone);
        return new PointEntryView(
            entry,
            DayKeys.ToDayKey(entry.Date, zone),
            DayKeys.ToDayKey(entry.WeekStart, zone),
            entry.PeriodStart is { } period ? DayKeys.ToDayKey(period, zone) : null);
    }
}

/// <summary>One page of ledger entries; <see cref="NextCursor"/> is <see langword="null"/> on the last page.</summary>
public sealed record PointEntryList(IReadOnlyList<PointEntryView> Items, string? NextCursor);

public static class PointsEntryLimits
{
    public const int DefaultLimit = 100;

    public const int MaxLimit = 500;

    /// <summary>The longest range of one entries request in days, both included (53 weeks; <c>MAX_POINTS_ENTRIES_RANGE_DAYS</c>).</summary>
    public const int MaxRangeDays = 371;
}

/// <summary>The rules of reading the ledger.</summary>
public static class PointsRules
{
    public static bool IsId(string? value) => AuditObjectId.IsHex(value);

    /// <summary><c>from_after_to</c> when the range runs backwards (on <c>from</c>), shared by the balances and the entries query.</summary>
    public static Dictionary<string, string[]> CheckRange(DateOnly? from, DateOnly? to)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (from is { } start && to is { } end && start > end)
        {
            errors["from"] = ["from_after_to"];
        }

        return errors;
    }

    /// <summary>The entries request: a range of at most <see cref="PointsEntryLimits.MaxRangeDays"/> days, and a limit and cursor within bounds.</summary>
    public static Dictionary<string, string[]> CheckEntries(PointEntriesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = CheckRange(request.From, request.To);
        if (!IsId(request.PersonId))
        {
            errors["personId"] = ["invalid_object_id"];
        }

        if (errors.Count == 0 && request.To.DayNumber - request.From.DayNumber + 1 > PointsEntryLimits.MaxRangeDays)
        {
            errors["to"] = ["range_too_large"];
        }

        if (request.Limit is < 1 or > PointsEntryLimits.MaxLimit)
        {
            errors["limit"] = ["must be a whole number from 1 to " + PointsEntryLimits.MaxLimit.ToString(CultureInfo.InvariantCulture)];
        }

        return errors;
    }

    /// <summary>
    /// Every active user, also at 0, and every inactive user with entries in the range, in the order of the user list (ADR-0011). Money is the
    /// whole cents of earned, redeemed and the balance at the factor in force now, <see langword="null"/> while a point is worth nothing.
    /// </summary>
    public static PointsBalances BuildBalances(
        DateOnly? from, DateOnly? to, HouseholdSettings settings, IReadOnlyList<User> users, IReadOnlyList<PointTotal> totals)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(totals);
        var centsPerPoint = settings.CentsPerPoint ?? 0;
        var byPerson = totals.ToDictionary(t => t.PersonId, StringComparer.Ordinal);
        var balances = users
            .Where(u => u.Active || byPerson.ContainsKey(u.Id))
            .Select(user =>
            {
                byPerson.TryGetValue(user.Id, out var total);
                var points = total?.Points ?? 0;
                var redeemed = total?.Redeemed ?? 0;
                var earned = points + redeemed;
                return new PersonBalance(
                    user.Id,
                    points,
                    earned,
                    redeemed,
                    centsPerPoint > 0
                        ? new BalanceMoney(
                            PointsMoney.PointsToCents(earned, centsPerPoint),
                            PointsMoney.PointsToCents(redeemed, centsPerPoint),
                            PointsMoney.PointsToCents(points, centsPerPoint))
                        : null,
                    total?.Executions ?? 0,
                    total?.BonusPoints ?? 0);
            })
            .ToList();
        return new PointsBalances(from, to, settings.CurrencyCode ?? SettingsDefaults.CurrencyCode, centsPerPoint, balances);
    }
}
