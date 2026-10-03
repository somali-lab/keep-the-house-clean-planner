namespace Huishoudplanner.Architecture.Tests;

/// <summary>
/// The package and framework half of the "one home per driver" rules; see <see cref="BuildRules"/>.
/// Each rule is shown red on csproj text that violates it, then green on the real projects.
/// </summary>
public class BuildRuleTests
{
    public static TheoryData<string> Projects => [.. BuildRules.ProjectNames];

    [Theory]
    [MemberData(nameof(Projects))]
    public void Real_projects_reference_only_what_they_may(string project)
    {
        BuildRules.Violations(project, BuildRules.Load(project)).Should().BeEmpty();
    }

    [Fact]
    public void The_check_reads_the_real_project_files()
    {
        BuildRules.Load("Host").Sdk.Should().Be("Microsoft.NET.Sdk.Web");
        BuildRules.Load("Domain").Packages.Should().Contain("OneOf");
    }

    public static TheoryData<string, string, string> Violating => new()
    {
        { "Application", "MongoDB.Driver", "<PackageReference Include=\"MongoDB.Driver\" />" },
        { "Adapters.Http", "MongoDB.Bson", "<PackageReference Include=\"MongoDB.Bson\" />" },
        { "Adapters.Mongo", "QuestPDF", "<PackageReference Include=\"QuestPDF\" />" },
        { "Adapters.Notify", "Microsoft.AspNetCore.App", "<FrameworkReference Include=\"Microsoft.AspNetCore.App\" />" },
        { "Adapters.Jobs", "Microsoft.AspNetCore.OpenApi", "<PackageReference Include=\"Microsoft.AspNetCore.OpenApi\" />" },
        { "Adapters.Pdf", "Microsoft.Extensions.AI.Abstractions", "<PackageReference Include=\"Microsoft.Extensions.AI.Abstractions\" />" },
        { "Adapters.Http", "Anthropic", "<PackageReference Include=\"Anthropic\" />" },
        { "Application", "OpenAI", "<PackageReference Include=\"OpenAI\" />" },
        { "Adapters.Notify", "OllamaSharp", "<PackageReference Include=\"OllamaSharp\" />" },
        { "Domain", "Newtonsoft.Json", "<PackageReference Include=\"Newtonsoft.Json\" />" },
    };

    [Theory]
    [MemberData(nameof(Violating))]
    public void A_project_with_a_forbidden_reference_is_flagged(string project, string reference, string element)
    {
        var csproj = $"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>{element}</ItemGroup></Project>";

        BuildRules.Violations(project, BuildRules.ParseCsproj(csproj))
            .Should().Contain(v => v.Contains(reference, StringComparison.Ordinal));
    }

    [Fact]
    public void A_non_host_project_on_the_web_sdk_is_flagged()
    {
        var csproj = "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />";

        BuildRules.Violations("Adapters.Jobs", BuildRules.ParseCsproj(csproj)).Should().NotBeEmpty();
        BuildRules.Violations("Adapters.Http", BuildRules.ParseCsproj(csproj)).Should().BeEmpty();
    }

    [Fact]
    public void A_dependency_that_only_restore_knows_about_is_flagged()
    {
        const string assets = """{"project":{"frameworks":{"net10.0":{"dependencies":{"MongoDB.Driver":{"target":"Package","version":"[3.0.0, )"}},"frameworkReferences":{"Microsoft.AspNetCore.App":{"privateAssets":"none"}}}}}}""";
        var facts = BuildRules.Merge(BuildRules.ParseCsproj("<Project Sdk=\"Microsoft.NET.Sdk\" />"), assets);

        BuildRules.Violations("Application", facts).Should().HaveCount(2);
    }

    [Fact]
    public void The_allowed_homes_are_not_flagged()
    {
        const string head = "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>";
        const string tail = "</ItemGroup></Project>";

        BuildRules.Violations("Adapters.Mongo", BuildRules.ParseCsproj(head + "<PackageReference Include=\"MongoDB.Driver\" />" + tail)).Should().BeEmpty();
        BuildRules.Violations("Adapters.Ai", BuildRules.ParseCsproj(head + "<PackageReference Include=\"Microsoft.Extensions.AI\" />" + tail)).Should().BeEmpty();
        foreach (var package in new[] { "Anthropic", "OpenAI", "OllamaSharp" })
        {
            BuildRules.Violations("Adapters.Ai", BuildRules.ParseCsproj(head + $"<PackageReference Include=\"{package}\" />" + tail)).Should().BeEmpty();
        }
        BuildRules.Violations("Adapters.Pdf", BuildRules.ParseCsproj(head + "<PackageReference Include=\"QuestPDF\" />" + tail)).Should().BeEmpty();
    }

    public static TheoryData<string, string> ViolatingProjectReferences => new()
    {
        { "Domain", "Application" },
        { "Application", "Adapters.Mongo" },
        { "Adapters.Mongo", "Adapters.Pdf" },
        { "Adapters.Http", "Adapters.Mongo" },
        { "Adapters.Jobs", "Adapters.Notify" },
        { "Adapters.Ai", "Application" },
        { "Adapters.Pdf", "Host" },
        { "Application", "Host" },
    };

    [Theory]
    [MemberData(nameof(ViolatingProjectReferences))]
    public void A_project_reference_outside_the_allowed_graph_is_flagged(string project, string target)
    {
        var csproj = $"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"..\\Huishoudplanner.{target}\\Huishoudplanner.{target}.csproj\" /></ItemGroup></Project>";

        BuildRules.Violations(project, BuildRules.ParseCsproj(csproj))
            .Should().Contain(v => v.Contains($"references project {target};", StringComparison.Ordinal));
    }

    [Fact]
    public void The_allowed_project_references_are_not_flagged()
    {
        static string Csproj(params string[] targets) =>
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
            + string.Concat(targets.Select(t => $"<ProjectReference Include=\"..\\Huishoudplanner.{t}\\Huishoudplanner.{t}.csproj\" />"))
            + "</ItemGroup></Project>";

        BuildRules.Violations("Application", BuildRules.ParseCsproj(Csproj("Domain"))).Should().BeEmpty();
        BuildRules.Violations("Adapters.Http", BuildRules.ParseCsproj(Csproj("Domain", "Application"))).Should().BeEmpty();
        BuildRules.Violations("Host", BuildRules.ParseCsproj(Csproj("Domain", "Application", "Adapters.Mongo", "Adapters.Http", "Adapters.Ai", "Adapters.Notify", "Adapters.Pdf", "Adapters.Jobs"))).Should().BeEmpty();
    }
}
