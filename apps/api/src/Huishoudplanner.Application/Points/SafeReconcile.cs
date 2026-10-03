using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Application.Points;

/// <summary>
/// The reconciliation for a caller that must not fail because of it (startup, the nightly job, an import; <c>reconcilePointsSafely</c> of the Node
/// server): a failure is logged and answered with <see langword="null"/>.
/// </summary>
public static partial class SafeReconcile
{
    public static async Task<PointsRecomputeResult?> RunAsync(
        IPointsService points, AuditActor actor, PointsRecomputeTrigger trigger, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            var result = await points.RecomputeAsync(actor, trigger, cancellationToken).ConfigureAwait(false);
            if (result.TryPickT0(out var done, out var failure))
            {
                if (done.BonusStepSkipped)
                {
                    LogBonusSkipped(logger, PointNames.ToWire(trigger));
                }

                return done;
            }

            LogFailed(logger, PointNames.ToWire(trigger), failure.Match(conflict => conflict.Code, error => error.Message));
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogThrown(logger, e, PointNames.ToWire(trigger));
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Points reconciliation ({Trigger}) skipped the bonuses: the cycle anchor is not a Monday")]
    private static partial void LogBonusSkipped(ILogger logger, string trigger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Points reconciliation failed ({Trigger}): {Reason}")]
    private static partial void LogFailed(ILogger logger, string trigger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Points reconciliation failed ({Trigger})")]
    private static partial void LogThrown(ILogger logger, Exception exception, string trigger);
}
