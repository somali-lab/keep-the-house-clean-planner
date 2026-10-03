using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace Huishoudplanner.Adapters.Http.WebApp;

/// <summary>Serves the built web app (<c>WEB_DIST_DIR</c>) with the SPA fallback rule of the Node server.</summary>
public static partial class WebAppExtensions
{
    private const string CacheControlValue = "public, max-age=0";

    /// <summary>
    /// Static files from <paramref name="webDistDir"/>, plus <c>index.html</c> for a <c>GET</c>/<c>HEAD</c> that is not an
    /// <c>/api</c> path and has no file extension. Unknown API routes and missing files stay a 404 Problem Details.
    /// Without a configured (or existing) directory nothing is registered, as the Node server does.
    /// </summary>
    public static WebApplication UseWebApp(this WebApplication app, string? webDistDir)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (string.IsNullOrWhiteSpace(webDistDir))
        {
            return app;
        }

        var root = Path.GetFullPath(webDistDir);
        if (!Directory.Exists(root))
        {
            return app;
        }

        var files = new PhysicalFileProvider(root);
        app.Lifetime.ApplicationStopped.Register(files.Dispose);

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = context => context.Context.Response.Headers[HeaderNames.CacheControl] = CacheControlValue,
        });

        app.MapFallback(context => ServeIndexAsync(context, files))
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get, HttpMethods.Head]))
            .ExcludeFromDescription();
        return app;
    }

    private static async Task ServeIndexAsync(HttpContext context, PhysicalFileProvider files)
    {
        var path = context.Request.Path.Value ?? "/";
        var index = files.GetFileInfo("index.html");
        if (IsApiPath(path) || HasExtension(path) || !index.Exists || index.PhysicalPath is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers[HeaderNames.CacheControl] = CacheControlValue;
        context.Response.Headers[HeaderNames.LastModified] = index.LastModified.ToString("R");
        context.Response.ContentLength = index.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.SendFileAsync(index, context.RequestAborted);
        }
    }

    private static bool IsApiPath(string path) =>
        path.Equals("/api", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);

    private static bool HasExtension(string path) => ExtensionPattern().IsMatch(path);

    [GeneratedRegex(@"\.[a-z0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionPattern();
}
