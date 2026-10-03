namespace Huishoudplanner.Host.Configuration;

/// <summary>Runtime mode. Replaces NODE_ENV; read from ASPNETCORE_ENVIRONMENT with NODE_ENV as an alias.</summary>
public enum AppEnvironment
{
    Development,
    Production,
    Test,
}

public enum NotifyType
{
    None,
    Ntfy,
    HomeAssistant,
}

/// <summary>A profile created on an empty database.</summary>
public sealed record SeedUser(string Name, string Color);

/// <summary>
/// Everything requirements section 9 configures, bound from flat environment-variable keys by
/// <see cref="AppOptionsBinder"/> and checked by <see cref="AppOptionsValidator"/>.
/// Lives in the Host for now; an adapter that needs a part of it later gets its own options type
/// (or a port-owned value) filled from here by the composition root.
/// </summary>
public sealed class AppOptions
{
    public static readonly IReadOnlyList<SeedUser> DefaultSeedUsers =
    [
        new SeedUser("Persoon 1", "#2563eb"),
        new SeedUser("Persoon 2", "#db2777"),
    ];

    public AppEnvironment Environment { get; set; } = AppEnvironment.Production;

    public int Port { get; set; } = 3000;

    public string MongoUrl { get; set; } = string.Empty;

    public string Timezone { get; set; } = "Europe/Amsterdam";

    public IReadOnlyList<SeedUser> SeedUsers { get; set; } = DefaultSeedUsers;

    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    /// <summary>Null means audit entries are kept indefinitely.</summary>
    public int? AuditRetentionDays { get; set; }

    public string? AiApiKey { get; set; }

    public NotifyType NotifyType { get; set; } = NotifyType.None;

    public string? NotifyUrl { get; set; }

    public string? NotifyToken { get; set; }

    public bool DisableScheduler { get; set; }

    public DateTimeOffset? FakeNow { get; set; }

    public string? WebDistDir { get; set; }

    /// <summary>Problems found while binding. Each names a variable and never contains a configured value.</summary>
    public IReadOnlyList<string> Issues { get; set; } = [];
}
