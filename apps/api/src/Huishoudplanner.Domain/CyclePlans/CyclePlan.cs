using System.Buffers.Text;
using System.Text.Json;

namespace Huishoudplanner.Domain.CyclePlans;

/// <summary>
/// One placement of a task in a stored plan (requirements 3, <c>cyclePlans.slots</c>). <see cref="Weekday"/> is 0=Sunday..6=Saturday,
/// <see cref="AssigneeId"/> is <see langword="null"/> for "anyone". Ids are 24 character lowercase hexadecimal ids.
/// </summary>
public sealed record CyclePlanSlot(string TaskId, int WeekIndex, int Weekday, string? AssigneeId, int SortOrder = 0);

/// <summary>The values of <c>cyclePlans.source</c>.</summary>
public static class PlanSources
{
    public const string Manual = "manual";

    public const string Ai = "ai";
}

/// <summary>
/// A four-week plan (requirements 3, <c>cyclePlans</c>). Exactly one plan is active; the oldest plan is the default plan. A plan from
/// the AI flow also carries a proposal id and a rationale per week; <see cref="Draft"/> and <see cref="Discarded"/> belong to that flow.
/// </summary>
public sealed record CyclePlan(
    string Id,
    string Name,
    bool Active,
    IReadOnlyList<CyclePlanSlot> Slots,
    IReadOnlyList<string> WeekThemes,
    bool Draft,
    string Source,
    string? ProposalId,
    IReadOnlyList<string>? Rationale,
    bool Discarded,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool Equals(CyclePlan? other) =>
        other is not null &&
        Id == other.Id && Name == other.Name && Active == other.Active && Draft == other.Draft && Source == other.Source &&
        ProposalId == other.ProposalId && Discarded == other.Discarded && CreatedAt == other.CreatedAt && UpdatedAt == other.UpdatedAt &&
        Slots.SequenceEqual(other.Slots) && WeekThemes.SequenceEqual(other.WeekThemes, StringComparer.Ordinal) &&
        (Rationale is null ? other.Rationale is null : other.Rationale is not null && Rationale.SequenceEqual(other.Rationale, StringComparer.Ordinal));

    public override int GetHashCode() => HashCode.Combine(Id, Name, Active, Slots.Count);
}

/// <summary>A plan as the store is asked to create it: a manual plan that is not a draft. The store assigns the id.</summary>
public sealed record NewCyclePlan(string Name, bool Active, IReadOnlyList<CyclePlanSlot> Slots, IReadOnlyList<string> WeekThemes, DateTimeOffset CreatedAt);

/// <summary>The fields a meta write changes: exactly the ones that differ from the stored plan. <c>updatedAt</c> is set by the store.</summary>
public sealed record PlanMetaChanges(string? Name = null, IReadOnlyList<string>? WeekThemes = null);

/// <summary>A partial update (name and week themes only); a member that is <see langword="null"/> stays as it is.</summary>
public sealed record CyclePlanPatch(string? Name = null, IReadOnlyList<string>? WeekThemes = null);

/// <summary>The request to create a plan, empty or as a copy of another one.</summary>
public sealed record CreateCyclePlanCommand(string Name, string? CopyFromId = null);

/// <summary>One page of plans, oldest first. <see cref="NextCursor"/> is <see langword="null"/> on the last page.</summary>
public sealed record CyclePlanList(IReadOnlyList<CyclePlan> Items, string? NextCursor);

public static class CyclePlanListQuery
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;
}

/// <summary>The position after a plan in the list order (<c>createdAt</c>, then id). Opaque to clients.</summary>
public sealed record CyclePlanCursor(DateTimeOffset CreatedAt, string Id)
{
    public static CyclePlanCursor After(CyclePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new(plan.CreatedAt, plan.Id);
    }

    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new object[] { CreatedAt.ToUnixTimeMilliseconds(), Id }));

    /// <summary>False for anything this application did not produce.</summary>
    public static bool TryDecode(string? value, out CyclePlanCursor cursor)
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
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 2 ||
                root[0].ValueKind != JsonValueKind.Number || !root[0].TryGetInt64(out var millis) ||
                root[1].ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var id = root[1].GetString()!;
            if (!CyclePlanRules.IsId(id))
            {
                return false;
            }

            cursor = new CyclePlanCursor(DateTimeOffset.FromUnixTimeMilliseconds(millis), id.ToLowerInvariant());
            return true;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
