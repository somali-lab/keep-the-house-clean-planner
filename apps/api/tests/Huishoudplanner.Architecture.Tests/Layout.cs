using System.Reflection;
using System.Text.RegularExpressions;

namespace Huishoudplanner.Architecture.Tests;

/// <summary>
/// The namespace layout of plan section 3.1 under a root namespace. The production rules run on
/// <see cref="Production"/>; the same rules run on the fixture roots to prove they can fail.
/// </summary>
internal sealed record Layout(string Root, string HostAssemblyPattern, Assembly[] Assemblies)
{
    public static readonly string[] AdapterNames = ["Mongo", "Http", "Ai", "Notify", "Pdf", "Jobs"];

    /// <summary>The real solution. The Host assembly also holds Program in the global namespace.</summary>
    public static Layout Production { get; } = new("Huishoudplanner", @"^Huishoudplanner\.Host(,.*)?$", Architectures.ProductionAssemblies);

    /// <summary>Deliberately violating types, one per rule.</summary>
    public static Layout Bad { get; } = new("Huishoudplanner.Fixtures.Bad", "^$", Architectures.FixtureAssemblies);

    /// <summary>A small conforming layout that touches every ring.</summary>
    public static Layout Good { get; } = new("Huishoudplanner.Fixtures.Good", "^$", Architectures.FixtureAssemblies);

    private static string Within(string ns) => $@"^{Regex.Escape(ns)}(\..*)?$";

    public string Everything => Within(Root);

    public string Domain => Within($"{Root}.Domain");

    public string Application => Within($"{Root}.Application");

    public string Host => Within($"{Root}.Host");

    public string HostNamespaceName => $"{Root}.Host";

    /// <summary>The exact clock adapter: the one type allowed to read the wall clock.</summary>
    public string ClockAdapterTypeName => $"{Root}.Host.SystemClock";

    public string Adapters => Within($"{Root}.Adapters");

    public string Adapter(string name) => Within($"{Root}.Adapters.{name}");

    public string DrivenPorts => Within($"{Root}.Domain.Ports.Driven");

    public string DrivingPorts => Within($"{Root}.Domain.Ports.Driving");

    /// <summary>Every adapter namespace except <paramref name="name"/>.</summary>
    public IEnumerable<string> OtherAdapters(string name) =>
        AdapterNames.Where(a => a != name).Select(Adapter);
}
