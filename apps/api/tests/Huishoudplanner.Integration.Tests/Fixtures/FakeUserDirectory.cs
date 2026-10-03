using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using OneOf;

namespace Huishoudplanner.Integration.Tests.Fixtures;

/// <summary>An in-memory <see cref="ForFindingUsers"/>. Ids are matched ignoring case, like ObjectId parsing does.</summary>
public sealed class FakeUserDirectory : ForFindingUsers
{
    private readonly Dictionary<string, UserIdentity> users = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> lookups = [];

    /// <summary>When set, every lookup fails with this port error.</summary>
    public PortError? Failure { get; set; }

    /// <summary>The ids that were looked up, in order.</summary>
    public IReadOnlyList<string> Lookups => lookups;

    public UserIdentity Add(Role role, bool active = true)
    {
        var user = new UserIdentity(ObjectId.GenerateNewId().ToString(), role, active);
        users[user.Id] = user;
        return user;
    }

    public Task<OneOf<UserIdentity, NotFound, PortError>> FindAsync(string userId, CancellationToken cancellationToken)
    {
        lookups.Add(userId);
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<UserIdentity, NotFound, PortError>>(failure);
        }

        return Task.FromResult<OneOf<UserIdentity, NotFound, PortError>>(
            users.TryGetValue(userId, out var user) ? user : new NotFound());
    }
}
