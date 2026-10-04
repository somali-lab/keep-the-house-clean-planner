using Huishoudplanner.Domain.Notifications;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.DependencyInjection;

namespace Huishoudplanner.Adapters.Jobs;

/// <summary>
/// The morning message at 07:30 (requirements 4.10). Like in the Node scheduler it is only scheduled when a notification channel is configured
/// (<see cref="Enabled"/> is the channel's <see cref="ForSendingNotifications.IsEnabled"/>). The use case never fails; a run that could not read its
/// data (<see cref="MorningStatus.Error"/>) is logged by the use case and ends here as <see cref="JobOutcome.Failed"/>. It writes nothing, so it has no actor.
/// </summary>
public sealed class MorningNotifyJob(ForSendingNotifications notifier) : IJob
{
    public const string JobName = "morning-notify";

    public const string CronSchedule = "30 7 * * *";

    public string Name => JobName;

    public string Schedule => CronSchedule;

    public bool Enabled => notifier.IsEnabled;

    public async Task<JobOutcome> RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var result = await services.GetRequiredService<IMorningNotifyService>().RunAsync(cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            MorningStatus.Disabled => JobOutcome.Skipped,
            MorningStatus.Error => JobOutcome.Failed,
            _ => JobOutcome.Succeeded,
        };
    }
}
