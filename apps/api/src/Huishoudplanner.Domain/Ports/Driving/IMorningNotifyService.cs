using Huishoudplanner.Domain.Notifications;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>
/// The morning notification (requirements 4.10): one Dutch message per active person with the open tasks planned for them today, the open tasks
/// planned for "anyone" and the tasks the due engine marks as overdue, sent through the configured channel. Called by the 07:30 job and by the manual
/// trigger. It writes nothing and never fails: a read that fails is <see cref="MorningStatus.Error"/>, a delivery that fails is counted.
/// </summary>
public interface IMorningNotifyService
{
    Task<MorningResult> RunAsync(CancellationToken cancellationToken);
}
