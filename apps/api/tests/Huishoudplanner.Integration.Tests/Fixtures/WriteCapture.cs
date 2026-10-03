using System.Collections.Concurrent;
using Huishoudplanner.Adapters.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Configuration;
using MongoDB.Driver.Core.Events;

namespace Huishoudplanner.Integration.Tests.Fixtures;

/// <summary>One command the application sent to MongoDB that changes data or schema.</summary>
/// <param name="Collection">The collection the command targets.</param>
/// <param name="Operation">The wire command: insert, update, delete, findAndModify, bulkWrite or an aggregate that ends in <c>$merge</c> or <c>$out</c> (a state write), or a schema command such as createIndexes.</param>
/// <param name="Documents">How many documents were inserted, modified, upserted or deleted (1 for a findAndModify, an aggregate write or a schema command; the operations of that namespace for a bulkWrite).</param>
public sealed record CapturedWrite(string Collection, string Operation, int Documents);

/// <summary>
/// The .NET counterpart of <c>captureWrites</c> in <c>apps/server/test/helpers/audit.ts</c>: watches every write command the
/// application's MongoDB client sends (driver command monitoring, plugged into the client through the
/// <see cref="IMongoClientSettingsCustomizer"/> seam; see <see cref="ApiFactory.WithWriteCapture"/>) so a test can assert what a request wrote.
/// Only commands that changed something are recorded: an update that matched but modified nothing (such as the insert-if-absent upsert of the generation) is a no-op, and so is a delete that removed nothing.
/// Writes of a transaction that was aborted are dropped, because they changed nothing.
/// </summary>
/// <remarks>
/// Limits, deliberately conservative (they can only make the capture report more, never less): a findAndModify that matched but modified nothing cannot be told from one that changed
/// something (the reply does not say), so a match counts as a change; a bulkWrite (server 8 command) reports only totals, so its per-namespace count is the number of operations sent and it
/// is recorded only when the totals show a change; an aggregate ending in <c>$merge</c> or <c>$out</c> reports no effect and always counts as one write on its target.
/// </remarks>
public sealed class WriteCapture
{
    private const string MergeOperation = "aggregate($merge)";
    private const string OutOperation = "aggregate($out)";

    /// <summary>Commands (and aggregate variants) that change state.</summary>
    private static readonly HashSet<string> StateOperations = ["insert", "update", "delete", "findAndModify", "bulkWrite", MergeOperation, OutOperation];

    /// <summary>Commands that change the schema; the application sends none while serving a request (startup ensures indexes before).</summary>
    private static readonly HashSet<string> SchemaCommands = ["createIndexes", "dropIndexes", "create", "drop", "dropDatabase", "renameCollection", "collMod"];

    private static readonly string[] BulkTotals = ["nInserted", "nUpserted", "nModified", "nDeleted"];
    private static readonly string[] BulkOps = ["insert", "update", "delete"];

    private sealed record Pending(IReadOnlyList<(string Collection, int Operations)> Targets, string Operation, string? Transaction);

    private readonly ConcurrentQueue<(CapturedWrite Write, string? Transaction)> events = new();
    private readonly ConcurrentQueue<string> aborted = new();
    private readonly ConcurrentDictionary<int, Pending> pending = new();
    private int abortedTransactions;

    /// <summary>How many transactions were rolled back since the last <see cref="Clear"/>.</summary>
    public int AbortedTransactions => Volatile.Read(ref abortedTransactions);

    /// <summary>Forgets what was captured so far. Call it right before the request under test.</summary>
    public void Clear()
    {
        events.Clear();
        aborted.Clear();
        Volatile.Write(ref abortedTransactions, 0);
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
        All().Where(w => StateOperations.Contains(w.Operation) && w.Collection is not (MongoCollections.AuditLog or MongoCollections.Migrations)).ToList();

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
        var command = started.Command;
        var transaction = TransactionOf(command);
        if (name == "abortTransaction" && transaction is not null)
        {
            aborted.Enqueue(transaction);
            Interlocked.Increment(ref abortedTransactions);
            return;
        }

        Pending? tracked = null;
        if (name == "bulkWrite")
        {
            var targets = BulkTargets(command);
            tracked = targets.Count > 0 ? new Pending(targets, name, transaction) : null;
        }
        else if (name == "aggregate")
        {
            var write = AggregateWrite(command);
            tracked = write is { } w ? new Pending([(w.Collection, 1)], w.Operation, transaction) : null;
        }
        else if ((StateOperations.Contains(name) || SchemaCommands.Contains(name)) && command.TryGetValue(name, out var collection) && collection.IsString)
        {
            tracked = new Pending([(collection.AsString, 1)], name, transaction);
        }

        if (tracked is not null)
        {
            pending[started.RequestId] = tracked;
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
        if (changed <= 0)
        {
            return;
        }

        foreach (var (collection, operations) in command.Targets)
        {
            var documents = command.Operation == "bulkWrite" ? operations : changed;
            events.Enqueue((new CapturedWrite(collection, command.Operation, documents), command.Transaction));
        }
    }

    /// <summary>
    /// What a reply says was changed. Statements that failed (<c>writeErrors</c>) are not in <c>n</c> or <c>nModified</c> (insert and delete count
    /// the applied ones only), so a command whose every statement failed counts as nothing.
    /// </summary>
    private static int EffectOf(string operation, BsonDocument reply) => operation switch
    {
        "insert" or "delete" => reply.GetValue("n", 0).ToInt32(),
        "update" => reply.GetValue("nModified", 0).ToInt32() + (reply.TryGetValue("upserted", out var upserted) && upserted.IsBsonArray ? upserted.AsBsonArray.Count : 0),
        "findAndModify" => reply.TryGetValue("lastErrorObject", out var last) && last.IsBsonDocument ? last.AsBsonDocument.GetValue("n", 0).ToInt32() : 0,
        "bulkWrite" => BulkTotals.Sum(field => reply.GetValue(field, 0).ToInt32()),
        _ => 1,
    };

    /// <summary>The namespaces of a bulkWrite and how many operations went to each (the ops carry an index into <c>nsInfo</c>).</summary>
    private static List<(string Collection, int Operations)> BulkTargets(BsonDocument command)
    {
        if (!command.TryGetValue("nsInfo", out var info) || !info.IsBsonArray)
        {
            return [];
        }

        var names = info.AsBsonArray.Select(n => n.AsBsonDocument.GetValue("ns", string.Empty).AsString).Select(ns => ns[(ns.IndexOf('.', StringComparison.Ordinal) + 1)..]).ToList();
        var counts = new int[names.Count];
        if (command.TryGetValue("ops", out var ops) && ops.IsBsonArray)
        {
            foreach (var op in ops.AsBsonArray.OfType<BsonDocument>())
            {
                var index = BulkOps.Select(k => op.TryGetValue(k, out var i) ? i.ToInt32() : -1).Max();
                if (index >= 0 && index < counts.Length)
                {
                    counts[index]++;
                }
            }
        }

        return names.Select((name, i) => (name, counts[i])).Where(t => t.Item2 > 0).ToList();
    }

    /// <summary>An aggregate whose last stage is <c>$merge</c> or <c>$out</c> writes to its target collection.</summary>
    private static (string Collection, string Operation)? AggregateWrite(BsonDocument command)
    {
        if (!command.TryGetValue("pipeline", out var pipeline) || !pipeline.IsBsonArray || pipeline.AsBsonArray.Count == 0 || pipeline.AsBsonArray[^1] is not BsonDocument last)
        {
            return null;
        }

        if (last.TryGetValue("$out", out var output))
        {
            return (output.IsString ? output.AsString : output.AsBsonDocument.GetValue("coll", string.Empty).AsString, OutOperation);
        }

        if (last.TryGetValue("$merge", out var merge))
        {
            var into = merge.IsString ? merge : merge.AsBsonDocument.GetValue("into", string.Empty);
            return (into.IsString ? into.AsString : into.AsBsonDocument.GetValue("coll", string.Empty).AsString, MergeOperation);
        }

        return null;
    }

    private static string? TransactionOf(BsonDocument command) =>
        command.TryGetValue("txnNumber", out var number) && command.TryGetValue("lsid", out var session) ? $"{session}/{number}" : null;
}

internal sealed class WriteCaptureCustomizer(WriteCapture capture) : IMongoClientSettingsCustomizer
{
    public void Customize(MongoClientSettings settings) => capture.Attach(settings);
}
