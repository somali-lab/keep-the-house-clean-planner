using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Notifications;
using Huishoudplanner.Domain.Ports.Driven;
using OneOf;

namespace Huishoudplanner.Integration.Tests.Fixtures;

/// <summary>A notification channel that records what would have been sent and can refuse it. Tests never call a real notification endpoint.</summary>
public sealed class RecordingNotifier(bool enabled = true) : ForSendingNotifications
{
    private readonly List<NotifyMessage> sent = [];

    public bool IsEnabled => enabled;

    /// <summary>When set, every delivery is refused with this error.</summary>
    public PortError? Refusal { get; set; }

    public IReadOnlyList<NotifyMessage> Sent
    {
        get
        {
            lock (sent)
            {
                return [.. sent];
            }
        }
    }

    public Task<OneOf<Success, PortError>> SendAsync(NotifyMessage message, CancellationToken cancellationToken)
    {
        lock (sent)
        {
            sent.Add(message);
        }

        return Task.FromResult<OneOf<Success, PortError>>(Refusal is { } refusal ? refusal : new Success());
    }
}
