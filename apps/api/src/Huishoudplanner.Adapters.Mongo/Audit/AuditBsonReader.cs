using Huishoudplanner.Domain.Audit;
using MongoDB.Bson;

namespace Huishoudplanner.Adapters.Mongo.Audit;

/// <summary>
/// The reverse of <see cref="AuditBson"/>: turns a stored <c>before</c>, <c>after</c> or <c>meta</c> document into the
/// driver-free value tree. Any BSON type the Node server could have stored is mapped; a type with no JSON counterpart
/// (binary, regular expression, ...) is shown as its text so that one odd value never hides an entry.
/// </summary>
internal static class AuditBsonReader
{
    public static AuditValue ToValue(BsonValue value) => value.BsonType switch
    {
        BsonType.Null or BsonType.Undefined => AuditNull.Instance,
        BsonType.Boolean => new AuditBool(value.AsBoolean),
        BsonType.String => new AuditString(value.AsString),
        BsonType.Int32 => new AuditInteger(value.AsInt32),
        BsonType.Int64 => new AuditInteger(value.AsInt64),
        BsonType.Double => new AuditDouble(value.AsDouble),
        BsonType.Decimal128 => new AuditDouble(value.ToDouble()),
        BsonType.DateTime => new AuditInstant(new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero)),
        BsonType.ObjectId => new AuditObjectId(value.AsObjectId.ToString()),
        BsonType.Array => new AuditArray([.. value.AsBsonArray.Select(ToValue)]),
        BsonType.Document => ToObject(value.AsBsonDocument),
        _ => new AuditString(value.ToString() ?? string.Empty),
    };

    public static AuditObject ToObject(BsonDocument document) =>
        new(document.Elements.Select(e => new KeyValuePair<string, AuditValue>(e.Name, ToValue(e.Value))));
}
