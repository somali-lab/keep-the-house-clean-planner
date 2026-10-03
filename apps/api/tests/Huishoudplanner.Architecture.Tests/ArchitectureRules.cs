using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Huishoudplanner.Architecture.Tests;

/// <summary>
/// The architecture rules of plan section 3.1, written once against a <see cref="Layout"/> so the same
/// rule object judges the production assemblies (must pass) and the violating fixtures (must fail).
/// Project references already make a wrong direction a compile error; these rules guard what
/// references cannot: third-party types, naming, the clock, and writes.
/// </summary>
internal static class ArchitectureRules
{
    // External namespaces that each have exactly one place to live.
    private const string MongoDb = @"^MongoDB(\..*)?$";
    private const string QuestPdf = @"^QuestPDF(\..*)?$";
    private const string AspNetCore = @"^Microsoft\.AspNetCore(\..*)?$";
    private const string ExtensionsAi = @"^Microsoft\.Extensions\.AI(\..*)?$";

    private static string Any(params string[] patterns) => string.Join("|", patterns);

    /// <summary>Every type of ours: the namespace tree plus the Host assembly (its Program has no namespace).</summary>
    public static IObjectProvider<IType> Ours(Layout l) =>
        Types().That().ResideInNamespaceMatching(l.Everything).Or().ResideInAssemblyMatching(l.HostAssemblyPattern)
            .As($"types of {l.Root}");

    public static IObjectProvider<IType> InNamespace(string regex, string description) =>
        Types().That().ResideInNamespaceMatching(regex).As(description);

    public static IObjectProvider<IType> HostTypes(Layout l) =>
        Types().That().ResideInNamespaceMatching(l.Host).Or().ResideInAssemblyMatching(l.HostAssemblyPattern)
            .As("Host types");

    // -- Dependency direction between the rings -------------------------------------------------

    /// <summary>Domain depends on nothing of ours except Domain.</summary>
    public static IArchRule DomainDependsOnNothingOfOurs(Layout l) =>
        Types().That().ResideInNamespaceMatching(l.Domain)
            .Should().NotDependOnAny(InNamespace(Any(l.Application, l.Adapters, l.Host), "Application, adapters and Host"))
            .Because("the domain depends on nothing of ours (ports are interfaces it owns)");

    /// <summary>Domain only uses the BCL and OneOf (plan: "no package references except OneOf").</summary>
    public static IArchRule DomainUsesOnlyTheBclAndOneOf(Layout l) =>
        Types().That().ResideInNamespaceMatching(l.Domain)
            .Should().NotDependOnAnyTypesThat()
            .DoNotResideInNamespaceMatching(Any(l.Domain, @"^System(\..*)?$", @"^OneOf(\..*)?$", "^$"))
            .Because("the domain has no package references except OneOf");

    /// <summary>Application depends on Domain only.</summary>
    public static IArchRule ApplicationDependsOnlyOnDomain(Layout l) =>
        Types().That().ResideInNamespaceMatching(l.Application)
            .Should().NotDependOnAny(InNamespace(Any(l.Adapters, l.Host), "adapters and Host"))
            .Because("use cases depend on the domain and its ports only");

    /// <summary>One adapter never depends on another adapter.</summary>
    public static IArchRule AdapterDoesNotDependOnOtherAdapters(Layout l, string adapter) =>
        Types().That().ResideInNamespaceMatching(l.Adapter(adapter))
            .Should().NotDependOnAny(InNamespace(Any([.. l.OtherAdapters(adapter)]), $"adapters other than {adapter}"))
            .Because("adapters meet only through the ports in the domain");

    /// <summary>Only the Host composes, so nothing depends on the Host.</summary>
    public static IArchRule NothingDependsOnTheHost(Layout l) =>
        Types().That().ResideInNamespaceMatching(Any(l.Domain, l.Application, l.Adapters))
            .Should().NotDependOnAny(InNamespace(l.Host, "Host types"))
            .Because("the Host is the only composition root");

    // -- A driver or framework has exactly one place to live ------------------------------------

    /// <summary>MongoDB.Driver (and BSON) types only inside Adapters.Mongo.</summary>
    public static IArchRule MongoDriverOnlyInMongoAdapter(Layout l) =>
        Types().That().Are(Ours(l)).And().AreNot(InNamespace(l.Adapter("Mongo"), "the Mongo adapter"))
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(MongoDb)
            .Because("MongoDB.Driver types live only in Adapters.Mongo");

    /// <summary>Domain and Application have no reference to MongoDB at all.</summary>
    public static IArchRule DomainAndApplicationDoNotKnowMongo(Layout l) =>
        Types().That().ResideInNamespaceMatching(Any(l.Domain, l.Application))
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(MongoDb)
            .Because("the domain and use cases are persistence-agnostic");

    /// <summary>QuestPDF only inside Adapters.Pdf.</summary>
    public static IArchRule QuestPdfOnlyInPdfAdapter(Layout l) =>
        Types().That().Are(Ours(l)).And().AreNot(InNamespace(l.Adapter("Pdf"), "the Pdf adapter"))
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(QuestPdf)
            .Because("QuestPDF lives only in Adapters.Pdf");

    /// <summary>Microsoft.AspNetCore.* only inside Adapters.Http and Host.</summary>
    public static IArchRule AspNetCoreOnlyInHttpAdapterAndHost(Layout l) =>
        Types().That().Are(Ours(l))
            .And().AreNot(InNamespace(l.Adapter("Http"), "the Http adapter"))
            .And().AreNot(HostTypes(l))
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(AspNetCore)
            .Because("ASP.NET Core lives only in Adapters.Http and Host");

    /// <summary>Microsoft.Extensions.AI only inside Adapters.Ai.</summary>
    public static IArchRule ExtensionsAiOnlyInAiAdapter(Layout l) =>
        Types().That().Are(Ours(l)).And().AreNot(InNamespace(l.Adapter("Ai"), "the Ai adapter"))
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(ExtensionsAi)
            .Because("Microsoft.Extensions.AI lives only in Adapters.Ai");

    // -- Ports ----------------------------------------------------------------------------------

    /// <summary>Interfaces in Domain.Ports.Driven are named For*; commands, DTOs and error records may sit beside them.</summary>
    public static IArchRule DrivenPortsAreNamedFor(Layout l) =>
        Interfaces().That().ResideInNamespaceMatching(l.DrivenPorts)
            .Should().HaveNameMatching(@"^For[A-Z]")
            .Because("driven ports are interfaces named ForXxx")
            .WithoutRequiringPositiveResults();

    /// <summary>Any For* interface is a driven port and so lives in Domain.Ports.Driven.</summary>
    public static IArchRule ForInterfacesLiveInDrivenPorts(Layout l) =>
        Interfaces().That().Are(Ours(l)).And().HaveNameMatching(@"^For[A-Z]")
            .Should().ResideInNamespaceMatching(l.DrivenPorts)
            .Because("a ForXxx interface is a driven port")
            .WithoutRequiringPositiveResults();

    /// <summary>Interfaces in Domain.Ports.Driving are named I*Service; commands, DTOs and error records may sit beside them.</summary>
    public static IArchRule DrivingPortsAreNamedService(Layout l) =>
        Interfaces().That().ResideInNamespaceMatching(l.DrivingPorts)
            .Should().HaveNameMatching(@"^I[A-Z]\w*Service$")
            .Because("driving ports are interfaces named IXxxService")
            .WithoutRequiringPositiveResults();

    // -- Assemblies keep to their own namespace root ----------------------------------------------

    /// <summary>
    /// Rings are judged by namespace, so an assembly must not host types of another ring. Every type in the
    /// assembly (within <paramref name="scope"/>) must live under <paramref name="ringNamespace"/>; the Host assembly's global-namespace Program and
    /// compiler-emitted attributes are exempt.
    /// </summary>
    public static IArchRule AssemblyKeepsToItsNamespaceRoot(string assemblyPattern, string ringNamespace, string scope, string hostAssemblyPattern) =>
        Types().That().ResideInAssemblyMatching(assemblyPattern)
            .And().ResideInNamespaceMatching(scope)
            .And().DoNotResideInNamespaceMatching(@"^(System|Microsoft\.CodeAnalysis)(\..*)?$")
            .And().AreNot(Types().That().HaveFullName("Program").And().ResideInAssemblyMatching(hostAssemblyPattern)
                .As("Program in the Host assembly"))
            .Should().ResideInNamespaceMatching(ringNamespace)
            .Because("an assembly holds only types under its own root namespace");
}
