using Microsoft.Extensions.Options;

namespace Huishoudplanner.Host.Configuration;

/// <summary>
/// Turns the issues found by <see cref="AppOptionsBinder"/> into an options validation failure.
/// The messages name variables only, so the resulting exception never echoes a configured value.
/// </summary>
public sealed class AppOptionsValidator : IValidateOptions<AppOptions>
{
    public ValidateOptionsResult Validate(string? name, AppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Issues.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(options.Issues);
    }
}
