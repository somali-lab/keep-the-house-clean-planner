using System.Collections.ObjectModel;

namespace Huishoudplanner.Domain.Audit;

/// <summary>
/// The driver-free value model of an audit diff: a JSON-like tree. It replaces the loosely typed documents the Node
/// server diffs (<c>apps/server/src/audit/diff.ts</c>). Mapping from the Node/BSON world:
/// <c>null</c> is <see cref="AuditNull"/>; a missing key (<c>undefined</c>) is an absent key in an
/// <see cref="AuditObject"/>, which is not the same as <see cref="AuditNull"/>; <c>boolean</c> and <c>string</c> are
/// <see cref="AuditBool"/> and <see cref="AuditString"/>; a whole number is <see cref="AuditInteger"/> and any other
/// number <see cref="AuditDouble"/> (they compare by numeric value, like <c>1 === 1.0</c>); a <c>Date</c> is
/// <see cref="AuditInstant"/> (compared by time); an <c>ObjectId</c> is <see cref="AuditObjectId"/> (24 hex characters,
/// compared by value); a plain object is <see cref="AuditObject"/> and an array <see cref="AuditArray"/>.
/// The Mongo adapter turns the tree into BSON of the same shape the Node server writes.
/// </summary>
public abstract record AuditValue
{
    public static implicit operator AuditValue(string value) => new AuditString(value);

    public static implicit operator AuditValue(bool value) => new AuditBool(value);

    public static implicit operator AuditValue(int value) => new AuditInteger(value);

    public static implicit operator AuditValue(long value) => new AuditInteger(value);

    public static implicit operator AuditValue(double value) => new AuditDouble(value);

    public static implicit operator AuditValue(DateTimeOffset value) => new AuditInstant(value);

    public static AuditValue FromString(string value) => new AuditString(value);

    public static AuditValue FromBool(bool value) => new AuditBool(value);

    public static AuditValue FromInteger(long value) => new AuditInteger(value);

    public static AuditValue FromDouble(double value) => new AuditDouble(value);

    public static AuditValue FromInstant(DateTimeOffset value) => new AuditInstant(value);
}

/// <summary>An explicit <c>null</c>.</summary>
public sealed record AuditNull : AuditValue
{
    public static AuditNull Instance { get; } = new();
}

public sealed record AuditBool(bool Value) : AuditValue;

public sealed record AuditString(string Value) : AuditValue;

/// <summary>A whole number. Compares equal to an <see cref="AuditDouble"/> of the same numeric value.</summary>
public sealed record AuditInteger(long Value) : AuditValue;

public sealed record AuditDouble(double Value) : AuditValue;

/// <summary>A moment in time (a BSON date: millisecond precision). Compared by the instant, not the offset.</summary>
public sealed record AuditInstant(DateTimeOffset Value) : AuditValue;

/// <summary>An id as 24 lowercase hexadecimal characters; written as an <c>ObjectId</c>.</summary>
public sealed record AuditObjectId : AuditValue
{
    public AuditObjectId(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (!IsHex(hex))
        {
            throw new ArgumentException("An ObjectId is 24 hexadecimal characters.", nameof(hex));
        }

        Hex = hex.ToLowerInvariant();
    }

    public string Hex { get; }

    public static bool IsHex(string? value) =>
        value is { Length: 24 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));
}

/// <summary>An array. Always compared and diffed as a whole (element by element, in order).</summary>
public sealed record AuditArray(IReadOnlyList<AuditValue> Items) : AuditValue
{
    public static AuditArray Of(params AuditValue[] items) => new(items);

    public bool Equals(AuditArray? other) => other is not null && AuditDiff.DeepEqual(this, other);

    public override int GetHashCode() => Items.Count;
}

/// <summary>
/// An object: string keys to values, in insertion order. Equality is by content and does not depend on key order,
/// which matches the Node comparison.
/// </summary>
public sealed record AuditObject : AuditValue
{
    private readonly KeyValuePair<string, AuditValue>[] entries;
    private readonly Dictionary<string, AuditValue> lookup = new(StringComparer.Ordinal);

    /// <summary>Duplicate keys behave like an object literal: the first position and the last value win.</summary>
    public AuditObject(IEnumerable<KeyValuePair<string, AuditValue>> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var order = new List<string>();
        foreach (var property in properties)
        {
            ArgumentNullException.ThrowIfNull(property.Value);
            if (!lookup.ContainsKey(property.Key))
            {
                order.Add(property.Key);
            }

            lookup[property.Key] = property.Value;
        }

        entries = [.. order.Select(key => new KeyValuePair<string, AuditValue>(key, lookup[key]))];
    }

    public static AuditObject Empty { get; } = new([]);

    public int Count => entries.Length;

    public IReadOnlyList<KeyValuePair<string, AuditValue>> Properties => new ReadOnlyCollection<KeyValuePair<string, AuditValue>>(entries);

    public IEnumerable<string> Keys => entries.Select(e => e.Key);

    /// <summary>The value, or <see langword="null"/> when the key is absent (an explicit null is <see cref="AuditNull"/>).</summary>
    public AuditValue? this[string key] => lookup.GetValueOrDefault(key);

    public static AuditObject Of(params (string Key, AuditValue Value)[] properties) =>
        new(properties.Select(p => new KeyValuePair<string, AuditValue>(p.Key, p.Value)));

    public bool Equals(AuditObject? other) => other is not null && AuditDiff.DeepEqual(this, other);

    public override int GetHashCode() => Count;
}
