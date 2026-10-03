using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using OneOf;

namespace Huishoudplanner.Application.Generation;

/// <summary>The cycle list (port of <c>routes/cycles.ts</c>): read-only, in index order, bounded and paged with a cursor.</summary>
public sealed class CycleService(ForStoringCycles cycles) : ICycleService
{
    public async Task<OneOf<CycleList, ValidationErrors, PortError>> ListAsync(int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var take = limit ?? CycleListQuery.DefaultLimit;
        if (take is < 1 or > CycleListQuery.MaxLimit)
        {
            errors["limit"] = ["must be a whole number from 1 to " + CycleListQuery.MaxLimit];
        }

        CycleCursor? after = null;
        if (cursor is not null)
        {
            if (CycleCursor.TryDecode(cursor, out var decoded))
            {
                after = decoded;
            }
            else
            {
                errors["cursor"] = ["invalid_cursor"];
            }
        }

        if (errors.Count > 0)
        {
            return new ValidationErrors(errors);
        }

        var found = await cycles.ListAsync(after, take + 1, cancellationToken).ConfigureAwait(false);
        return found.Match<OneOf<CycleList, ValidationErrors, PortError>>(
            items => items.Count > take
                ? new CycleList([.. items.Take(take)], CycleCursor.After(items[take - 1]).Encode())
                : new CycleList(items, null),
            error => error);
    }
}
