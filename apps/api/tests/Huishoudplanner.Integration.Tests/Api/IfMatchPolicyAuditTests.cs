using System.Text.Json.Nodes;
using Huishoudplanner.Adapters.Http.Concurrency;
using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Structural guard for ADR-0022, next to <see cref="EndpointPolicyAuditTests"/>: no PATCH, PUT or DELETE of API v2 ships without the <c>If-Match</c>
/// requirement, so a future endpoint cannot forget optimistic concurrency. An endpoint that deliberately takes none (a correction of an event, a bulk reset)
/// says so with <c>WithoutIfMatch(reason)</c> and is listed in <see cref="Exempt"/>, so every exemption is a visible, reviewed decision. The audit runs over the
/// endpoint metadata of the real host and over the checked-in OpenAPI document, which is what the generated web client is built from.
/// </summary>
public sealed class IfMatchPolicyAuditTests
{
    /// <summary>The writes that take no If-Match, by "METHOD pattern". An entry needs a reason on the endpoint, and a new entry needs a decision here.</summary>
    private static readonly string[] Exempt =
    [
        "DELETE /api/v2/occurrences/{id}",
        "DELETE /api/v2/points/redemptions/{id}",
        "DELETE /api/v2/audit",
        "DELETE /api/v2/stats",
    ];

    private static readonly string[] EntityWriteMethods = [HttpMethods.Patch, HttpMethods.Put, HttpMethods.Delete];

    private static string Key(RouteEndpoint endpoint, string method) => $"{method} {endpoint.RoutePattern.RawText}";

    private static IEnumerable<(RouteEndpoint Endpoint, string Method)> EntityWrites(IEnumerable<RouteEndpoint> endpoints) =>
        endpoints.SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
            .Where(m => EntityWriteMethods.Contains(m, StringComparer.OrdinalIgnoreCase))
            .Select(m => (Endpoint: e, Method: m.ToUpperInvariant())));

    private static List<string> Violations(IEnumerable<RouteEndpoint> endpoints)
    {
        var violations = new List<string>();
        var list = endpoints.ToList();
        foreach (var (endpoint, method) in EntityWrites(list))
        {
            var key = Key(endpoint, method);
            var requires = endpoint.Metadata.GetMetadata<IfMatchRequiredMetadata>() is not null;
            var exempt = endpoint.Metadata.GetMetadata<NoIfMatchMetadata>();
            if (requires && exempt is not null)
            {
                violations.Add($"{key} both requires If-Match and is exempt from it");
            }
            else if (!requires && exempt is null)
            {
                violations.Add($"{key} neither requires If-Match (RequireIfMatch) nor says why it takes none (WithoutIfMatch)");
            }
            else if (requires && method != HttpMethods.Delete && endpoint.Metadata.GetMetadata<ReturnsETagMetadata>() is null)
            {
                violations.Add($"{key} requires If-Match but does not answer the new ETag (ReturnsETag)");
            }
        }

        // Intent endpoints (POST) never take If-Match: they keep their own idempotency or state rules.
        foreach (var endpoint in list.Where(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Contains(HttpMethods.Post)))
        {
            if (endpoint.Metadata.GetMetadata<IfMatchRequiredMetadata>() is not null)
            {
                violations.Add($"POST {endpoint.RoutePattern.RawText} must not require If-Match");
            }
        }

        return violations;
    }

    private static List<RouteEndpoint> EndpointsOf(ApiFactory factory)
    {
        _ = factory.CreateClient();
        return [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()];
    }

    [Fact]
    public void EveryPatchPutAndDeleteOfTheRealHost_requiresIfMatch_orIsAListedExemption()
    {
        using var factory = ApiFactory.WithoutDatabase();

        var endpoints = EndpointsOf(factory);

        endpoints.Should().NotBeEmpty();
        Violations(endpoints).Should().BeEmpty();
    }

    [Fact]
    public void TheExemptions_areExactlyTheListedOnes_eachWithAReason()
    {
        using var factory = ApiFactory.WithoutDatabase();

        var exempt = EntityWrites(EndpointsOf(factory))
            .Where(w => w.Endpoint.Metadata.GetMetadata<NoIfMatchMetadata>() is not null)
            .ToList();

        exempt.Select(w => Key(w.Endpoint, w.Method)).Should().BeEquivalentTo(Exempt);
        exempt.Select(w => w.Endpoint.Metadata.GetMetadata<NoIfMatchMetadata>()!.Reason).Should().OnlyContain(r => r.Length > 20);
    }

    [Fact]
    public void TheAudit_flagsAnEntityWriteWithoutTheRequirement_andAnIntentThatTakesIt()
    {
        using var factory = ApiFactory.WithoutDatabase().WithEndpoints(routes =>
        {
            routes.MapPatch("/test/forgot/{id}", (string id) => id).RequireAdmin();
            routes.MapPut("/test/forgot-too/{id}", (string id) => id).RequireAdmin();
            routes.MapDelete("/test/forgot-delete/{id}", (string id) => id).RequireAdmin();
            routes.MapPatch("/test/etag-missing/{id}", (string id) => id).RequireAdmin().RequireIfMatch();
            routes.MapPatch("/test/fine/{id}", (string id) => id).RequireAdmin().RequireIfMatch().ReturnsETag();
            routes.MapDelete("/test/fine-delete/{id}", (string id) => id).RequireAdmin().RequireIfMatch();
            routes.MapDelete("/test/exempt/{id}", (string id) => id).RequireAdmin().WithoutIfMatch("A correction that is guarded by its own state.");
            routes.MapDelete("/test/both/{id}", (string id) => id).RequireAdmin().RequireIfMatch().WithoutIfMatch("Both is a mistake.");
            routes.MapPost("/test/intent", () => "x").RequireAdmin().RequireIfMatch();
        });

        var violations = Violations(EndpointsOf(factory).Where(e => e.RoutePattern.RawText?.StartsWith("/test/", StringComparison.Ordinal) == true));

        violations.Should().BeEquivalentTo(
            "PATCH /test/forgot/{id} neither requires If-Match (RequireIfMatch) nor says why it takes none (WithoutIfMatch)",
            "PUT /test/forgot-too/{id} neither requires If-Match (RequireIfMatch) nor says why it takes none (WithoutIfMatch)",
            "DELETE /test/forgot-delete/{id} neither requires If-Match (RequireIfMatch) nor says why it takes none (WithoutIfMatch)",
            "PATCH /test/etag-missing/{id} requires If-Match but does not answer the new ETag (ReturnsETag)",
            "DELETE /test/both/{id} both requires If-Match and is exempt from it",
            "POST /test/intent must not require If-Match");
    }

    // ---- the checked-in OpenAPI document, the source of the generated client

    private static JsonNode CheckedInDocument()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "version.txt")))
        {
            directory = directory.Parent;
        }

        var path = Path.Combine(directory?.FullName ?? throw new InvalidOperationException("The repository root was not found."), "apps", "api", "openapi", "v2.json");
        return JsonNode.Parse(File.ReadAllText(path))!;
    }

    private static IEnumerable<(string Path, string Method, JsonNode Operation)> Operations(JsonNode document) =>
        document["paths"]!.AsObject().SelectMany(p => p.Value!.AsObject()
            .Select(o => (Path: p.Key, Method: o.Key.ToUpperInvariant(), Operation: o.Value!)));

    private static bool HasRequiredIfMatch(JsonNode operation) =>
        operation["parameters"]?.AsArray().Any(p => p?["name"]?.GetValue<string>() == "If-Match" && p["in"]?.GetValue<string>() == "header" && p["required"]?.GetValue<bool>() == true) == true;

    [Fact]
    public void EveryPatchPutAndDeleteOfTheOpenApiDocument_declaresTheRequiredIfMatchHeader_orIsAListedExemption()
    {
        var offenders = Operations(CheckedInDocument())
            .Where(o => EntityWriteMethods.Contains(o.Method, StringComparer.OrdinalIgnoreCase))
            .Where(o => !HasRequiredIfMatch(o.Operation) && !Exempt.Contains($"{o.Method} {o.Path}"))
            .Select(o => $"{o.Method} {o.Path}")
            .ToList();

        offenders.Should().BeEmpty("every entity write of the document declares the required If-Match header (ADR-0022)");
    }

    [Fact]
    public void TheOpenApiDocument_documentsThe412And428AnswersAndTheETagHeader_wherever_IfMatchIsRequired()
    {
        var withIfMatch = Operations(CheckedInDocument()).Where(o => HasRequiredIfMatch(o.Operation)).ToList();

        withIfMatch.Should().NotBeEmpty();
        foreach (var (path, method, operation) in withIfMatch)
        {
            var responses = operation["responses"]!.AsObject().Select(r => r.Key).ToList();
            responses.Should().Contain(["412", "428"], $"{method} {path} answers precondition failures");
            if (method != "DELETE")
            {
                var success = operation["responses"]!.AsObject().First(r => r.Key.StartsWith('2')).Value!;
                success["headers"]?["ETag"].Should().NotBeNull($"{method} {path} answers the new ETag");
            }
        }
    }

    [Fact]
    public void TheOpenApiDocument_intentEndpointsTakeNoIfMatch()
    {
        var offenders = Operations(CheckedInDocument())
            .Where(o => o.Method == "POST" && HasRequiredIfMatch(o.Operation))
            .Select(o => $"{o.Method} {o.Path}");

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void EveryReadOfASingleEntity_answersTheETag()
    {
        var singles = new[]
        {
            "/api/v2/tasks/{id}", "/api/v2/rooms/{id}", "/api/v2/users/{id}", "/api/v2/badges/{id}", "/api/v2/cycle-plans/{id}", "/api/v2/cycle-plans/active", "/api/v2/settings",
        };
        var document = CheckedInDocument();

        foreach (var path in singles)
        {
            var get = document["paths"]![path]!["get"]!;
            get["responses"]!["200"]!["headers"]?["ETag"].Should().NotBeNull($"GET {path} answers the ETag");
        }
    }

    [Fact]
    public void EveryEntityWithAnETagRead_hasAnIfMatchWrite_andTheOtherWayAround()
    {
        var document = CheckedInDocument();

        var read = Operations(document)
            .Where(o => o.Method == "GET" && o.Operation["responses"]!["200"]?["headers"]?["ETag"] is not null)
            .Select(o => o.Path)
            .ToList();
        var writes = Operations(document).Where(o => HasRequiredIfMatch(o.Operation)).Select(o => o.Path).ToList();

        // The slots and the notification moments are written through a sub-resource of the entity that is read.
        string Entity(string path) => path.EndsWith("/slots", StringComparison.Ordinal) ? path[..^"/slots".Length]
            : path.EndsWith("/browser-notifications", StringComparison.Ordinal) ? path[..^"/browser-notifications".Length]
            : path;
        writes.Select(Entity).Distinct().Should().OnlyContain(w => read.Contains(w), "an entity that is written with If-Match can be read with its ETag");
    }
}
