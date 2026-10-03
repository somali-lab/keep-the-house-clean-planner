using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Host.Startup;

namespace Huishoudplanner.Host;

/// <summary>
/// First-run settings as a seed step (after the users, <c>seed()</c> in apps/server/src/domain/seed.ts): writes the settings of a fresh
/// installation, with the timezone of <c>TZ_APP</c>, or adds the shipped interval to an older installation. Idempotent; a failure stops the start.
/// </summary>
internal sealed partial class SettingsSeedStep(ISettingsSeedService seeding, ILogger<SettingsSeedStep> logger) : ISeedStep
{
    public string Name => "settings";

    public async Task RunAsync(CancellationToken cancellationToken)
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
            conflict => throw new InvalidOperationException($"Startup failed while seeding the settings: {conflict.Code}"),
            error => throw new InvalidOperationException($"Startup failed while seeding the settings: {error.Message}"));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Settings seed completed: {Result}")]
    private static partial void LogSeeded(ILogger logger, SettingsSeedResult result);
}
