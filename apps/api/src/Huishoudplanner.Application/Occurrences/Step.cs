using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Application.Occurrences;

/// <summary>Why a step of an occurrence use case stopped. Never leaves the use case: the public methods turn it into the variants of their port.</summary>
internal readonly record struct Refusal(OneOf<NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError> Value)
{
    public static implicit operator Refusal(NotFound value) => new(value);

    public static implicit operator Refusal(ValidationErrors value) => new(value);

    public static implicit operator Refusal(ConflictError value) => new(value);

    public static implicit operator Refusal(SettingsMissing value) => new(value);

    public static implicit operator Refusal(PortError value) => new(value);
}

/// <summary>
/// A step result that is either a value or a <see cref="Refusal"/>, so that a use case reads top to bottom instead of nesting a
/// <c>TryPickT</c> per port call. A private convenience of the occurrence service; the ports keep returning <c>OneOf</c> values.
/// </summary>
internal readonly struct Step<T>
{
    private readonly T? value;

    private readonly Refusal? refusal;

    private Step(T? value, Refusal? refusal)
    {
        this.value = value;
        this.refusal = refusal;
    }

    public bool IsSuccess => refusal is null;

    public static implicit operator Step<T>(T value) => new(value, null);

    public static implicit operator Step<T>(Refusal refusal) => new(default, refusal);

    public static implicit operator Step<T>(NotFound value) => new(default, new Refusal(value));

    public static implicit operator Step<T>(ValidationErrors value) => new(default, new Refusal(value));

    public static implicit operator Step<T>(ConflictError value) => new(default, new Refusal(value));

    public static implicit operator Step<T>(SettingsMissing value) => new(default, new Refusal(value));

    public static implicit operator Step<T>(PortError value) => new(default, new Refusal(value));

    public bool TryGet(out T result, out Refusal failure)
    {
        result = value!;
        failure = refusal.GetValueOrDefault();
        return refusal is null;
    }

    public OneOf<TOut, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError> ToOneOf<TOut>(Func<T, TOut> map) =>
        refusal is { } failure
            ? failure.Value.Match<OneOf<TOut, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>>(
                a => a, b => b, c => c, d => d, e => e)
            : map(value!);
}

internal static class StepLift
{
    public static Step<T> AsStep<T>(this OneOf<T, PortError> result) => result.Match<Step<T>>(value => value, error => error);

    public static Step<T> AsStep<T>(this OneOf<T, NotFound, PortError> result) =>
        result.Match<Step<T>>(value => value, notFound => notFound, error => error);

    public static Step<T> AsStep<T>(this OneOf<T, SettingsMissing, PortError> result) =>
        result.Match<Step<T>>(value => value, missing => missing, error => error);

    /// <summary>A missing value is <see langword="null"/>, not a failure.</summary>
    public static Step<T?> AsOptional<T>(this OneOf<T, NotFound, PortError> result)
        where T : class =>
        result.Match<Step<T?>>(value => value, _ => (T?)null, error => error);
}
