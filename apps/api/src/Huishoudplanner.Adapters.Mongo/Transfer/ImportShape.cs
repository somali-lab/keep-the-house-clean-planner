using System.Text.RegularExpressions;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using MongoDB.Bson;

namespace Huishoudplanner.Adapters.Mongo.Transfer;

/// <summary>
/// The problems found in an import file, keyed by the path in the file (<c>collections.users.0.color</c>). Bounded: after
/// <see cref="Max"/> problems nothing more is recorded, so a file full of garbage cannot make the answer grow with it.
/// </summary>
internal sealed class ImportIssues
{
    public const int Max = 200;

    private readonly Dictionary<string, List<string>> errors = new(StringComparer.Ordinal);
    private int count;

    public bool Any => count > 0;

    public bool Full => count >= Max;

    public void Add(string field, string message)
    {
        if (Full)
        {
            return;
        }

        count++;
        if (!errors.TryGetValue(field, out var messages))
        {
            messages = [];
            errors[field] = messages;
        }

        if (!messages.Contains(message, StringComparer.Ordinal))
        {
            messages.Add(message);
        }
    }

    public ValidationErrors ToErrors() => new(errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal));
}

/// <summary>A check of one value of a document: the value, its path in the file and where to report.</summary>
internal delegate void Check(BsonValue value, string path, ImportIssues issues);

/// <summary>One field of a document shape.</summary>
internal sealed record Field(string Name, Check Check, bool Required);

/// <summary>
/// A tiny declarative shape language over BSON values, the counterpart of the zod schemas of <c>packages/shared</c> (<c>settingsSchema</c>,
/// <c>userSchema</c> and so on) and of the storage type checks of <c>domain/transfer.ts</c> in one: a value must have the stored BSON type (ids as
/// <c>ObjectId</c>, instants as dates, never their string form), the value rules of the schema, and unknown fields are ignored. The codes are the
/// ones of the Node server where it has one (<c>invalid_color</c>, <c>expected_object_id</c>, <c>expected_date</c>) and short snake_case words otherwise.
/// </summary>
internal static partial class Shape
{
    public static Field Req(string name, Check check) => new(name, check, true);

    public static Field Opt(string name, Check check) => new(name, check, false);

    public static Check Nullable(Check inner) => (value, path, issues) =>
    {
        if (!value.IsBsonNull)
        {
            inner(value, path, issues);
        }
    };

    /// <summary>Only <c>null</c> (a redemption has no occurrence, task or period).</summary>
    public static Check Null { get; } = (value, path, issues) =>
    {
        if (!value.IsBsonNull)
        {
            issues.Add(path, "expected_null");
        }
    };

    public static Check Bool { get; } = (value, path, issues) =>
    {
        if (!value.IsBoolean)
        {
            issues.Add(path, "expected_boolean");
        }
    };

    public static Check Id { get; } = (value, path, issues) =>
    {
        if (!value.IsObjectId)
        {
            issues.Add(path, "expected_object_id");
        }
    };

    public static Check Date { get; } = (value, path, issues) =>
    {
        if (!value.IsValidDateTime)
        {
            issues.Add(path, "expected_date");
        }
    };

    public static Check Binary { get; } = (value, path, issues) =>
    {
        if (!value.IsBsonBinaryData)
        {
            issues.Add(path, "expected_binary");
        }
    };

    /// <summary>A string for which <paramref name="rule"/> returns no code (<see langword="null"/>).</summary>
    public static Check Str(Func<string, string?>? rule = null) => (value, path, issues) =>
    {
        if (!value.IsString)
        {
            issues.Add(path, "expected_string");
        }
        else if (rule?.Invoke(value.AsString) is { } code)
        {
            issues.Add(path, code);
        }
    };

    public static Check Text { get; } = Str();

    /// <summary>A string that is not empty after trimming (<c>z.string().trim().min(1)</c>).</summary>
    public static Check NonBlank { get; } = Str(s => s.Trim().Length == 0 ? "required" : null);

    public static Check MaxLength(int max, bool nonBlank = false) =>
        Str(s => nonBlank && s.Trim().Length == 0 ? "required" : s.Length > max ? "too_long" : null);

    public static Check Matching(Regex pattern, string code) => Str(s => pattern.IsMatch(s) ? null : code);

    public static Check Choice(params string[] values) => Str(s => values.Contains(s, StringComparer.Ordinal) ? null : "invalid_enum");

    public static Check Equal(string expected) => Str(s => s == expected ? null : "invalid_enum");

    public static Check DayKey { get; } = Str(s => DayKeys.IsDayKey(s) ? null : "invalid_day_key");

    /// <summary>An integer from <paramref name="min"/> to <paramref name="max"/>; a double without a fraction counts (a JSON file does not tell 5 from 5.0).</summary>
    public static Check Int(long min = long.MinValue, long max = long.MaxValue) => (value, path, issues) =>
    {
        if (!TryInteger(value, out var number))
        {
            issues.Add(path, "expected_integer");
        }
        else if (number < min || number > max)
        {
            issues.Add(path, "out_of_range");
        }
    };

    public static Check Items(Check item, int? max = null, Func<BsonArray, string?>? whole = null) => (value, path, issues) =>
    {
        if (!value.IsBsonArray)
        {
            issues.Add(path, "expected_array");
            return;
        }

        var items = value.AsBsonArray;
        if (max is { } limit && items.Count > limit)
        {
            issues.Add(path, "too_many");
        }

        for (var i = 0; i < items.Count && !issues.Full; i++)
        {
            item(items[i], $"{path}.{i}", issues);
        }

        if (whole?.Invoke(items) is { } code)
        {
            issues.Add(path, code);
        }
    };

    /// <summary>An array of exactly <paramref name="length"/> items (<c>z.tuple</c>).</summary>
    public static Check Exactly(int length, Check item) => (value, path, issues) =>
    {
        if (value.IsBsonArray && value.AsBsonArray.Count != length)
        {
            issues.Add(path, "invalid_length");
            return;
        }

        Items(item)(value, path, issues);
    };

    /// <summary>Any document (<c>z.record(z.string(), z.unknown())</c>).</summary>
    public static Check AnyDocument { get; } = (value, path, issues) =>
    {
        if (!value.IsBsonDocument)
        {
            issues.Add(path, "expected_object");
        }
    };

    public static Check Doc(params Field[] fields) => new DocShape(fields).Validate;

    /// <summary>Whether the value is an integer, and which.</summary>
    public static bool TryInteger(BsonValue value, out long number)
    {
        switch (value.BsonType)
        {
            case BsonType.Int32:
                number = value.AsInt32;
                return true;
            case BsonType.Int64:
                number = value.AsInt64;
                return true;
            case BsonType.Double when Math.Abs(value.AsDouble) < 9e15 && value.AsDouble == Math.Floor(value.AsDouble):
                number = (long)value.AsDouble;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    public static partial Regex HexColor();

    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")]
    public static partial Regex TimeOfDay();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    public static partial Regex Sha256Hex();

    [GeneratedRegex("^[A-Za-z0-9_-]{16,64}$")]
    public static partial Regex RequestKey();

    [GeneratedRegex("^redemption:[0-9a-f]{24}$")]
    public static partial Regex RedemptionKey();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$")]
    public static partial Regex IsoInstant();
}

/// <summary>The shape of one document: its fields, and the field names that survive an import (everything else is dropped, like the zod parse of the Node server).</summary>
internal sealed class DocShape(IReadOnlyList<Field> fields)
{
    public IReadOnlySet<string> Known { get; } = fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

    public void Validate(BsonValue value, string path, ImportIssues issues)
    {
        if (!value.IsBsonDocument)
        {
            issues.Add(path, "expected_object");
            return;
        }

        var doc = value.AsBsonDocument;
        foreach (var field in fields)
        {
            if (issues.Full)
            {
                return;
            }

            if (doc.TryGetValue(field.Name, out var inner))
            {
                field.Check(inner, $"{path}.{field.Name}", issues);
            }
            else if (field.Required)
            {
                issues.Add($"{path}.{field.Name}", "required");
            }
        }
    }

    /// <summary>The document with only the known fields, in the order of the file.</summary>
    public BsonDocument Keep(BsonDocument doc) => new(doc.Elements.Where(e => Known.Contains(e.Name)));
}
