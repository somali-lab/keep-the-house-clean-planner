using MongoDB.Bson;

namespace Huishoudplanner.Adapters.Mongo;

/// <summary>One index: ordered key fields (1 ascending, -1 descending), optional name, unique flag and partial filter.</summary>
internal sealed record IndexSpec(
    IReadOnlyList<(string Field, int Direction)> Keys,
    string? ExplicitName = null,
    bool Unique = false,
    BsonDocument? PartialFilter = null)
{
    /// <summary>The server's default naming (field_direction joined by underscores), identical to the Node driver's.</summary>
    public string Name => ExplicitName ?? string.Join('_', Keys.Select(k => $"{k.Field}_{k.Direction}"));
}
