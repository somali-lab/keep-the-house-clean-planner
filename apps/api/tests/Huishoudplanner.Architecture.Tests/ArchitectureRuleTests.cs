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
    private sealed record RuleCase(Func<Layout, IArchRule> Rule, string[] Offenders);

    private static readonly Dictionary<string, RuleCase> Cases = BuildCases();

    public static TheoryData<string> RuleNames => [.. Cases.Keys];

    private static Dictionary<string, RuleCase> BuildCases()
    {
        var cases = new Dictionary<string, RuleCase>
        {
            ["Domain depends on nothing of ours"] = new(ArchitectureRules.DomainDependsOnNothingOfOurs,
                ["DomainReachesApplication", "DomainReachesAdapter", "DomainReachesHost"]),
            ["Domain uses only the BCL and OneOf"] = new(ArchitectureRules.DomainUsesOnlyTheBclAndOneOf,
                ["DomainUsesMongo"]),
            ["Application depends only on Domain"] = new(ArchitectureRules.ApplicationDependsOnlyOnDomain,
                ["ApplicationReachesAdapter", "ApplicationReachesHost"]),
            ["Nothing depends on the Host"] = new(ArchitectureRules.NothingDependsOnTheHost,
                ["DomainReachesHost", "ApplicationReachesHost", "HttpReachesHost"]),
            ["MongoDB types only in Adapters.Mongo"] = new(ArchitectureRules.MongoDriverOnlyInMongoAdapter,
                ["DomainUsesMongo", "ApplicationWritesToMongo", "HttpWritesToMongo"]),
            ["Mongo writes only in Adapters.Mongo"] = new(ArchitectureRules.MongoWritesOnlyInMongoAdapter,
                ["ApplicationWritesToMongo", "HttpWritesToMongo"]),
            ["Domain and Application do not know Mongo"] = new(ArchitectureRules.DomainAndApplicationDoNotKnowMongo,
                ["DomainUsesMongo", "ApplicationWritesToMongo"]),
            ["QuestPDF only in Adapters.Pdf"] = new(ArchitectureRules.QuestPdfOnlyInPdfAdapter,
                ["HttpUsesQuestPdf"]),
            ["ASP.NET Core only in Adapters.Http and Host"] = new(ArchitectureRules.AspNetCoreOnlyInHttpAdapterAndHost,
                ["MongoUsesAspNet"]),
            ["Microsoft.Extensions.AI only in Adapters.Ai"] = new(ArchitectureRules.ExtensionsAiOnlyInAiAdapter,
                ["NotifyUsesAi"]),
            ["Wall clock only in the clock adapter"] = new(ArchitectureRules.WallClockOnlyInClockAdapter,
                ["DomainReadsClock", "Scheduler", "UnwelcomeClock"]),
            ["Driven ports are interfaces named For*"] = new(ArchitectureRules.DrivenPortsAreNamedFor,
                ["IStoringThings", "ForBeingAClass"]),
            ["For* interfaces live in Domain.Ports.Driven"] = new(ArchitectureRules.ForInterfacesLiveInDrivenPorts,
                ["ForMisplacedThings"]),
            ["Driving ports are interfaces named I*Service"] = new(ArchitectureRules.DrivingPortsAreNamedService,
                ["ThingManager", "IThingService"]),
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
                new(l => ArchitectureRules.AdapterDoesNotDependOnOtherAdapters(l, name), adapterOffenders[name]);
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(RuleNames))]
    public void Production_assemblies_follow_the_rule(string name)
    {
        Cases[name].Rule(Layout.Production).Check(Architectures.Production);
    }

    [Theory]
    [MemberData(nameof(RuleNames))]
    public void A_conforming_layout_follows_the_rule(string name)
    {
        Cases[name].Rule(Layout.Good).Check(Architectures.Fixtures);
    }

    [Theory]
    [MemberData(nameof(RuleNames))]
    public void A_violating_layout_breaks_the_rule_and_names_every_offender(string name)
    {
        var rule = Cases[name].Rule(Layout.Bad);

        var failures = rule.Evaluate(Architectures.Fixtures).Where(r => !r.Passed).ToList();

        failures.Should().NotBeEmpty("the rule must catch the fixture violations");
        var offenders = failures.Select(f => ((IHasName)f.EvaluatedObject).FullName).ToList();
        foreach (var expected in Cases[name].Offenders)
        {
            offenders.Should().Contain(o => o.EndsWith("." + expected, StringComparison.Ordinal),
                $"{expected} breaks this rule");
        }

        offenders.Should().OnlyContain(o => o.Contains(".Bad.", StringComparison.Ordinal),
            "only the Bad layout may be flagged");
    }

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

        Types().That().ResideInNamespaceMatching(layout.DrivenPorts).GetObjects(Architectures.Fixtures)
            .Should().NotBeEmpty();
        Types().That().ResideInNamespaceMatching(layout.DrivingPorts).GetObjects(Architectures.Fixtures)
            .Should().NotBeEmpty();
        ArchitectureRules.ClockAdapter(layout).GetObjects(Architectures.Fixtures).Should().NotBeEmpty();
    }

    [Fact]
    public void The_production_solution_includes_the_hosts_global_namespace_program()
    {
        // Program has no namespace; the rules must still see it, or the Host could use any driver unchecked.
        ArchitectureRules.Ours(Layout.Production).GetObjects(Architectures.Production)
            .Should().Contain(t => t.Name == "Program");
    }
}
