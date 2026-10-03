namespace Huishoudplanner.Domain.Notifications;

/// <summary>
/// One notification as the Node notifier sends it: a title, a Dutch body and structured fields for receivers that
/// automate on them (Home Assistant). ntfy sends the body only; there is no priority, tag or click URL.
/// </summary>
public sealed record NotifyMessage(string Title, string Body, IReadOnlyDictionary<string, object?> Data)
{
    public NotifyMessage(string title, string body)
        : this(title, body, new Dictionary<string, object?>())
    {
    }
}
