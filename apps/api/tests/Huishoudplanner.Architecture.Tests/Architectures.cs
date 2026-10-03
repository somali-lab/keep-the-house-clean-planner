using System.Reflection;
using ArchUnitNET.Loader;
using Huishoudplanner.Fixtures.Bad.Domain;

namespace Huishoudplanner.Architecture.Tests;

/// <summary>The loaded architectures, built once (loading is the slow part).</summary>
internal static class Architectures
{
    public static readonly Assembly[] ProductionAssemblies =
        [
            typeof(Huishoudplanner.Domain.AssemblyMarker).Assembly,
            typeof(Huishoudplanner.Application.AssemblyMarker).Assembly,
            typeof(Huishoudplanner.Adapters.Mongo.AssemblyMarker).Assembly,
            typeof(Huishoudplanner.Adapters.Http.AssemblyMarker).Assembly,
            typeof(Huishoudplanner.Adapters.Ai.AssemblyMarker).Assembly,
            typeof(Huishoudplanner.Adapters.Notify.AssemblyMarker).Assembly,
            typeof(Huishoudplanner.Adapters.Pdf.AssemblyMarker).Assembly,
            typeof(Huishoudplanner.Adapters.Jobs.AssemblyMarker).Assembly,
            typeof(Huishoudplanner.Host.AssemblyMarker).Assembly,
        ];

    public static readonly Assembly[] FixtureAssemblies = [typeof(DomainReachesApplication).Assembly];

    /// <summary>The nine shipped assemblies.</summary>
    public static readonly ArchUnitNET.Domain.Architecture Production =
        new ArchLoader().LoadAssemblies(ProductionAssemblies).Build();

    /// <summary>The fixture assembly: Bad and Good roots side by side.</summary>
    public static readonly ArchUnitNET.Domain.Architecture Fixtures =
        new ArchLoader().LoadAssemblies(FixtureAssemblies).Build();
}
