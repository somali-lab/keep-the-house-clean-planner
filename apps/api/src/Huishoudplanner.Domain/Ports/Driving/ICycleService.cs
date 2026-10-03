using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>The generated cycles (requirements 3 and 8, <c>GET /cycles</c>): a read-only list that the export dialog uses to know which weeks exist.</summary>
public interface ICycleService
{
    /// <summary>A page of cycles in index order (negative indexes first). A bad <c>limit</c> or cursor is a <see cref="ValidationErrors"/>.</summary>
    Task<OneOf<CycleList, ValidationErrors, PortError>> ListAsync(int? limit, string? cursor, CancellationToken cancellationToken);
}
