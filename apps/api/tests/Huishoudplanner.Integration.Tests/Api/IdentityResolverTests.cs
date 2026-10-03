using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Integration.Tests.Fixtures;
using OneOf;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>Ports apps/server/test/identity.test.ts at the port level: what the header adapter makes of the two header values.</summary>
public sealed class IdentityResolverTests
{
    private readonly FakeUserDirectory users = new();
    private readonly ProfileHeaderActorResolver resolver;

    public IdentityResolverTests() => resolver = new ProfileHeaderActorResolver(users);

    private Task<OneOf<Actor, ProfileRequired, PortError>> Resolve(string? profileId, string? client = null) =>
        resolver.ResolveAsync(new ActorRequest(profileId, client), TestContext.Current.CancellationToken);

    [Fact]
    public async Task ActiveProfile_withWebClient_resolvesWithSourceUi()
    {
        var user = users.Add(Role.Planner);

        var result = await Resolve(user.Id, "web");

        result.AsT0.Should().Be(new Actor(user.Id, Role.Planner, ActorSource.Ui));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("mobile")]
    [InlineData("WEB")]
    public async Task ActiveProfile_withoutExactWebClient_resolvesWithSourceApi(string? client)
    {
        var user = users.Add(Role.Member);

        var result = await Resolve(user.Id, client);

        result.AsT0.Source.Should().Be(ActorSource.Api);
    }

    [Fact]
    public async Task UppercaseHexId_isAccepted_andTheActorCarriesTheCanonicalId()
    {
        var user = users.Add(Role.Admin);

        var result = await Resolve(user.Id.ToUpperInvariant());

        result.AsT0.ActorId.Should().Be(user.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("0123456789abcdef0123456")]
    [InlineData("0123456789abcdef012345678")]
    [InlineData("0123456789abcdef0123456g")]
    [InlineData("0123456789abcdef01234567\n")]
    [InlineData("0123456789abcdef01234567, 0123456789abcdef01234568")]
    public async Task MissingOrMalformedId_isProfileRequired_withoutLookingAnyoneUp(string? profileId)
    {
        var result = await Resolve(profileId);

        result.IsT1.Should().BeTrue();
        users.Lookups.Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownUser_isProfileRequired()
    {
        var result = await Resolve("0123456789abcdef01234567");

        result.IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task InactiveUser_isProfileRequired()
    {
        var inactive = users.Add(Role.Admin, active: false);

        var result = await Resolve(inactive.Id);

        result.IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task FailingLookup_isAPortError_notAnException()
    {
        users.Failure = new PortError("database down");

        var result = await Resolve("0123456789abcdef01234567");

        result.AsT2.Message.Should().Be("database down");
    }
}
