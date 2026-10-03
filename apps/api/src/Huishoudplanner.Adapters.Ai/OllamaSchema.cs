using System.Text.Json;
using System.Text.Json.Nodes;

namespace Huishoudplanner.Adapters.Ai;

/// <summary>
/// Ollama's grammar converter does not accept the JSON Schema 2020-12 tuple representation (<c>prefixItems</c> plus
/// <c>items: false</c>). The fixed length stays, the item type is expressed as a regular array schema it understands.
/// </summary>
internal static class OllamaSchema
{
    public static JsonElement Convert(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText());
        return JsonSerializer.SerializeToElement(ConvertNode(node));
    }

    private static JsonNode? ConvertNode(JsonNode? value)
    {
        switch (value)
        {
            case JsonArray array:
                return new JsonArray([.. array.Select(item => ConvertNode(item?.DeepClone()))]);
            case JsonObject source:
                var converted = new JsonObject();
                foreach (var (key, child) in source)
                {
                    converted[key] = ConvertNode(child?.DeepClone());
                }

                if (source["prefixItems"] is JsonArray { Count: > 0 } prefix)
                {
                    var alternatives = prefix.Select(item => ConvertNode(item?.DeepClone())).ToList();
                    var first = alternatives[0]?.ToJsonString();
                    converted["items"] = alternatives.All(a => a?.ToJsonString() == first)
                        ? alternatives[0]
                        : new JsonObject { ["anyOf"] = new JsonArray([.. alternatives]) };
                    converted.Remove("prefixItems");
                }
                else if (source["items"] is JsonValue flag && flag.TryGetValue<bool>(out var allowed) && !allowed)
                {
                    converted.Remove("items");
                }

                return converted;
            default:
                return value?.DeepClone();
        }
    }
}
