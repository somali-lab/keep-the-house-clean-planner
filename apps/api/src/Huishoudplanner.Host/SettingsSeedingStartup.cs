using Huishoudplanner.Domain.Ports.Driving;

namespace Huishoudplanner.Host;

/// <summary>
/// First-run settings at startup, before the host accepts requests (<c>seed()</c> in apps/server/src/domain/seed.ts): writes the settings of
/// a fresh installation, with the timezone of <c>TZ_APP</c>, or adds the shipped interval to an older installation. Idempotent, so
/// every start may run it. A failure stops the start: nothing works without settings.
/// </summary>
public sealed partial class SettingsSeedingStartup(ISettingsSeedService seeding, ILogger<SettingsSeedingStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var result = await seeding.SeedAsync(cancellationToken);
        result.Switch(
            outcome =>
            {
                if (outcome != SettingsSeedResult.Unchanged)
                {
                    LogSeeded(logger, outcome);
                }
            },
            conflict => throw new InvalidOperationException($"Seeding the settings failed: {conflict.Code}."),
            error => throw new InvalidOperationException($"Seeding the settings failed: {error.Message}"));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Settings seed completed: {Result}")]
    private static partial void LogSeeded(ILogger logger, SettingsSeedResult result);
}
