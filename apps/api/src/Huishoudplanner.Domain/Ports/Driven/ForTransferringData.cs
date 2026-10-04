using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Transfer;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// The whole dataset as a JSON file, and back (requirements 8; <c>data/transfer.ts</c> and <c>domain/transfer.ts</c> of the Node server). The file holds
/// MongoDB relaxed Extended JSON per collection, so ids, dates and the binary badge images survive the round trip unchanged.
/// </summary>
public interface ForTransferringData
{
    /// <summary>The export file as UTF-8 JSON, stamped with <paramref name="now"/>. Reads only; every collection is ordered by id.</summary>
    Task<OneOf<byte[], PortError>> ExportAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Reads and validates a whole file before anything is written: the envelope, the shape and storage types of every document and the
    /// rules between documents. Every problem is a field-keyed <see cref="ValidationErrors"/> (the key is the path in the file, for example
    /// <c>collections.users.0.color</c>; at most 200 problems are reported). <paramref name="now"/> judges the bonus schedule against today.
    /// </summary>
    Task<OneOf<ParsedImport, ValidationErrors, PortError>> ParseAsync(Stream body, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The redemptions and badges that exist, which a file of an older version would remove.</summary>
    Task<OneOf<ExistingCounts, PortError>> CountExistingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Replaces every collection with the file's documents, drops the points ledger (the redemptions of the file are put back) and the badge awards,
    /// and merges the audit log (entries already in it stay and are not added twice). It only runs inside <see cref="ForRunningTransactions"/>, together
    /// with the import audit entry, so a failure anywhere leaves everything as it was; called outside a transaction it writes nothing and returns a
    /// <see cref="PortError"/> whose message starts with <c>transfer.no_transaction</c>.
    /// </summary>
    Task<OneOf<ImportResult, PortError>> ReplaceAsync(ParsedImport parsed, CancellationToken cancellationToken);
}
