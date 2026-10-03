namespace Huishoudplanner.Domain.Audit;

/// <summary>
/// Port of <c>apps/server/src/audit/diff.ts</c>. Changed fields only: nested objects are diffed recursively, arrays,
/// instants and ids are compared as a whole and by value, absent keys stay absent.
/// </summary>
public static class AuditDiff
{
    /// <summary>Top-level keys left out when the caller gives no list.</summary>
    public static IReadOnlyList<string> DefaultIgnore { get; } = ["updatedAt"];

    /// <param name="before"><see langword="null"/> stands for no document (a create).</param>
    /// <param name="after"><see langword="null"/> stands for no document (a delete).</param>
    /// <param name="ignore">Top-level keys to leave out; default <see cref="DefaultIgnore"/>. Nested objects ignore nothing.</param>
    public static FieldDiff Diff(AuditObject? before, AuditObject? after, IEnumerable<string>? ignore = null)
    {
        var ignored = new HashSet<string>(ignore ?? DefaultIgnore, StringComparer.Ordinal);
        return DiffObjects(before ?? AuditObject.Empty, after ?? AuditObject.Empty, ignored);
    }

    private static FieldDiff DiffObjects(AuditObject before, AuditObject after, HashSet<string> ignored)
    {
        var beforeChanged = new List<KeyValuePair<string, AuditValue>>();
        var afterChanged = new List<KeyValuePair<string, AuditValue>>();

        foreach (var key in before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal))
        {
            if (ignored.Contains(key))
            {
                continue;
            }

            var bv = before[key];
            var av = after[key];
            if (bv is AuditObject bo && av is AuditObject ao)
            {
                var nested = DiffObjects(bo, ao, []);
                if (!nested.IsEmpty)
                {
                    beforeChanged.Add(new(key, nested.Before));
                    afterChanged.Add(new(key, nested.After));
                }

                continue;
            }

            if (DeepEqual(bv, av))
            {
                continue;
            }

            if (bv is not null)
            {
                beforeChanged.Add(new(key, bv));
            }

            if (av is not null)
            {
                afterChanged.Add(new(key, av));
            }
        }

        return new FieldDiff(new AuditObject(beforeChanged), new AuditObject(afterChanged));
    }

    /// <summary>
    /// Structural equality as the Node <c>deepEqual</c> defines it. <see langword="null"/> (absent) equals only absent;
    /// <see cref="AuditNull"/> equals only <see cref="AuditNull"/>; numbers compare by value; types never coerce.
    /// </summary>
    public static bool DeepEqual(AuditValue? a, AuditValue? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        return (a, b) switch
        {
            (null, _) or (_, null) => false,
            (AuditNull, AuditNull) => true,
            (AuditBool x, AuditBool y) => x.Value == y.Value,
            (AuditString x, AuditString y) => string.Equals(x.Value, y.Value, StringComparison.Ordinal),
            (AuditInteger x, AuditInteger y) => x.Value == y.Value,
            (AuditDouble x, AuditDouble y) => x.Value == y.Value,
            (AuditInteger x, AuditDouble y) => y.Value == x.Value,
            (AuditDouble x, AuditInteger y) => x.Value == y.Value,
            (AuditInstant x, AuditInstant y) => x.Value.ToUnixTimeMilliseconds() == y.Value.ToUnixTimeMilliseconds(),
            (AuditObjectId x, AuditObjectId y) => string.Equals(x.Hex, y.Hex, StringComparison.Ordinal),
            (AuditArray x, AuditArray y) => x.Items.Count == y.Items.Count && x.Items.Zip(y.Items).All(p => DeepEqual(p.First, p.Second)),
            (AuditObject x, AuditObject y) => ObjectsEqual(x, y),
            _ => false,
        };
    }

    private static bool ObjectsEqual(AuditObject x, AuditObject y) =>
        x.Keys.Concat(y.Keys).Distinct(StringComparer.Ordinal).All(key => DeepEqual(x[key], y[key]));
}
