using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Huishoudplanner.Architecture.Tests;

/// <summary>
/// Build-level half of the "one home per driver" rules. ArchUnitNET sees namespaces, not the package or
/// framework a type came from, so ASP.NET Core types outside <c>Microsoft.AspNetCore.*</c> (for example
/// <c>AddRouting()</c> in <c>Microsoft.Extensions.DependencyInjection</c>) or <c>Microsoft.Extensions.AI</c>
/// helpers could slip past. What the project may reference is decided here instead: every package,
/// framework reference and SDK of each src project is checked against an allow-list per project.
/// </summary>
internal static class BuildRules
{
    internal sealed record ProjectFacts(string Sdk, IReadOnlyList<string> Packages, IReadOnlyList<string> FrameworkReferences, IReadOnlyList<string> ProjectReferences);

    private sealed record Restriction(string Description, string Pattern, string[] AllowedProjects);

    private static readonly Restriction[] Restrictions =
    [
        new("MongoDB packages", @"^MongoDB(\..*)?$", ["Adapters.Mongo"]),
        new("QuestPDF", @"^QuestPDF(\..*)?$", ["Adapters.Pdf"]),
        new("ASP.NET Core packages, framework reference and Web SDK", @"^(Microsoft\.AspNetCore(\..*)?|Microsoft\.NET\.Sdk\.Web)$", ["Adapters.Http", "Host"]),
        new("Microsoft.Extensions.AI packages", @"^Microsoft\.Extensions\.AI(\..*)?$", ["Adapters.Ai"]),
    ];

    /// <summary>Host (the composition root) is absent on purpose: it may reference everything.</summary>
    private static readonly Dictionary<string, string[]> AllowedProjectReferences = new()
    {
        ["Domain"] = [],
        ["Application"] = ["Domain"],
        ["Adapters.Mongo"] = ["Domain"],
        ["Adapters.Http"] = ["Domain", "Application"],
        ["Adapters.Jobs"] = ["Domain", "Application"],
        ["Adapters.Ai"] = ["Domain"],
        ["Adapters.Notify"] = ["Domain"],
        ["Adapters.Pdf"] = ["Domain"],
    };

    /// <summary>Reads the SDK, package references and framework references of a csproj.</summary>
    public static ProjectFacts ParseCsproj(string csproj)
    {
        var xml = XDocument.Parse(csproj);
        var root = xml.Root!;
        var sdk = (string?)root.Attribute("Sdk") ?? "Microsoft.NET.Sdk";
        var packages = root.Descendants("PackageReference").Select(e => (string?)e.Attribute("Include") ?? "").Where(n => n.Length > 0);
        var frameworks = root.Descendants("FrameworkReference").Select(e => (string?)e.Attribute("Include") ?? "").Where(n => n.Length > 0);
        var projects = root.Descendants("ProjectReference")
            .Select(e => ((string?)e.Attribute("Include") ?? "").Replace('\\', '/'))
            .Where(n => n.Length > 0)
            .Select(n => Regex.Replace(Path.GetFileNameWithoutExtension(n), @"^Huishoudplanner\.", ""));
        return new ProjectFacts(sdk, [.. packages], [.. frameworks], [.. projects]);
    }

    /// <summary>Adds the direct dependencies restore resolved (catches references injected by props files).</summary>
    public static ProjectFacts Merge(ProjectFacts facts, string projectAssetsJson)
    {
        using var json = JsonDocument.Parse(projectAssetsJson);
        var packages = new List<string>(facts.Packages);
        var frameworks = new List<string>(facts.FrameworkReferences);
        if (json.RootElement.TryGetProperty("project", out var project)
            && project.TryGetProperty("frameworks", out var targets))
        {
            foreach (var target in targets.EnumerateObject())
            {
                if (target.Value.TryGetProperty("dependencies", out var deps))
                {
                    packages.AddRange(deps.EnumerateObject().Select(d => d.Name));
                }

                if (target.Value.TryGetProperty("frameworkReferences", out var refs))
                {
                    frameworks.AddRange(refs.EnumerateObject().Select(d => d.Name));
                }
            }
        }

        return new ProjectFacts(facts.Sdk, [.. packages.Distinct()], [.. frameworks.Distinct()], facts.ProjectReferences);
    }

    /// <summary>Violations for one project (named like <c>Adapters.Mongo</c>, without the solution prefix).</summary>
    public static IReadOnlyList<string> Violations(string project, ProjectFacts facts)
    {
        var found = new List<string>();
        var references = facts.Packages.Concat(facts.FrameworkReferences).Append(facts.Sdk).ToList();

        foreach (var restriction in Restrictions.Where(r => !r.AllowedProjects.Contains(project)))
        {
            found.AddRange(references.Where(r => Regex.IsMatch(r, restriction.Pattern))
                .Select(r => $"{project} references {r}, which belongs only in {string.Join(" and ", restriction.AllowedProjects)} ({restriction.Description})"));
        }

        // Framework and package references flow through ProjectReferences, so the reference graph is part of
        // the rule: a project may only reference the projects the plan (section 3.1) lets it reference.
        if (AllowedProjectReferences.TryGetValue(project, out var allowed))
        {
            found.AddRange(facts.ProjectReferences.Where(r => !allowed.Contains(r))
                .Select(r => $"{project} references project {r}; it may reference only {(allowed.Length == 0 ? "nothing" : string.Join(", ", allowed))}"));
        }

        if (project == "Domain")
        {
            found.AddRange(facts.Packages.Where(p => p != "OneOf")
                .Select(p => $"Domain references package {p}; only OneOf is allowed"));
            found.AddRange(facts.FrameworkReferences.Where(f => f != "Microsoft.NETCore.App").Select(f => $"Domain references framework {f}"));
        }

        return found;
    }

    public static IReadOnlyList<string> ProjectNames { get; } =
        ["Domain", "Application", .. Layout.AdapterNames.Select(a => $"Adapters.{a}"), "Host"];

    /// <summary>The src directory, found by walking up from the test binaries to the solution file.</summary>
    public static string SrcDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Huishoudplanner.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Huishoudplanner.slnx not found above the test binaries"), "src");
    }

    public static ProjectFacts Load(string project)
    {
        var folder = Path.Combine(SrcDirectory(), $"Huishoudplanner.{project}");
        var facts = ParseCsproj(File.ReadAllText(Path.Combine(folder, $"Huishoudplanner.{project}.csproj")));
        var assets = Path.Combine(folder, "obj", "project.assets.json");
        return File.Exists(assets) ? Merge(facts, File.ReadAllText(assets)) : facts;
    }
}
