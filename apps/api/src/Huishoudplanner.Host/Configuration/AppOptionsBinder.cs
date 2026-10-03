using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Huishoudplanner.Host.Configuration;

/// <summary>
/// Binds the flat environment-variable keys of requirements section 9 to <see cref="AppOptions"/>.
/// Mirrors apps/server/src/config.ts: an empty value is read as unset, and no issue ever contains
/// a configured value because values may be secrets.
/// </summary>
/// <remarks>
/// Renames (plan 3.7): NODE_ENV is now ASPNETCORE_ENVIRONMENT and LOG_LEVEL is now
/// Logging__LogLevel__Default. The old names are still read as aliases (the new name wins when both
/// are set; DOTNET_ENVIRONMENT sits between them for the environment) until the switch is documented, then they are dropped. WEB_DIST_DIR keeps its meaning.
/// </remarks>
public static partial class AppOptionsBinder
{
    private const string EnvironmentKey = "ASPNETCORE_ENVIRONMENT";
    private const string DotNetEnvironmentKey = "DOTNET_ENVIRONMENT";
    private const string LegacyEnvironmentKey = "NODE_ENV";
    private const string LogLevelKey = "Logging:LogLevel:Default";
    private const string LegacyLogLevelKey = "LOG_LEVEL";

    private static readonly Dictionary<string, LogLevel> LogLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fatal"] = LogLevel.Critical,
        ["critical"] = LogLevel.Critical,
        ["error"] = LogLevel.Error,
        ["warn"] = LogLevel.Warning,
        ["warning"] = LogLevel.Warning,
        ["info"] = LogLevel.Information,
        ["information"] = LogLevel.Information,
        ["debug"] = LogLevel.Debug,
        ["trace"] = LogLevel.Trace,
        ["silent"] = LogLevel.None,
        ["none"] = LogLevel.None,
    };

    [GeneratedRegex("^#[0-9a-f]{6}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}(:?\d{2})?)$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoInstantWithOffset();

    /// <summary>Fills <paramref name="options"/> from <paramref name="configuration"/>; problems end up in <see cref="AppOptions.Issues"/>.</summary>
    public static void Bind(IConfiguration configuration, AppOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        var issues = new List<string>();

        string? Get(string key) => Unset(configuration[key]);

        var environmentKey = Get(EnvironmentKey) is not null ? EnvironmentKey
            : Get(DotNetEnvironmentKey) is not null ? DotNetEnvironmentKey
            : LegacyEnvironmentKey;
        var environmentOk = true;
        if (Get(environmentKey) is { } rawEnvironment)
        {
            if (TryParseEnvironment(rawEnvironment, out var environment))
            {
                options.Environment = environment;
            }
            else
            {
                environmentOk = false;
                issues.Add($"{environmentKey}: must be one of development, production, test");
            }
        }

        if (Get("PORT") is { } rawPort)
        {
            if (TryParseInt(rawPort, out var port) && port is >= 1 and <= 65535)
            {
                options.Port = port;
            }
            else
            {
                issues.Add("PORT: must be a whole number from 1 to 65535");
            }
        }

        // Unlike every other variable an empty MONGO_URL is invalid, not unset.
        var mongoUrl = configuration["MONGO_URL"];
        if (string.IsNullOrEmpty(mongoUrl))
        {
            issues.Add("MONGO_URL: is required");
        }
        else
        {
            options.MongoUrl = mongoUrl;
        }

        if (Get("TZ_APP") is { } timezone)
        {
            options.Timezone = timezone;
        }

        if (Get("SEED_USERS") is { } rawSeedUsers)
        {
            if (TryParseSeedUsers(rawSeedUsers, out var seedUsers))
            {
                options.SeedUsers = seedUsers;
            }
            else
            {
                issues.Add("SEED_USERS: must be a JSON array of at least one { \"name\", \"color\" } with a non-empty name and a #rrggbb colour");
            }
        }

        var logLevelKey = Get(LogLevelKey) is not null ? LogLevelKey : LegacyLogLevelKey;
        if (Get(logLevelKey) is { } rawLogLevel)
        {
            if (LogLevels.TryGetValue(rawLogLevel, out var logLevel))
            {
                options.LogLevel = logLevel;
            }
            else
            {
                issues.Add($"{logLevelKey}: must be one of fatal, error, warn, info, debug, trace, silent");
            }
        }

        if (Get("AUDIT_RETENTION_DAYS") is { } rawRetention)
        {
            if (TryParseInt(rawRetention, out var days) && days >= 1)
            {
                options.AuditRetentionDays = days;
            }
            else
            {
                issues.Add("AUDIT_RETENTION_DAYS: must be a whole number of at least 1");
            }
        }

        options.AiApiKey = Get("AI_API_KEY");
        options.NotifyToken = Get("NOTIFY_TOKEN");
        options.WebDistDir = Get("WEB_DIST_DIR");

        var notifyTypeOk = true;
        if (Get("NOTIFY_TYPE") is { } rawNotifyType)
        {
            switch (rawNotifyType)
            {
                case "none":
                    options.NotifyType = NotifyType.None;
                    break;
                case "ntfy":
                    options.NotifyType = NotifyType.Ntfy;
                    break;
                case "homeassistant":
                    options.NotifyType = NotifyType.HomeAssistant;
                    break;
                default:
                    notifyTypeOk = false;
                    issues.Add("NOTIFY_TYPE: must be one of none, ntfy, homeassistant");
                    break;
            }
        }

        if (Get("NOTIFY_URL") is { } rawNotifyUrl)
        {
            // config.ts accepts any URL with a scheme; notification targets are http(s) only, so stricter on purpose.
            if (Uri.TryCreate(rawNotifyUrl, UriKind.Absolute, out var notifyUri)
                && (notifyUri.Scheme == Uri.UriSchemeHttp || notifyUri.Scheme == Uri.UriSchemeHttps))
            {
                options.NotifyUrl = rawNotifyUrl;
            }
            else
            {
                issues.Add("NOTIFY_URL: must be an absolute http or https URL");
            }
        }

        if (Get("DISABLE_SCHEDULER") is { } rawScheduler)
        {
            switch (rawScheduler)
            {
                case "true":
                    options.DisableScheduler = true;
                    break;
                case "false":
                    options.DisableScheduler = false;
                    break;
                default:
                    issues.Add("DISABLE_SCHEDULER: must be true or false");
                    break;
            }
        }

        var fakeNowOk = true;
        if (Get("APP_FAKE_NOW") is { } rawFakeNow)
        {
            if (IsoInstantWithOffset().IsMatch(rawFakeNow)
                && DateTimeOffset.TryParse(rawFakeNow, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fakeNow))
            {
                options.FakeNow = fakeNow;
            }
            else
            {
                fakeNowOk = false;
                issues.Add("APP_FAKE_NOW: must be an ISO instant with an offset");
            }
        }

        // Cross-field rules only run on parsed fields, like the superRefine of config.ts.
        if (fakeNowOk && environmentOk && options.FakeNow is not null && options.Environment != AppEnvironment.Test)
        {
            issues.Add("APP_FAKE_NOW: is only allowed when ASPNETCORE_ENVIRONMENT=test");
        }

        if (notifyTypeOk && options.NotifyType != NotifyType.None && Get("NOTIFY_URL") is null)
        {
            issues.Add("NOTIFY_URL: is required when NOTIFY_TYPE is not none");
        }

        options.Issues = issues;
    }

    /// <summary>
    /// The environment name for the host when only the legacy NODE_ENV is set, so that
    /// <c>IHostEnvironment</c> follows the alias. Null when nothing needs overriding.
    /// </summary>
    public static string? ResolveLegacyHostEnvironment(Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        if (Unset(getVariable(EnvironmentKey)) is not null
            || Unset(getVariable(DotNetEnvironmentKey)) is not null
            || Unset(getVariable(LegacyEnvironmentKey)) is not { } legacy
            || !TryParseEnvironment(legacy, out var environment))
        {
            return null;
        }

        return environment switch
        {
            AppEnvironment.Development => Environments.Development,
            AppEnvironment.Test => "Test",
            _ => Environments.Production,
        };
    }

    /// <summary>
    /// Configuration entries that make the .NET logging system follow the legacy LOG_LEVEL when
    /// Logging__LogLevel__Default is unset. Empty when there is nothing to forward.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> LegacyLogLevelOverride(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (Unset(configuration[LogLevelKey]) is null
            && Unset(configuration[LegacyLogLevelKey]) is { } legacy
            && LogLevels.TryGetValue(legacy, out var level))
        {
            return new Dictionary<string, string?> { [LogLevelKey] = level.ToString() };
        }

        return new Dictionary<string, string?>();
    }

    private static string? Unset(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static bool TryParseEnvironment(string value, out AppEnvironment environment)
    {
        switch (value.ToUpperInvariant())
        {
            case "DEVELOPMENT":
                environment = AppEnvironment.Development;
                return true;
            case "PRODUCTION":
                environment = AppEnvironment.Production;
                return true;
            case "TEST":
                environment = AppEnvironment.Test;
                return true;
            default:
                environment = default;
                return false;
        }
    }

    private static bool TryParseInt(string value, out int result) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static bool TryParseSeedUsers(string json, out IReadOnlyList<SeedUser> users)
    {
        users = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var parsed = new List<SeedUser>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                    || !element.TryGetProperty("color", out var color) || color.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                var trimmed = name.GetString()!.Trim();
                var colorValue = color.GetString()!;
                if (trimmed.Length == 0 || !HexColor().IsMatch(colorValue))
                {
                    return false;
                }

                parsed.Add(new SeedUser(trimmed, colorValue));
            }

            if (parsed.Count == 0)
            {
                return false;
            }

            users = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
