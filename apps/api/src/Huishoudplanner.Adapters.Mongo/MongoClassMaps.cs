using MongoDB.Bson.Serialization.Conventions;

namespace Huishoudplanner.Adapters.Mongo;

/// <summary>
/// The one place where documents are mapped (plan 3.5). Domain types carry no Bson attributes;
/// per-type class maps are added here by the repository slices. Safe to call any number of times.
/// </summary>
public static class MongoClassMaps
{
    private const string ConventionPackName = "huishoudplanner";

    private static readonly object Gate = new();
    private static bool registered;

    public static void Register()
    {
        lock (Gate)
        {
            if (registered)
            {
                return;
            }

            var pack = new ConventionPack { new IgnoreExtraElementsConvention(true) };
            ConventionRegistry.Register(ConventionPackName, pack, _ => true);
            registered = true;
        }
    }
}
