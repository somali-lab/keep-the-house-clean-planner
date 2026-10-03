using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Application.Generation;

/// <summary>Why a step inside the generation use case stopped. Never leaves the use case: the public methods turn it into the variants of their port.</summary>
internal abstract record FlowFailure
{
    private FlowFailure()
    {
    }

    public sealed record MissingSettings : FlowFailure;

    public sealed record MissingPlan : FlowFailure;

    public sealed record Port(PortError Error) : FlowFailure;
}

/// <summary>
/// A step result that is either a value or a <see cref="FlowFailure"/>, so that the long linear algorithm of generation reads top to
/// bottom instead of nesting a <c>TryPickT</c> per port call. It is a private convenience of this class; the ports keep returning
/// <c>OneOf</c> values.
/// </summary>
internal readonly struct Flow<T>
{
    private readonly T? value;

    private readonly FlowFailure? failure;

    private Flow(T? value, FlowFailure? failure)
    {
        this.value = value;
        this.failure = failure;
    }

    public bool IsSuccess => failure is null;

    public static implicit operator Flow<T>(T value) => new(value, null);

    public static implicit operator Flow<T>(FlowFailure failure) => new(default, failure);

    public static implicit operator Flow<T>(PortError error) => new(default, new FlowFailure.Port(error));

    public bool TryGet(out T result, out FlowFailure fail)
    {
        result = value!;
        fail = failure!;
        return failure is null;
    }

    /// <summary>The same failure for another value type.</summary>
    public Flow<TOther> Fail<TOther>() => failure is null ? throw new InvalidOperationException("A successful flow has no failure.") : failure;
}

internal static class FlowLift
{
    public static Flow<T> Of<T>(this OneOf<T, PortError> result) => result.Match<Flow<T>>(value => value, error => error);

    public static Flow<T> Of<T>(this OneOf<T, SettingsMissing, PortError> result) =>
        result.Match<Flow<T>>(value => value, _ => new FlowFailure.MissingSettings(), error => error);

    /// <summary>A missing value (<see cref="NotFound"/>) is <see langword="null"/>, not a failure.</summary>
    public static Flow<T?> OfOptional<T>(this OneOf<T, NotFound, PortError> result)
        where T : class =>
        result.Match<Flow<T?>>(value => value, _ => (T?)null, error => error);

    /// <summary>A missing value (<see cref="NotFound"/>) is the failure <see cref="FlowFailure.MissingPlan"/>.</summary>
    public static Flow<T> OfRequired<T>(this OneOf<T, NotFound, PortError> result) =>
        result.Match<Flow<T>>(value => value, _ => new FlowFailure.MissingPlan(), error => error);
}
