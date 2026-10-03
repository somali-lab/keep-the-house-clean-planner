using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;
using QuestPDF.Fluent;

// A small conforming layout. The architecture tests assert that every production rule passes on it
// with positive results, so a rule cannot be green only because it matched nothing.

namespace Huishoudplanner.Fixtures.Good.Domain
{
    public sealed class Thing
    {
        public OneOf<int, string> Value { get; set; }
    }
}

namespace Huishoudplanner.Fixtures.Good.Domain.Ports.Driven
{
    public interface ForStoringThings
    {
        Task Save(Thing thing);
    }

    public sealed record StoreThingCommand(string Name);

    public interface ForTellingTime
    {
        DateTimeOffset Now();
    }
}

namespace Huishoudplanner.Fixtures.Good.Domain.Ports.Driving
{
    public sealed record RenameThingCommand(string Name);

    public interface IThingService
    {
        Task Rename(Thing thing);
    }
}

namespace Huishoudplanner.Fixtures.Good.Application
{
    public sealed class ThingService(Domain.Ports.Driven.ForStoringThings store) : Domain.Ports.Driving.IThingService
    {
        public Task Rename(Domain.Thing thing) => store.Save(thing);
    }
}

namespace Huishoudplanner.Fixtures.Good.Adapters.Mongo
{
    public sealed class MongoThingStore(IMongoCollection<BsonDocument> collection) : Domain.Ports.Driven.ForStoringThings
    {
        public Task Save(Domain.Thing thing) => collection.InsertOneAsync(new BsonDocument());
    }
}

namespace Huishoudplanner.Fixtures.Good.Adapters.Http
{
    public sealed class ThingEndpoint(Domain.Ports.Driving.IThingService service)
    {
        public Task Handle(HttpContext context) => service.Rename(new Domain.Thing());
    }
}

namespace Huishoudplanner.Fixtures.Good.Adapters.Pdf
{
    public sealed class ThingSheet
    {
        public Document? Document { get; set; }
    }
}

namespace Huishoudplanner.Fixtures.Good.Adapters.Ai
{
    public sealed class ThingAssistant
    {
        public IChatClient? Client { get; set; }
    }
}

namespace Huishoudplanner.Fixtures.Good.Adapters.Notify
{
    public sealed class ThingNotifier;
}

namespace Huishoudplanner.Fixtures.Good.Adapters.Jobs
{
    public sealed class ThingJob;
}

namespace Huishoudplanner.Fixtures.Good.Host
{
    /// <summary>The one place allowed to read the wall clock.</summary>
    public sealed class SystemClock : Domain.Ports.Driven.ForTellingTime
    {
        public DateTimeOffset Now() => DateTimeOffset.UtcNow;

        public DateTime LocalToday() => DateTime.Now.Date;
    }

    /// <summary>The composition root: references everything.</summary>
    public sealed class Composition
    {
        public Adapters.Mongo.MongoThingStore? Store { get; set; }
        public Adapters.Http.ThingEndpoint? Endpoint { get; set; }
        public Adapters.Pdf.ThingSheet? Sheet { get; set; }
        public Adapters.Ai.ThingAssistant? Assistant { get; set; }
        public Adapters.Notify.ThingNotifier? Notifier { get; set; }
        public Adapters.Jobs.ThingJob? Job { get; set; }
        public Application.ThingService? Service { get; set; }
    }
}
