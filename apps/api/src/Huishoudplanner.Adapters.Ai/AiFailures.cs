using System.ClientModel;
using System.Net;
using System.Text.Json;
using Anthropic.Exceptions;

namespace Huishoudplanner.Adapters.Ai;

/// <summary>The adapter raised this itself: the deterministic mock has no answer for the requested name.</summary>
internal sealed class ModelUnavailableException(string message) : Exception(message);

/// <summary>
/// What counts as an infrastructure failure of a model provider, and how it is described without leaking anything.
/// Everything else (a bug) propagates instead of being swallowed.
/// </summary>
internal static class AiFailures
{
    /// <summary>
    /// The named infrastructure-exception filter: transport failures, provider SDK failures, unreadable bodies, and a
    /// cancellation that is not the caller's (our own timeout, or an HTTP client timeout).
    /// </summary>
    public static bool IsInfrastructure(Exception exception, CancellationToken callerToken) => exception switch
    {
        OperationCanceledException => !callerToken.IsCancellationRequested,
        HttpRequestException or TimeoutException or IOException or JsonException or ClientResultException
            or AnthropicException or ModelUnavailableException or NoContentException => true,
        _ => false,
    };

    /// <summary>The HTTP status the provider answered with, if the failure carries one (also through inner exceptions).</summary>
    public static int? StatusOf(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AnthropicApiException api:
                    return (int)api.StatusCode;
                case ClientResultException { Status: > 0 } result:
                    return result.Status;
                case HttpRequestException { StatusCode: { } status }:
                    return (int)status;
                case HttpRequestException { Message: var message } when TryStatusInMessage(message, out var parsed):
                    return parsed;
                default:
                    break;
            }
        }

        return null;
    }

    public static bool IsTimeout(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException or OperationCanceledException)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryStatusInMessage(string message, out int status)
    {
        // OllamaSharp reports a failed request as an HttpRequestException without a StatusCode in some versions.
        status = 0;
        var marker = message.IndexOf('(');
        if (marker < 0)
        {
            return false;
        }

        var digits = new string(message.Skip(marker + 1).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out status) && Enum.IsDefined((HttpStatusCode)status);
    }
}
