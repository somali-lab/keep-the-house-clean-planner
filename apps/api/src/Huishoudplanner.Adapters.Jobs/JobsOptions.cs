namespace Huishoudplanner.Adapters.Jobs;

/// <summary>What the scheduler needs from the configuration: whether jobs run at all (<c>DISABLE_SCHEDULER</c>) and the household timezone (<c>TZ_APP</c>).</summary>
public sealed record JobsOptions(bool Enabled, TimeZoneInfo TimeZone);
