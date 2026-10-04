using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Transfer;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>
/// The JSON export and import of the whole dataset (requirements 8). The export needs no actor, like in the Node server. The import is for
/// administrators, which the driving adapter decides; it replaces all data in one transaction with one audit entry and then rebuilds the derived
/// ledger.
/// </summary>
public interface ITransferService
{
    Task<OneOf<TransferFile, PortError>> ExportAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Imports the file in <paramref name="body"/>. In this order: a <c>mode</c> other than <c>replace</c> is a <see cref="ValidationErrors"/> on <c>mode</c>;
    /// no <c>confirm=true</c> is <see cref="ConfirmationRequired"/>; a file that is not valid is a <see cref="ValidationErrors"/> keyed by the path in the file;
    /// a file older than version 5 while redemptions exist, or older than version 6 while badges exist, is a <see cref="ConflictError"/>
    /// (<c>redemptions_would_be_removed</c>, <c>badges_would_be_removed</c>, with the <c>count</c>) unless acknowledged. Nothing is written for any refusal.
    /// </summary>
    Task<OneOf<ImportResult, ValidationErrors, ConfirmationRequired, ConflictError, PortError>> ImportAsync(
        Actor actor, ImportOptions options, Stream body, CancellationToken cancellationToken);
}
