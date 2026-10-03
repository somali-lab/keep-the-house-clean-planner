using System.Buffers.Text;
using System.Text.Json;

namespace Huishoudplanner.Domain.Generation;

/// <summary>
/// One generated cycle (requirements 3, <c>cycles</c>): 28 days from a Monday, <see cref="Index"/> 0 being the cycle that starts on the
/// anchor date and negative before it. A day in a cycle that has no document is not generated yet. <see cref="PlanId"/> is the plan the
/// cycle was generated from.
/// </summary>
public sealed record Cycle(
    string Id,
    int Index,
    DateOnly StartDate,
    DateOnly EndDate,
    string? PlanId,
    DateTimeOffset GeneratedAt,
    string GenerationRunId);

/// <summary>A cycle as the store is asked to create it. The store assigns the id.</summary>
public sealed record NewCycle(int Index, DateOnly StartDate, DateOnly EndDate, string? PlanId, DateTimeOffset GeneratedAt, string GenerationRunId);

/// <summary>One page of cycles in index order. <see cref="NextCursor"/> is <see langword="null"/> on the last page.</summary>
public sealed record CycleList(IReadOnlyList<Cycle> Items, string? NextCursor);

public static class CycleListQuery
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;
}

/// <summary>The position after a cycle in the list order (its index, which can be negative). Opaque to clients.</summary>
public sealed record CycleCursor(int Index)
{
    public static CycleCursor After(Cycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        return new(cycle.Index);
    }

    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new object[] { Index }));

    /// <summary>False for anything this application did not produce.</summary>
    public static bool TryDecode(string? value, out CycleCursor cursor)
    {
        cursor = null!;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(value));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 1 ||
                root[0].ValueKind != JsonValueKind.Number || !root[0].TryGetInt32(out var index))
            {
                return false;
            }

            cursor = new CycleCursor(index);
            return true;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
