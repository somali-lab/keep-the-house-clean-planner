using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Huishoudplanner.Architecture.Tests;

/// <summary>
/// Rules about which methods a type may call. ArchUnitNET does not load the compiler-generated types
/// that hold async lambdas, async local functions and iterators (closure classes and state machines), so
/// calls made there are invisible to it. These rules read the IL of every method, including those nested
/// generated types, and attribute each call to the outermost declaring type that the author wrote.
/// </summary>
internal static class IlRules
{
    private const string MongoDb = @"^MongoDB(\..*)?$";

    /// <summary>Mongo write operations; the .NET counterpart of the write methods lint-rule.test.ts forbids.</summary>
    private const string MongoWriteMethod =
        @"^(Insert|Update|Replace|Delete|BulkWrite|FindOneAnd|Drop|Create|Rename|Merge|Out|AggregateToCollection|Upload|RunCommand)";

    internal sealed record Call(string Caller, string CallerNamespace, string CallerAssembly, string CalleeType, string CalleeNamespace, string Method);

    private static readonly Dictionary<string, IReadOnlyList<Call>> Cache = [];

    /// <summary>Every call made by the given assemblies, attributed to the outermost type that contains the call site.</summary>
    public static IReadOnlyList<Call> CallsOf(IEnumerable<System.Reflection.Assembly> assemblies)
    {
        var all = new List<Call>();
        foreach (var assembly in assemblies)
        {
            var path = assembly.Location;
            lock (Cache)
            {
                if (!Cache.TryGetValue(path, out var calls))
                {
                    calls = Scan(path);
                    Cache[path] = calls;
                }

                all.AddRange(calls);
            }
        }

        return all;
    }

    private static List<Call> Scan(string path)
    {
        using var module = ModuleDefinition.ReadModule(path);
        var assemblyName = module.Assembly.Name.FullName;
        var calls = new List<Call>();
        foreach (var top in module.Types.Where(t => !t.Name.StartsWith('<')))
        {
            foreach (var type in Flatten(top))
            {
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.Operand is MethodReference callee && instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
                        {
                            var element = callee.DeclaringType.GetElementType();
                            calls.Add(new Call(top.FullName, top.Namespace, assemblyName, element.FullName, element.Namespace, callee.Name));
                        }
                    }
                }
            }
        }

        return calls;
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) =>
        new[] { type }.Concat(type.NestedTypes.SelectMany(Flatten));

    private static bool InScope(Layout l, Call c) =>
        Regex.IsMatch(c.CallerNamespace, l.Everything) || Regex.IsMatch(c.CallerAssembly, l.HostAssemblyPattern);

    private static bool InHost(Layout l, Call c) =>
        Regex.IsMatch(c.CallerNamespace, l.Host) || Regex.IsMatch(c.CallerAssembly, l.HostAssemblyPattern);

    private static List<string> Offenders(Layout l, Func<Call, bool> forbidden) =>
        [.. CallsOf(l.Assemblies).Where(c => InScope(l, c) && forbidden(c)).Select(c => c.Caller).Distinct().Order(StringComparer.Ordinal)];

    private static bool ReadsWallClock(Call c) =>
        c.CalleeType is "System.DateTime" or "System.DateTimeOffset"
        && c.Method is "get_Now" or "get_UtcNow" or "get_Today";

    /// <summary>
    /// Decision: the clock adapter is the exact type <c>SystemClock</c> in the Host namespace (the
    /// <c>ForTellingTime</c> implementation). Every other type, including its own neighbours, goes through the port.
    /// </summary>
    public static IReadOnlyList<string> WallClockOutsideClockAdapter(Layout l) =>
        Offenders(l, c => ReadsWallClock(c) && c.Caller != l.ClockAdapterTypeName);

    /// <summary><c>TimeProvider.System</c> is composition; only Host may touch it.</summary>
    public static IReadOnlyList<string> TimeProviderSystemOutsideHost(Layout l) =>
        Offenders(l, c => c.CalleeType == "System.TimeProvider" && c.Method == "get_System" && !InHost(l, c));

    /// <summary>
    /// Writes only in the Mongo adapter: the counterpart of apps/server/test/lint-rule.test.ts. Nothing outside
    /// Adapters.Mongo may call a MongoDB.Driver write method.
    /// </summary>
    public static IReadOnlyList<string> MongoWritesOutsideMongoAdapter(Layout l) =>
        Offenders(l, c => Regex.IsMatch(c.CalleeNamespace, MongoDb)
                          && Regex.IsMatch(c.Method, MongoWriteMethod)
                          && !Regex.IsMatch(c.CallerNamespace, l.Adapter("Mongo")));
}
