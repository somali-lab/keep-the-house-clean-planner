using Huishoudplanner.Application.Users;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Users;
using Huishoudplanner.Host.Configuration;
using Huishoudplanner.Host.Startup;
using Microsoft.Extensions.Options;

namespace Huishoudplanner.Host.Users;

public static class UserComposition
{
    /// <summary>The users use cases and the seed step for <c>SEED_USERS</c>. The store comes from the Mongo adapter registration.</summary>
    public static IServiceCollection AddUsers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IUserService, UserService>();
        services.AddSingleton<IUserSeedService, UserSeedService>();
        services.AddSingleton<ISeedStep, UserSeedStep>();
        return services;
    }
}

/// <summary>Seeds the configured profiles on an empty users collection and logs how many were created (never their names).</summary>
internal sealed partial class UserSeedStep(IUserSeedService seed, IOptions<AppOptions> options, ILogger<UserSeedStep> logger) : ISeedStep
{
    public string Name => "users";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var profiles = options.Value.SeedUsers.Select(u => new SeedProfile(u.Name, u.Color)).ToList();
        var result = await seed.SeedAsync(profiles, cancellationToken).ConfigureAwait(false);
        result.Switch(
            created =>
            {
                if (created > 0)
                {
                    LogSeeded(logger, created);
                }
            },
            conflict => throw new InvalidOperationException($"Startup failed while seeding the users: {conflict.Code}"),
            error => throw new InvalidOperationException($"Startup failed while seeding the users: {error.Message}"));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded {Count} users")]
    private static partial void LogSeeded(ILogger logger, int count);
}
