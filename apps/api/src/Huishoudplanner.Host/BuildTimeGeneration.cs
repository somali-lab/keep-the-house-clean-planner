using System.Reflection;

namespace Huishoudplanner.Host;

/// <summary>
/// Microsoft.Extensions.ApiDescription.Server generates the OpenAPI document at build time by running this entry point
/// inside its tool (<c>GetDocument.Insider</c>), which starts the host with a stub server. No configuration and no
/// external service exists then, so startup validation of configuration and everything that talks to a service must
/// be skipped: the document only needs the endpoint metadata.
/// </summary>
public static class BuildTimeGeneration
{
    private const string ToolAssemblyName = "GetDocument.Insider";

    public static bool IsRunning => IsRunningUnder(Assembly.GetEntryAssembly()?.GetName().Name);

    public static bool IsRunningUnder(string? entryAssemblyName) =>
        string.Equals(entryAssemblyName, ToolAssemblyName, StringComparison.Ordinal);
}
