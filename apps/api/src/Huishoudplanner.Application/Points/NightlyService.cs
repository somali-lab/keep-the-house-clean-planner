using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.Logging;
using OneOf;

namespace Huishoudplanner.Application.Points;

/// <summary>
/// The scheduled nightly run (requirements 4.10; <c>runNightly</c> of the Node server): generation first, then the reconciliation of the ledger,
/// which repairs drift within a day. A reconciliation that fails is logged and never fails the run. The scheduler of slice 6.3 calls it.
/// </summary>
public sealed class NightlyService(IGenerationService generation, IPointsService points, ILogger<NightlyService> logger) : INightlyService
{
    public async Task<OneOf<NightlyRun, SettingsMissing, ConflictError, PortError>> RunAsync(AuditActor actor, string runId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var generated = await generation.GenerateUpcomingAsync(actor, runId, cancellationToken).ConfigureAwait(false);
        if (!generated.TryPickT0(out var run, out var failure))
        {
            return failure.Match<OneOf<NightlyRun, SettingsMissing, ConflictError, PortError>>(missing => missing, conflict => conflict, error => error);
        }

        var result = await SafeReconcile.RunAsync(points, actor, PointsRecomputeTrigger.Nightly, logger, cancellationToken).ConfigureAwait(false);
        return new NightlyRun(run, result);
    }
}
