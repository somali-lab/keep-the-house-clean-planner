using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.Hosting;

namespace Huishoudplanner.Host.Startup;

/// <summary>
/// The one startup task of the host, in the order the data needs: first the storage is prepared (migrations, then the
/// collections and indexes), then every <see cref="ISeedStep"/> runs. It runs before the host starts listening, and any
/// failure stops the start with a value-free message (a container that cannot reach its database restarts, as the Node
/// server does). Not registered during build-time OpenAPI generation, which has no database.
/// </summary>
public sealed partial class StartupService(ForPreparingStorage storage, IEnumerable<ISeedStep> seeds, ILogger<StartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var prepared = await storage.PrepareAsync(cancellationToken).ConfigureAwait(false);
        if (prepared.IsT1)
        {
            throw new InvalidOperationException($"Startup failed while preparing the storage: {prepared.AsT1.Message}");
        }

        foreach (var seed in seeds)
        {
            await seed.RunAsync(cancellationToken).ConfigureAwait(false);
            LogSeedStepDone(logger, seed.Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Debug, Message = "Seed step {Step} done")]
    private static partial void LogSeedStepDone(ILogger logger, string step);
}
