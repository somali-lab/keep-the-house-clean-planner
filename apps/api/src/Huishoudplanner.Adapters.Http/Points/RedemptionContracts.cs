using System.ComponentModel;

namespace Huishoudplanner.Adapters.Http.Points;

// The request record below only documents the body in OpenAPI; the body is read by RedemptionRequestParser (Zod-style validation_error for
// a wrong type, null or malformed JSON), never bound to this type.

/// <summary>A redemption: a person gives up points for a payout or a reward.</summary>
public sealed record CreateRedemptionRequest(
    [property: Description("The person; left out: the active profile. Only an administrator may name another person.")] string? PersonId,
    [property: Description("The points to give up: a whole number of at least 1, at most the balance of the person over the whole ledger.")] int? Points,
    [property: Description("Free text, trimmed, at most 200 characters; empty means none.")] string? Note,
    [property: Description("Idempotency key, 16 to 64 characters of letters, digits, underscore and hyphen.")] string? RequestId);

/// <summary>How many redemptions exist.</summary>
public sealed record RedemptionCountResponse(long Count);

/// <summary>The answer of an undo.</summary>
public sealed record DeleteRedemptionResponse(bool Deleted);
