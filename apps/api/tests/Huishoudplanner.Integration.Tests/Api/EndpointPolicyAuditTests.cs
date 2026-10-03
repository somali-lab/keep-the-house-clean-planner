using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Structural guard for ADR-0018: no writing endpoint ships without declaring who may call it, and the reads that
/// need a role keep it. Later slices extend <see cref="AnonymousWrites"/> (rare) and <see cref="ProtectedReads"/>.
/// </summary>
public sealed class EndpointPolicyAuditTests
{
    /// <summary>Non-GET endpoints that are intentionally open, by route pattern. Needs a reason per entry.</summary>
    private static readonly HashSet<string> AnonymousWrites = [];

    /// <summary>GET endpoints that require a role, by route pattern (Node: requirePlanner on the activation preview).</summary>
    private static readonly Dictionary<string, string> ProtectedReads = new()
    {
        ["/api/v2/cycle-plans/{id}/activation-preview"] = AuthorizationPolicies.PlannerPolicy,
    };

    private static readonly string[] ReadMethods = [HttpMethods.Get, HttpMethods.Head, HttpMethods.Options];

    private static List<string> Violations(IEnumerable<RouteEndpoint> endpoints)
    {
        var violations = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var pattern = endpoint.RoutePattern.RawText ?? "?";
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
            var writes = methods.Count == 0 || methods.Any(m => !ReadMethods.Contains(m, StringComparer.OrdinalIgnoreCase));
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;

            if (writes && policies.Count == 0 && !anonymous && !AnonymousWrites.Contains(pattern))
            {
                violations.Add($"{string.Join(",", methods)} {pattern} declares no authorization policy");
            }

            if (ProtectedReads.TryGetValue(pattern, out var required) && !policies.Contains(required))
            {
                violations.Add($"{pattern} must require {required}");
            }
        }

        return violations;
    }

    private static IEnumerable<RouteEndpoint> EndpointsOf(ApiFactory factory)
    {
        _ = factory.CreateClient();
        return factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
    }

    [Fact]
    public void EveryWritingEndpointOfTheRealHost_declaresAPolicy_andProtectedReadsKeepTheirs()
    {
        using var factory = ApiFactory.WithoutDatabase();

        var endpoints = EndpointsOf(factory).ToList();

        endpoints.Should().NotBeEmpty();
        Violations(endpoints).Should().BeEmpty();
    }

    [Fact]
    public void TheAudit_flagsAnUnprotectedWrite_andAnUnprotectedProtectedRead()
    {
        using var factory = ApiFactory.WithoutDatabase().WithEndpoints(routes =>
        {
            routes.MapPost("/test/open-write", () => "x");
            routes.MapDelete("/test/closed-write", () => "x").RequireAdmin();
            routes.MapGet("/api/v2/cycle-plans/{id}/activation-preview", (string id) => id);
        });

        var violations = Violations(EndpointsOf(factory));

        violations.Should().BeEquivalentTo(
            "POST /test/open-write declares no authorization policy",
            "/api/v2/cycle-plans/{id}/activation-preview must require RequirePlanner");
    }
}
