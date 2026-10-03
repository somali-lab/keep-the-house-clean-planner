using System.Text.Json;
using System.Text.RegularExpressions;
using OneOf;

namespace Huishoudplanner.Domain.Ai;

/// <summary>The model's answer was not a JSON document. Maps to <c>422 ai_invalid_response</c>.</summary>
public sealed record NotJson(string Message);

/// <summary>Provider-neutral handling of model output (port of <c>stripFence</c> and the parse step of the Node server).</summary>
public static partial class ModelJson
{
    public const string NotJsonMessage = "The AI answer was not valid JSON";

    // JSON.parse has no depth limit of its own; the .NET default of 64 would reject answers Node accepts.
    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 512 };

    [GeneratedRegex(@"^\s*```(?:json)?\s*([\s\S]*?)\s*```\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Fence();

    /// <summary>Removes one surrounding markdown code fence, if any, and parses the rest as JSON.</summary>
    public static OneOf<JsonElement, NotJson> Extract(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        try
        {
            var fenced = Fence().Match(raw);
            using var document = JsonDocument.Parse(fenced.Success ? fenced.Groups[1].Value : raw, ParseOptions);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or RegexMatchTimeoutException)
        {
            // A fence that takes too long to match is treated like any other unusable answer.
            return new NotJson(NotJsonMessage);
        }
    }
}
