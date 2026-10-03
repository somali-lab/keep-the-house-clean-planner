using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Notifications;
using Huishoudplanner.Domain.Ports.Driven;
using OneOf;

namespace Huishoudplanner.Adapters.Notify;

/// <summary>NOTIFY_TYPE=none: notifications are off, nothing is sent and nothing fails.</summary>
internal sealed class NoneNotifier : ForSendingNotifications
{
    public bool IsEnabled => false;

    public Task<OneOf<Success, PortError>> SendAsync(NotifyMessage message, CancellationToken cancellationToken) =>
        Task.FromResult<OneOf<Success, PortError>>(new Success());
}
