namespace Huishoudplanner.Adapters.Notify;

/// <summary>Which receiver the installation notifies (NOTIFY_TYPE).</summary>
public enum NotifyKind
{
    None,
    Ntfy,
    HomeAssistant,
}

/// <summary>
/// The adapter's own options, filled by the composition root. <see cref="Token"/> is a secret and <see cref="Url"/>
/// is as good as one (an ntfy topic URL); neither is ever logged or put in an error.
/// </summary>
public sealed record NotifyEndpoint(NotifyKind Kind, string? Url, string? Token, TimeSpan? Timeout = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public override string ToString() => $"NotifyEndpoint {{ Kind = {Kind} }}";
}
