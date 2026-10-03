using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Notifications;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// Delivers a notification to the configured receiver (ntfy, Home Assistant, or nowhere). Never throws on delivery trouble (only cancellation by the caller's token propagates): a refused or
/// failed delivery is a <see cref="PortError"/> whose message names the notifier and the HTTP status only, never the
/// URL (an ntfy topic URL is effectively a secret) or the token.
/// </summary>
public interface ForSendingNotifications
{
    /// <summary>False for the <c>none</c> notifier: callers skip the work and report the job as disabled.</summary>
    bool IsEnabled { get; }

    Task<OneOf<Success, PortError>> SendAsync(NotifyMessage message, CancellationToken cancellationToken);
}
