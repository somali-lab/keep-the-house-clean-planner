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

    /// <summary>Writing endpoints and the policy each must declare, by "METHOD pattern" (the Node route guards: requireAdmin, requireActor).</summary>
    private static readonly Dictionary<string, string> ProtectedWrites = new()
    {
        ["POST /api/v2/users"] = AuthorizationPolicies.AdminPolicy,
        ["PATCH /api/v2/users/{id}"] = AuthorizationPolicies.AdminPolicy,
        ["PUT /api/v2/users/{id}/browser-notifications"] = AuthorizationPolicies.ActorPolicy,
        ["POST /api/v2/tasks"] = AuthorizationPolicies.PlannerPolicy,
        ["PATCH /api/v2/tasks/{id}"] = AuthorizationPolicies.PlannerPolicy,
        ["POST /api/v2/rooms/{id}/tasks/bulk"] = AuthorizationPolicies.PlannerPolicy,
        ["POST /api/v2/cycle-plans"] = AuthorizationPolicies.PlannerPolicy,
        ["PATCH /api/v2/cycle-plans/{id}"] = AuthorizationPolicies.PlannerPolicy,
        ["DELETE /api/v2/cycle-plans/{id}"] = AuthorizationPolicies.PlannerPolicy,
        ["PUT /api/v2/cycle-plans/{id}/slots"] = AuthorizationPolicies.PlannerPolicy,
        ["POST /api/v2/cycle-plans/{id}/validation"] = AuthorizationPolicies.PlannerPolicy,
        ["POST /api/v2/cycle-plans/validation"] = AuthorizationPolicies.PlannerPolicy,
        ["POST /api/v2/occurrences/{id}/complete"] = AuthorizationPolicies.ActorPolicy,
        ["POST /api/v2/occurrences/{id}/uncomplete"] = AuthorizationPolicies.ActorPolicy,
        ["POST /api/v2/occurrences/{id}/completion"] = AuthorizationPolicies.AdminPolicy,
        ["POST /api/v2/occurrences/{id}/skip"] = AuthorizationPolicies.ActorPolicy,
        ["POST /api/v2/occurrences/{id}/reschedule"] = AuthorizationPolicies.ActorPolicy,
        ["POST /api/v2/occurrences/{id}/assignment"] = AuthorizationPolicies.ActorPolicy,
        ["POST /api/v2/occurrences/{id}/claim"] = AuthorizationPolicies.ActorPolicy,
        ["DELETE /api/v2/occurrences/{id}"] = AuthorizationPolicies.AdminPolicy,

        ["POST /api/v2/cycle-plans/{id}/activation"] = AuthorizationPolicies.PlannerPolicy,
        ["POST /api/v2/ai/test"] = AuthorizationPolicies.PlannerPolicy,
        ["POST /api/v2/ai/propose-plan"] = AuthorizationPolicies.PlannerPolicy,
        ["POST /api/v2/ai/rebalance"] = AuthorizationPolicies.PlannerPolicy,
        ["POST /api/v2/ai/suggest-tasks"] = AuthorizationPolicies.PlannerPolicy,
        ["POST /api/v2/ai/explain"] = AuthorizationPolicies.PlannerPolicy,
        ["POST /api/v2/jobs/generation"] = AuthorizationPolicies.PlannerPolicy,
        ["POST /api/v2/jobs/audit-retention"] = AuthorizationPolicies.PlannerPolicy,
    };

    private static readonly string[] ReadMethods = [HttpMethods.Get, HttpMethods.Head, HttpMethods.Options];

    private static List<string> Violations(IEnumerable<RouteEndpoint> endpoints)
    {
        var violations = new List<string>();
        var list = endpoints.ToList();
        foreach (var (route, required) in ProtectedWrites)
        {
            var found = list.Where(e => $"{string.Join(",", e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])} {e.RoutePattern.RawText}" == route).ToList();
            if (found.Count == 0)
            {
                violations.Add($"{route} is not mapped");
            }
            else if (!found.Any(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == required)))
            {
                violations.Add($"{route} must require {required}");
            }
        }

        foreach (var endpoint in list)
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
    public void TheDueList_staysOpenLikeTheNodeRoute()
    {
        using var factory = ApiFactory.WithoutDatabase();

        var due = EndpointsOf(factory).Single(e => e.RoutePattern.RawText == "/api/v2/due");

        due.Metadata.GetOrderedMetadata<IAuthorizeData>().Should().BeEmpty("routes/due.ts has no guard");
    }

    [Fact]
    public void ThePdfExports_stayOpenLikeTheNodeRoutes()
    {
        using var factory = ApiFactory.WithoutDatabase();

        var exports = EndpointsOf(factory).Where(e => e.RoutePattern.RawText?.StartsWith("/api/v2/export/pdf/", StringComparison.Ordinal) == true).ToList();

        exports.Select(e => e.RoutePattern.RawText).Should().BeEquivalentTo(
            "/api/v2/export/pdf/schedule", "/api/v2/export/pdf/day", "/api/v2/export/pdf/due", "/api/v2/export/pdf/tasks");
        exports.SelectMany(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>()).Should().BeEmpty("routes/export.ts has no guard");
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
