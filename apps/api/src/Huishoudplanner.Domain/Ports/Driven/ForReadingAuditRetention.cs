using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

#pragma warning disable CA1715

/// <summary>The configured audit retention (<c>AUDIT_RETENTION_DAYS</c>).</summary>
public interface ForReadingAuditRetention
{
    Task<OneOf<AuditRetentionPolicy, PortError>> GetAsync(CancellationToken cancellationToken);
}
