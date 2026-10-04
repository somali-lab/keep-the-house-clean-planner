using System.Net;
using System.Text.RegularExpressions;

namespace Huishoudplanner.Integration.Tests.Fixtures;

/// <summary>
/// The precondition of every entity write (ADR-0022) in the HTTP tests that are about something else: a PATCH, PUT or DELETE of a versioned
/// entity that carries no <c>If-Match</c> first reads the entity and sends the ETag it got, like a client that read before it wrote.
/// A test about the precondition itself sends its own <c>If-Match</c> or opts out with <see cref="OptOutHeader"/> (the header is removed
/// before the request reaches the host). A target that cannot be read (unknown or malformed id) gets the ETag of version 0, so the request
/// reaches the use case and gets the answer it would get with a fresh ETag (404, 400).
/// </summary>
public sealed partial class AutoIfMatchHandler : DelegatingHandler
{
    /// <summary>Set on a request that must reach the host without the automatic <c>If-Match</c> (for example a 428 test).</summary>
    public const string OptOutHeader = "X-Test-No-If-Match";

    [GeneratedRegex(@"^/api/v2/(?<resource>tasks|rooms|users|badges|cycle-plans)/(?<id>[^/]+)(?:/(?:browser-notifications|slots))?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionedEntityPath();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Headers.Remove(OptOutHeader) || request.Headers.Contains("If-Match") || !IsEntityWrite(request.Method) || request.RequestUri is null)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var path = request.RequestUri.AbsolutePath;
        string? readPath = null;
        if (VersionedEntityPath().Match(path) is { Success: true } match)
        {
            readPath = $"/api/v2/{match.Groups["resource"].Value}/{match.Groups["id"].Value}";
        }
        else if (path == "/api/v2/settings")
        {
            readPath = path;
        }

        if (readPath is null)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        using var read = new HttpRequestMessage(HttpMethod.Get, new Uri(request.RequestUri, readPath));
        foreach (var name in new[] { "X-Profile-Id", "X-Client" })
        {
            if (request.Headers.TryGetValues(name, out var values))
            {
                read.Headers.TryAddWithoutValidation(name, values);
            }
        }

        using var response = await base.SendAsync(read, cancellationToken);
        var etag = response.StatusCode == HttpStatusCode.OK && response.Headers.ETag is { } tag ? tag.ToString() : "\"0\"";
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await base.SendAsync(request, cancellationToken);
    }

    private static bool IsEntityWrite(HttpMethod method) => method == HttpMethod.Patch || method == HttpMethod.Put || method == HttpMethod.Delete;
}
