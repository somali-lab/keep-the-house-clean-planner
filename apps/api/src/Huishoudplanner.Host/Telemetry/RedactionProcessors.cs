using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Huishoudplanner.Host.Telemetry;

/// <summary>
/// The redaction list of the Node logger (<c>authorization</c>, <c>x-api-key</c>), applied to spans and log
/// records before any exporter sees them. A new sensitive header needs an entry in <see cref="Headers"/>.
/// </summary>
public static class Redaction
{
    public static readonly IReadOnlyList<string> Headers = ["authorization", "x-api-key"];

    /// <summary>Replaces a log message that was formatted from a sensitive field.</summary>
    public const string MaskedMessage = "[redacted: the message contained a sensitive field]";

    /// <summary>
    /// True for a tag or attribute that carries a sensitive header: the bare header name or a key that ends in
    /// it, such as <c>http.request.header.authorization</c> or its underscore form <c>http.request.header.x_api_key</c>.
    /// </summary>
    public static bool IsSensitiveKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var normalized = key.Replace('-', '_').ToLowerInvariant();
        foreach (var header in Headers)
        {
            var name = header.Replace('-', '_');
            if (normalized == name || normalized.EndsWith("." + name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Removes sensitive header tags from every finished span.</summary>
public sealed class RedactingActivityProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);
        List<string>? sensitive = null;
        foreach (var tag in data.TagObjects)
        {
            if (Redaction.IsSensitiveKey(tag.Key))
            {
                (sensitive ??= []).Add(tag.Key);
            }
        }

        if (sensitive is null)
        {
            return;
        }

        foreach (var key in sensitive)
        {
            data.SetTag(key, null); // a null value removes the tag
        }
    }
}

/// <summary>Removes sensitive header attributes from every log record.</summary>
public sealed class RedactingLogRecordProcessor : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var attributes = data.Attributes;
        if (attributes is null || !attributes.Any(a => Redaction.IsSensitiveKey(a.Key)))
        {
            return;
        }

        data.Attributes = [.. attributes.Where(a => !Redaction.IsSensitiveKey(a.Key))];

        // The formatted message was rendered from the same values, so it may hold the secret.
        if (data.FormattedMessage is not null)
        {
            data.FormattedMessage = Redaction.MaskedMessage;
        }
    }
}
