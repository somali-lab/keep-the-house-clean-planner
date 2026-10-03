using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit.Sdk;

namespace Huishoudplanner.Domain.Tests.Support;

/// <summary>
/// One case of a golden vector file (Vectors/README.md). Serializable so that xunit lists every case as its own test.
/// </summary>
public sealed class VectorCase : IXunitSerializable
{
    private string _module = "";
    private int _index;
    private JsonElement _case;

    public VectorCase()
    {
    }

    private VectorCase(string module, int index, JsonElement vectorCase)
    {
        _module = module;
        _index = index;
        _case = vectorCase;
    }

    public string Name => _case.GetProperty("name").GetString()!;

    public string Function => _case.GetProperty("function").GetString()!;

    public JsonElement Input => _case.GetProperty("input");

    public bool Throws => _case.TryGetProperty("throws", out _);

    public string? ThrowsKind => Throws ? _case.GetProperty("throws").GetString() : null;

    public JsonNode? Expected => JsonNode.Parse(_case.GetProperty("expected").GetRawText());

    /// <summary>All cases of a vector file, as theory data.</summary>
    public static TheoryData<VectorCase> Load(string module)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(PathOf(module)));
        var data = new TheoryData<VectorCase>();
        var index = 0;
        foreach (var vectorCase in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            data.Add(new VectorCase(module, index++, vectorCase.Clone()));
        }

        return data;
    }

    public void Deserialize(IXunitSerializationInfo info)
    {
        _module = info.GetValue<string>("module")!;
        _index = info.GetValue<int>("index");
        using var document = JsonDocument.Parse(File.ReadAllText(PathOf(_module)));
        _case = document.RootElement.GetProperty("cases")[_index].Clone();
    }

    public void Serialize(IXunitSerializationInfo info)
    {
        info.AddValue("module", _module);
        info.AddValue("index", _index);
    }

    public override string ToString() => $"{Function}: {Name}";

    private static string PathOf(string module) => Path.Combine(AppContext.BaseDirectory, "Vectors", module + ".json");
}
