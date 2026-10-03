using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// The role guards of the Node routes for settings (<c>requireAdmin</c> on the patch, nothing on the read), as declared on the endpoints.
/// <see cref="EndpointPolicyAuditTests"/> keys its protected reads by route pattern, which GET and PATCH of settings share, so the
/// two guards are pinned here by method.
/// </summary>
public sealed class SettingsPolicyTests
{
    private static List<string?> PoliciesOf(string method)
    {
        using var factory = ApiFactory.WithoutDatabase();
        _ = factory.CreateClient();
        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/api/v2/settings"
                && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase));
        return [.. endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy)];
    }

    [Fact]
    public void Patch_requiresAnAdministrator() => PoliciesOf(HttpMethods.Patch).Should().Equal(AuthorizationPolicies.AdminPolicy);

    [Fact]
    public void Get_isOpen() => PoliciesOf(HttpMethods.Get).Should().BeEmpty();
}
