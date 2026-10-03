using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Huishoudplanner.Adapters.Notify;

/// <summary>
/// Spans and the delivery counter of plan section 3.6. The name sits under the application root so the Host pipeline
/// picks it up (<c>Huishoudplanner.*</c>). Tags are the notifier and the outcome only: never a URL, a token or a body.
/// </summary>
internal static class NotifyTelemetry
{
    public const string Name = "Huishoudplanner.Notify";
    public const string DeliveriesName = "huishoudplanner.notifications.deliveries";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    private static readonly Counter<long> Deliveries = Meter.CreateCounter<long>(
        DeliveriesName, unit: "{delivery}", description: "Notification deliveries by notifier and outcome.");

    public static void CountDelivery(string notifier, bool success) =>
        Deliveries.Add(
            1,
            new KeyValuePair<string, object?>("notifier", notifier),
            new KeyValuePair<string, object?>("outcome", success ? "success" : "failure"));
}
