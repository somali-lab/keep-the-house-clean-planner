using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using MongoDB.Driver;
using QuestPDF.Fluent;

// Every type below breaks exactly the rule named in its summary. The architecture tests assert that
// the production rules, pointed at this namespace root, flag the type. None of this ships.

/// <summary>A global-namespace Program outside the Host assembly: a stray.</summary>
public sealed class Program;

namespace Huishoudplanner.Fixtures.Bad.Domain
{
    /// <summary>A file-local type (compiled as a top-level type whose name starts with a angle bracket) reading the clock.</summary>
    file static class FileLocalClock
    {
        public static DateTime Read() => DateTime.UtcNow;
    }

    /// <summary>Domain depends on Application.</summary>
    public sealed class DomainReachesApplication
    {
        public Application.ApplicationTarget? Target { get; set; }
    }

    /// <summary>Domain depends on an adapter.</summary>
    public sealed class DomainReachesAdapter
    {
        public Adapters.Pdf.PdfTarget? Target { get; set; }
    }

    /// <summary>Domain depends on the Host.</summary>
    public sealed class DomainReachesHost
    {
        public Host.HostTarget? Target { get; set; }
    }

    /// <summary>Domain uses MongoDB.Driver (also a third-party dependency beyond OneOf).</summary>
    public sealed class DomainUsesMongo
    {
        public IMongoDatabase? Database { get; set; }
    }

    /// <summary>Domain reads the clock inside an async lambda (compiled into a nested generated type).</summary>
    public sealed class DomainReadsClockInAsyncLambda
    {
        public Func<Task<DateTime>> Reader() => async () =>
        {
            await Task.Yield();
            return DateTime.UtcNow;
        };
    }

    /// <summary>Domain reads the clock inside an async local function.</summary>
    public sealed class DomainReadsClockInAsyncLocalFunction
    {
        public async Task<DateTimeOffset> Read()
        {
            await Task.Yield();
            return Inner();

            static DateTimeOffset Inner() => DateTimeOffset.Now;
        }
    }

    /// <summary>Domain reads the clock inside an async iterator.</summary>
    public sealed class DomainReadsClockInAsyncIterator
    {
        public async IAsyncEnumerable<DateTime> Stream()
        {
            await Task.Yield();
            yield return DateTime.Today;
        }
    }

    /// <summary>Domain uses TimeProvider.System directly.</summary>
    public sealed class DomainUsesTimeProvider
    {
        public DateTimeOffset Now() => TimeProvider.System.GetUtcNow();
    }

    /// <summary>Domain reads the wall clock.</summary>
    public sealed class DomainReadsClock
    {
        public DateTime Stamp() => DateTime.UtcNow;
    }
}

namespace Huishoudplanner.Fixtures.Bad.Domain.Ports.Driven
{
    /// <summary>Driven port not named For*.</summary>
    public interface IStoringThings;

    /// <summary>Named For* but not an interface: allowed beside ports.</summary>
    public sealed class ForBeingAClass;

    /// <summary>A command next to the ports: allowed.</summary>
    public sealed record StoreThingCommand(string Name);
}

namespace Huishoudplanner.Fixtures.Bad.Domain.Ports.Driving
{
    /// <summary>Driving port not named I*Service.</summary>
    public interface ThingManager;

    /// <summary>Named I*Service but not an interface: allowed beside ports.</summary>
    public sealed class IThingService;

    /// <summary>A command next to the ports: allowed.</summary>
    public sealed record RenameThingCommand(string Name);
}

namespace Huishoudplanner.Fixtures.Bad.Domain.Misplaced
{
    /// <summary>A For* interface outside the driven ports namespace.</summary>
    public interface ForMisplacedThings;
}

namespace Huishoudplanner.Fixtures.Bad.Application
{
    /// <summary>Plain target for the Domain-to-Application violation.</summary>
    public sealed class ApplicationTarget;

    /// <summary>Application depends on an adapter.</summary>
    public sealed class ApplicationReachesAdapter
    {
        public Adapters.Mongo.MongoTarget? Target { get; set; }
    }

    /// <summary>Application depends on the Host.</summary>
    public sealed class ApplicationReachesHost
    {
        public Host.HostTarget? Target { get; set; }
    }

    /// <summary>Application writes to Mongo inside an async lambda.</summary>
    public sealed class ApplicationWritesToMongoInAsyncLambda
    {
        public Func<IMongoCollection<BsonDocument>, Task> Writer() =>
            async collection =>
            {
                await Task.Yield();
                await collection.ReplaceOneAsync(FilterDefinition<BsonDocument>.Empty, new BsonDocument());
            };
    }

    /// <summary>Application writes with an aggregation that ends in $out.</summary>
    public sealed class ApplicationAggregatesIntoOut
    {
        public Task Write(IMongoCollection<BsonDocument> collection) =>
            collection.Aggregate().Out("elsewhere").ToListAsync();
    }

    /// <summary>Application writes to Mongo directly.</summary>
    public sealed class ApplicationWritesToMongo
    {
        public Task Write(IMongoCollection<BsonDocument> collection) =>
            collection.InsertOneAsync(new BsonDocument());
    }
}

namespace Huishoudplanner.Fixtures.Bad.Adapters.Mongo
{
    public sealed class MongoTarget;

    /// <summary>Mongo adapter depends on another adapter.</summary>
    public sealed class MongoReachesPdf
    {
        public Pdf.PdfTarget? Target { get; set; }
    }

    /// <summary>Mongo adapter uses ASP.NET Core.</summary>
    public sealed class MongoUsesAspNet
    {
        public HttpContext? Context { get; set; }
    }
}

namespace Huishoudplanner.Fixtures.Bad.Adapters.Http
{
    public sealed class HttpTarget;

    /// <summary>Http adapter depends on another adapter.</summary>
    public sealed class HttpReachesAi
    {
        public Ai.AiTarget? Target { get; set; }
    }

    /// <summary>Http adapter deletes through MongoDB.Driver.</summary>
    public sealed class HttpWritesToMongo
    {
        public Task Write(IMongoCollection<BsonDocument> collection) =>
            collection.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
    }

    /// <summary>Http adapter uses QuestPDF.</summary>
    public sealed class HttpUsesQuestPdf
    {
        public Document? Document { get; set; }
    }

    /// <summary>Adapter depends on the Host.</summary>
    public sealed class HttpReachesHost
    {
        public Host.HostTarget? Target { get; set; }
    }
}

namespace Huishoudplanner.Fixtures.Bad.Adapters.Pdf
{
    public sealed class PdfTarget;

    /// <summary>Pdf adapter depends on another adapter.</summary>
    public sealed class PdfReachesJobs
    {
        public Jobs.JobsTarget? Target { get; set; }
    }
}

namespace Huishoudplanner.Fixtures.Bad.Adapters.Ai
{
    public sealed class AiTarget;

    /// <summary>Ai adapter depends on another adapter.</summary>
    public sealed class AiReachesHttp
    {
        public Http.HttpTarget? Target { get; set; }
    }
}

namespace Huishoudplanner.Fixtures.Bad.Adapters.Notify
{
    public sealed class NotifyTarget;

    /// <summary>Notify adapter depends on another adapter.</summary>
    public sealed class NotifyReachesMongo
    {
        public Mongo.MongoTarget? Target { get; set; }
    }

    /// <summary>Notify adapter uses Microsoft.Extensions.AI.</summary>
    public sealed class NotifyUsesAi
    {
        public IChatClient? Client { get; set; }
    }
}

namespace Huishoudplanner.Fixtures.Bad.Adapters.Jobs
{
    public sealed class JobsTarget;

    /// <summary>Jobs adapter depends on another adapter.</summary>
    public sealed class JobsReachesNotify
    {
        public Notify.NotifyTarget? Target { get; set; }
    }
}

namespace Huishoudplanner.Fixtures.Bad.Host
{
    public sealed class HostTarget;

    /// <summary>A type with Clock in its name that is not the exact SystemClock.</summary>
    public sealed class NotQuiteSystemClock
    {
        public DateTime Now() => DateTime.UtcNow;
    }

    /// <summary>Host type that reads the clock but is not a *Clock type.</summary>
    public sealed class Scheduler
    {
        public DateTimeOffset Now() => DateTimeOffset.UtcNow;
    }
}

namespace Huishoudplanner.Fixtures.Bad.Adapters.Jobs.Time
{
    /// <summary>A *Clock type outside the Host.</summary>
    public sealed class UnwelcomeClock
    {
        public DateTime Today() => DateTime.Today;
    }
}

namespace Huishoudplanner.Fixtures.Bad.Elsewhere
{
    /// <summary>A type outside the root namespace of its assembly.</summary>
    public sealed class StrayType;
}
