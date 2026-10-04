using Huishoudplanner.Domain.Errors;

namespace Huishoudplanner.Domain.Concurrency;

/// <summary>
/// The version of a stored entity (ADR-0022): an integer that every real change of its document raises by one, in the same write. A document
/// without a version (legacy, Node or imported data) reads as <see cref="Legacy"/>; a document created by this application starts at
/// <see cref="Initial"/>. A write that changes nothing writes nothing and keeps the version. The HTTP adapter shows the version as the strong
/// validator <c>"&lt;version&gt;"</c> in the <c>ETag</c> header and as the <c>version</c> member of the entity.
/// </summary>
public static class EntityVersion
{
    /// <summary>What a document without a stored version reads as.</summary>
    public const int Legacy = 0;

    /// <summary>The version of an entity this application creates.</summary>
    public const int Initial = 1;

    /// <summary>
    /// The precondition of a write: <see langword="null"/> when the version the caller expects (<see langword="null"/> means "unconditional",
    /// for flows without a client precondition) is the stored one, otherwise the <see cref="PreconditionFailed"/> to answer with.
    /// </summary>
    public static PreconditionFailed? Check(int? expected, int current) =>
        expected is { } wanted && wanted != current ? new PreconditionFailed(current) : null;
}
