using System.Text.RegularExpressions;
using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.xUnitV3;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Huishoudplanner.Architecture.Tests;

/// <summary>
/// Every rule is proven three ways: it holds on the production assemblies, it holds with positive results on a
/// small conforming layout (so it is not green by matching nothing), and it fails on deliberately violating
/// fixture types, naming each offender (so it is not green by being blind).
/// </summary>
public class ArchitectureRuleTests
{
    /// <summary>A rule is either an ArchUnitNET rule or an IL rule that returns the names of offending types.</summary>
    private sealed record RuleCase(
        Func<Layout, IArchRule>? Arch,
        Func<Layout, IReadOnlyList<string>>? Il,
        string[] Offenders,
        string[]? Tolerated = null);

    private static RuleCase ArchCase(Func<Layout, IArchRule> rule, string[] offenders, string[]? tolerated = null) =>
        new(rule, null, offenders, tolerated);

    private static RuleCase IlCase(Func<Layout, IReadOnlyList<string>> rule, string[] offenders) =>
        new(null, rule, offenders);

    private static readonly Dictionary<string, RuleCase> Cases = BuildCases();

    public static TheoryData<string> RuleNames => [.. Cases.Keys];

    private static Dictionary<string, RuleCase> BuildCases()
    {
        var cases = new Dictionary<string, RuleCase>
        {
            ["Domain depends on nothing of ours"] = ArchCase(ArchitectureRules.DomainDependsOnNothingOfOurs,
                ["DomainReachesApplication", "DomainReachesAdapter", "DomainReachesHost"]),
            ["Domain uses only the BCL and OneOf"] = ArchCase(ArchitectureRules.DomainUsesOnlyTheBclAndOneOf,
                ["DomainUsesMongo"]),
            ["Application depends only on Domain"] = ArchCase(ArchitectureRules.ApplicationDependsOnlyOnDomain,
                ["ApplicationReachesAdapter", "ApplicationReachesHost"]),
            ["Nothing depends on the Host"] = ArchCase(ArchitectureRules.NothingDependsOnTheHost,
                ["DomainReachesHost", "ApplicationReachesHost", "HttpReachesHost"]),
            ["MongoDB types only in Adapters.Mongo"] = ArchCase(ArchitectureRules.MongoDriverOnlyInMongoAdapter,
                ["DomainUsesMongo", "ApplicationWritesToMongo", "HttpWritesToMongo"]),
            ["Mongo writes only in Adapters.Mongo"] = IlCase(IlRules.MongoWritesOutsideMongoAdapter,
                ["ApplicationWritesToMongo", "ApplicationWritesToMongoInAsyncLambda", "ApplicationAggregatesIntoOut", "HttpWritesToMongo"]),
            ["Domain and Application do not know Mongo"] = ArchCase(ArchitectureRules.DomainAndApplicationDoNotKnowMongo,
                ["DomainUsesMongo", "ApplicationWritesToMongo"]),
            ["QuestPDF only in Adapters.Pdf"] = ArchCase(ArchitectureRules.QuestPdfOnlyInPdfAdapter,
                ["HttpUsesQuestPdf"]),
            ["ASP.NET Core only in Adapters.Http and Host"] = ArchCase(ArchitectureRules.AspNetCoreOnlyInHttpAdapterAndHost,
                ["MongoUsesAspNet"]),
            ["Microsoft.Extensions.AI only in Adapters.Ai"] = ArchCase(ArchitectureRules.ExtensionsAiOnlyInAiAdapter,
                ["NotifyUsesAi"]),
            ["Wall clock only in SystemClock"] = IlCase(IlRules.WallClockOutsideClockAdapter,
                [
                    "DomainReadsClock", "DomainReadsClockInAsyncLambda", "DomainReadsClockInAsyncLocalFunction",
                    "DomainReadsClockInAsyncIterator", "FileLocalClock", "Scheduler", "NotQuiteSystemClock", "UnwelcomeClock",
                ]),
            ["TimeProvider.System only in Host"] = IlCase(IlRules.TimeProviderSystemOutsideHost,
                ["DomainUsesTimeProvider"]),
            ["Driven port interfaces are named For*"] = ArchCase(ArchitectureRules.DrivenPortsAreNamedFor,
                ["IStoringThings"], ["ForBeingAClass", "StoreThingCommand"]),
            ["For* interfaces live in Domain.Ports.Driven"] = ArchCase(ArchitectureRules.ForInterfacesLiveInDrivenPorts,
                ["ForMisplacedThings"]),
            ["Driving port interfaces are named I*Service"] = ArchCase(ArchitectureRules.DrivingPortsAreNamedService,
                ["ThingManager"], ["IThingService", "RenameThingCommand"]),
        };

        var adapterOffenders = new Dictionary<string, string[]>
        {
            ["Mongo"] = ["MongoReachesPdf"],
            ["Http"] = ["HttpReachesAi"],
            ["Ai"] = ["AiReachesHttp"],
            ["Notify"] = ["NotifyReachesMongo"],
            ["Pdf"] = ["PdfReachesJobs"],
            ["Jobs"] = ["JobsReachesNotify"],
        };
        foreach (var adapter in Layout.AdapterNames)
        {
            var name = adapter;
            cases[$"Adapters.{name} does not depend on other adapters"] =
                ArchCase(l => ArchitectureRules.AdapterDoesNotDependOnOtherAdapters(l, name), adapterOffenders[name]);
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(RuleNames))]
    public void Production_assemblies_follow_the_rule(string name)
    {
        var rule = Cases[name];
        if (rule.Arch is not null)
        {
            rule.Arch(Layout.Production).Check(Architectures.Production);
        }
        else
        {
            rule.Il!(Layout.Production).Should().BeEmpty();
        }
    }

    [Theory]
    [MemberData(nameof(RuleNames))]
    public void A_conforming_layout_follows_the_rule(string name)
    {
        var rule = Cases[name];
        if (rule.Arch is not null)
        {
            rule.Arch(Layout.Good).Check(Architectures.Fixtures);
        }
        else
        {
            rule.Il!(Layout.Good).Should().BeEmpty();
        }
    }

    [Theory]
    [MemberData(nameof(RuleNames))]
    public void A_violating_layout_breaks_the_rule_and_names_every_offender(string name)
    {
        var rule = Cases[name];
        var offenders = rule.Arch is not null
            ? rule.Arch(Layout.Bad).Evaluate(Architectures.Fixtures).Where(r => !r.Passed)
                .Select(f => ((IHasName)f.EvaluatedObject).FullName).ToList()
            : [.. rule.Il!(Layout.Bad)];

        offenders.Should().NotBeEmpty("the rule must catch the fixture violations");
        foreach (var expected in rule.Offenders)
        {
            offenders.Should().Contain(o => o.EndsWith("." + expected, StringComparison.Ordinal) || o.EndsWith("__" + expected, StringComparison.Ordinal),
                $"{expected} breaks this rule");
        }

        foreach (var tolerated in rule.Tolerated ?? [])
        {
            offenders.Should().NotContain(o => o.EndsWith("." + tolerated, StringComparison.Ordinal),
                $"{tolerated} is allowed by this rule");
        }

        offenders.Should().OnlyContain(o => o.Contains(".Bad.", StringComparison.Ordinal),
            "only the Bad layout may be flagged");
    }

    // -- Assemblies keep to their own namespace root ----------------------------------------------

    public static TheoryData<string> Assemblies =>
    [
        "Huishoudplanner.Domain",
        "Huishoudplanner.Application",
        "Huishoudplanner.Adapters.Mongo",
        "Huishoudplanner.Adapters.Http",
        "Huishoudplanner.Adapters.Ai",
        "Huishoudplanner.Adapters.Notify",
        "Huishoudplanner.Adapters.Pdf",
        "Huishoudplanner.Adapters.Jobs",
        "Huishoudplanner.Host",
    ];

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void Each_production_assembly_keeps_to_its_own_namespace_root(string assembly)
    {
        ArchitectureRules.AssemblyKeepsToItsNamespaceRoot(
                $@"^{Regex.Escape(assembly)}(,.*)?$",
                $@"^{Regex.Escape(assembly)}(\..*)?$",
                "^.*$",
                Layout.Production.HostAssemblyPattern)
            .Check(Architectures.Production);
    }

    private const string FixtureAssembly = @"^Huishoudplanner\.Architecture\.Tests\.Fixtures(,.*)?$";

    private static List<string> Strays(string scope, string hostAssemblyPattern) =>
        ArchitectureRules.AssemblyKeepsToItsNamespaceRoot(FixtureAssembly, Layout.Bad.Domain, scope, hostAssemblyPattern)
            .Evaluate(Architectures.Fixtures).Where(r => !r.Passed)
            .Select(f => ((IHasName)f.EvaluatedObject).FullName).ToList();

    [Fact]
    public void An_assembly_holding_types_of_another_ring_breaks_the_namespace_root_rule()
    {
        // The fixture assembly stands in for "the Domain assembly": everything of Bad outside Bad.Domain is a stray.
        var offenders = Strays(Layout.Bad.Everything, "^$");

        offenders.Should().Contain(o => o.EndsWith(".StrayType", StringComparison.Ordinal));
        offenders.Should().Contain(o => o.EndsWith(".ApplicationTarget", StringComparison.Ordinal));
        offenders.Should().NotContain(o => o.EndsWith(".DomainReachesApplication", StringComparison.Ordinal));
        offenders.Should().OnlyContain(o => o.Contains(".Bad.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_global_namespace_program_is_exempt_only_in_the_host_assembly()
    {
        var scopeWithGlobalNamespace = @"^(Huishoudplanner\.Fixtures\.Bad(\..*)?)?$";

        Strays(scopeWithGlobalNamespace, "^$").Should().Contain("Program", "outside the Host assembly Program is a stray");
        Strays(scopeWithGlobalNamespace, FixtureAssembly).Should().NotContain("Program", "inside the Host assembly Program is exempt");
    }

    // -- Non-vacuity ------------------------------------------------------------------------------

    [Fact]
    public void Every_ring_of_the_production_solution_is_present_so_ring_rules_are_never_vacuous()
    {
        var layout = Layout.Production;
        var rings = new[] { layout.Domain, layout.Application, layout.Host }
            .Concat(Layout.AdapterNames.Select(layout.Adapter));

        foreach (var ring in rings)
        {
            Types().That().ResideInNamespaceMatching(ring).GetObjects(Architectures.Production)
                .Should().NotBeEmpty($"{ring} must hold at least its assembly marker");
        }
    }

    [Fact]
    public void The_conforming_layout_has_ports_so_the_naming_rules_are_not_vacuous()
    {
        var layout = Layout.Good;

        Interfaces().That().ResideInNamespaceMatching(layout.DrivenPorts).GetObjects(Architectures.Fixtures)
            .Should().NotBeEmpty();
        Interfaces().That().ResideInNamespaceMatching(layout.DrivingPorts).GetObjects(Architectures.Fixtures)
            .Should().NotBeEmpty();
    }

    [Fact]
    public void The_il_scan_sees_the_clock_adapter_and_calls_inside_generated_types()
    {
        var good = IlRules.CallsOf(Layout.Good.Assemblies);
        good.Should().Contain(c => c.Caller == Layout.Good.ClockAdapterTypeName && c.Method == "get_UtcNow",
            "the Good clock adapter reads the wall clock, so the clock rule is not green by seeing nothing");

        var bad = IlRules.CallsOf(Layout.Bad.Assemblies);
        bad.Should().Contain(c => c.Caller.EndsWith(".DomainReadsClockInAsyncLambda", StringComparison.Ordinal)
                                  && c.Method == "get_UtcNow",
            "calls inside compiler-generated closure and state-machine types are attributed to the declaring type");
    }

    [Fact]
    public void The_production_solution_includes_the_hosts_global_namespace_program()
    {
        // Program has no namespace; the rules must still see it, or the Host could use any driver unchecked.
        ArchitectureRules.Ours(Layout.Production).GetObjects(Architectures.Production)
            .Should().Contain(t => t.Name == "Program");
    }
}
