using Huishoudplanner.Domain.Audit;
using MongoDB.Bson;

namespace Huishoudplanner.Adapters.Mongo;

/// <summary>
/// Turns the driver-free audit value tree into the BSON the Node server writes: integers as int32 (int64 beyond that
/// range), other numbers as double, instants as BSON dates, ids as ObjectId, absent keys absent.
/// </summary>
internal static class AuditBson
{
    public static BsonValue ToBson(AuditValue value) => value switch
    {
        AuditNull => BsonNull.Value,
        AuditBool b => BsonBoolean.Create(b.Value),
        AuditString s => new BsonString(s.Value),
        AuditInteger i => i.Value is >= int.MinValue and <= int.MaxValue ? new BsonInt32((int)i.Value) : new BsonInt64(i.Value),
        AuditDouble d => new BsonDouble(d.Value),
        AuditInstant t => new BsonDateTime(t.Value.UtcDateTime),
        AuditObjectId id => new BsonObjectId(ObjectId.Parse(id.Hex)),
        AuditArray a => new BsonArray(a.Items.Select(ToBson)),
        AuditObject o => ToDocument(o),
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static BsonDocument ToDocument(AuditObject value)
    {
        var document = new BsonDocument();
        foreach (var (key, item) in value.Properties)
        {
            document.Add(key, ToBson(item));
        }

        return document;
    }
}
