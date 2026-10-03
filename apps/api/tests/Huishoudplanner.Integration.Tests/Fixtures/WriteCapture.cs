using System.Collections.Concurrent;
using Huishoudplanner.Adapters.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Configuration;
using MongoDB.Driver.Core.Events;

namespace Huishoudplanner.Integration.Tests.Fixtures;

/// <summary>One command the application sent to MongoDB that changes data or schema.</summary>
/// <param name="Collection">The collection the command targets.</param>
/// <param name="Operation">The wire command: insert, update, delete or findAndModify (a state write), or a schema command such as createIndexes.</param>
/// <param name="Documents">How many documents were inserted, modified, upserted or deleted (1 for a findAndModify or a schema command).</param>
public sealed record CapturedWrite(string Collection, string Operation, int Documents);

/// <summary>
/// The .NET counterpart of <c>captureWrites</c> in <c>apps/server/test/helpers/audit.ts</c>: watches every write command the
/// application's MongoDB client sends (driver command monitoring, plugged into the client through the
/// <see cref="IMongoClientSettingsCustomizer"/> seam; see <see cref="ApiFactory.WithWriteCapture"/>) so a test can assert what a request wrote.
/// Only commands that changed something are recorded: an update that matched but modified nothing (such as the insert-if-absent upsert of the generation) is a no-op, and so is a delete that removed nothing.
/// Writes of a transaction that was aborted are dropped, because they changed nothing.
/// </summary>
public sealed class WriteCapture
{
    /// <summary>Commands that change state.</summary>
    private static readonly HashSet<string> StateCommands = ["insert", "update", "delete", "findAndModify"];

    /// <summary>Commands that change the schema; the application sends none while serving a request (startup ensures indexes before).</summary>
    private static readonly HashSet<string> SchemaCommands = ["createIndexes", "dropIndexes", "create", "drop", "dropDatabase", "renameCollection", "collMod"];

    private readonly ConcurrentQueue<(CapturedWrite Write, string? Transaction)> events = new();
    private readonly ConcurrentQueue<string> aborted = new();
    private readonly ConcurrentDictionary<int, (string Collection, string Operation, string? Transaction)> pending = new();

    /// <summary>Forgets what was captured so far. Call it right before the request under test.</summary>
    public void Clear()
    {
        events.Clear();
        aborted.Clear();
    }

    /// <summary>Everything captured since the last <see cref="Clear"/>, in order, without the writes of aborted transactions.</summary>
    public IReadOnlyList<CapturedWrite> All()
    {
        var rolledBack = aborted.ToHashSet();
        return events.Where(e => e.Transaction is null || !rolledBack.Contains(e.Transaction)).Select(e => e.Write).ToList();
    }

    /// <summary>
    /// The state writes of the request: the audit collection (what is checked against) and <c>migrations</c> (bookkeeping of
    /// startup, never state of the household) are left out.
    /// </summary>
    public IReadOnlyList<CapturedWrite> Writes() =>
        All().Where(w => StateCommands.Contains(w.Operation) && w.Collection is not (MongoCollections.AuditLog or MongoCollections.Migrations)).ToList();

    /// <summary>The audit documents inserted by the request.</summary>
    public int AuditInserts() =>
        All().Where(w => w is { Collection: MongoCollections.AuditLog, Operation: "insert" }).Sum(w => w.Documents);

    /// <summary>Schema commands of the request (there must be none).</summary>
    public IReadOnlyList<CapturedWrite> SchemaChanges() => All().Where(w => SchemaCommands.Contains(w.Operation)).ToList();

    internal void Attach(MongoClientSettings settings)
    {
        var previous = settings.ClusterConfigurator;
        settings.ClusterConfigurator = builder =>
        {
            previous?.Invoke(builder);
            builder.Subscribe<CommandStartedEvent>(OnStarted);
            builder.Subscribe<CommandSucceededEvent>(OnSucceeded);
            builder.Subscribe<CommandFailedEvent>(failed => pending.TryRemove(failed.RequestId, out _));
        };
    }

    private void OnStarted(CommandStartedEvent started)
    {
        var name = started.CommandName;
        var transaction = TransactionOf(started.Command);
        if (name == "abortTransaction" && transaction is not null)
        {
            aborted.Enqueue(transaction);
            return;
        }

        var tracked = StateCommands.Contains(name) || SchemaCommands.Contains(name);
        if (tracked && started.Command.TryGetValue(name, out var collection) && collection.IsString)
        {
            pending[started.RequestId] = (collection.AsString, name, transaction);
        }
    }

    /// <summary>Only a command that changed something counts: an update that matched but modified nothing, or a delete that removed nothing, is a no-op.</summary>
    private void OnSucceeded(CommandSucceededEvent succeeded)
    {
        if (!pending.TryRemove(succeeded.RequestId, out var command))
        {
            return;
        }

        var changed = EffectOf(command.Operation, succeeded.Reply);
        if (changed > 0)
        {
            events.Enqueue((new CapturedWrite(command.Collection, command.Operation, changed), command.Transaction));
        }
    }

    private static int EffectOf(string operation, BsonDocument reply) => operation switch
    {
        "insert" or "delete" => reply.GetValue("n", 0).ToInt32(),
        "update" => reply.GetValue("nModified", 0).ToInt32() + (reply.TryGetValue("upserted", out var upserted) && upserted.IsBsonArray ? upserted.AsBsonArray.Count : 0),
        "findAndModify" => reply.TryGetValue("lastErrorObject", out var last) && last.IsBsonDocument ? last.AsBsonDocument.GetValue("n", 0).ToInt32() : 0,
        _ => 1,
    };

    private static string? TransactionOf(BsonDocument command) =>
        command.TryGetValue("txnNumber", out var number) && command.TryGetValue("lsid", out var session) ? $"{session}/{number}" : null;
}

internal sealed class WriteCaptureCustomizer(WriteCapture capture) : IMongoClientSettingsCustomizer
{
    public void Customize(MongoClientSettings settings) => capture.Attach(settings);
}
