using System.Globalization;
using System.Text.RegularExpressions;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Adapters.Http.Audit;

/// <summary>
/// Reads the query of <c>GET /api/v2/audit</c> from strings, so that a malformed value is a field-keyed
/// <c>400 validation_error</c> and never a framework binding failure. Only the syntax is checked here (a name, a number, an
/// ISO instant); the rules (id format, limit range, cursor) belong to the use case.
/// </summary>
internal static partial class AuditQueryParser
{
    public static OneOf<(AuditLogFilter Filter, int? Limit), ValidationErrors> Parse(
        string? entity, string? entityId, string? actorId, string? source, string? from, string? to, string? limit)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        AuditEntity? entityFilter = null;
        if (entity is not null)
        {
            if (AuditNames.TryParseEntity(entity, out var parsed))
            {
                entityFilter = parsed;
            }
            else
            {
                errors["entity"] = ["must be one of: " + string.Join(", ", AuditNames.EntityNames)];
            }
        }

        AuditSource? sourceFilter = null;
        if (source is not null)
        {
            if (AuditNames.TryParseSource(source, out var parsed))
            {
                sourceFilter = parsed;
            }
            else
            {
                errors["source"] = ["must be one of: " + string.Join(", ", AuditNames.SourceNames)];
            }
        }

        var fromValue = Instant(from, "from", errors);
        var toValue = Instant(to, "to", errors);

        int? limitValue = null;
        if (limit is not null)
        {
            if (int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                limitValue = parsed;
            }
            else
            {
                errors["limit"] = ["Must be an integer."];
            }
        }

        return errors.Count > 0
            ? new ValidationErrors(errors)
            : (new AuditLogFilter(entityFilter, entityId, actorId, sourceFilter, fromValue, toValue), limitValue);
    }

    private static DateTimeOffset? Instant(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            return null;
        }

        if (IsoInstant().IsMatch(value) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        errors[field] = ["must be an ISO 8601 date and time with a time zone, for example 2026-09-18T08:00:00.000Z"];
        return null;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}(:?\d{2})?)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex IsoInstant();
}
