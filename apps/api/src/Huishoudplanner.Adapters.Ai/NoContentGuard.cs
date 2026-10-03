using Microsoft.Extensions.AI;

namespace Huishoudplanner.Adapters.Ai;

/// <summary>The provider answered 200 with a body that holds no message (for example an empty <c>choices</c> array).</summary>
internal sealed class NoContentException : Exception
{
    public NoContentException()
        : base("The provider answered without a message")
    {
    }
}

/// <summary>
/// The OpenAI SDK (2.14) throws <see cref="ArgumentOutOfRangeException"/> while reading a completion whose
/// <c>choices</c> array is empty, which compatible servers do send. That is an answer without content, not a bug
/// of ours, so it is translated here, in the one place that knows the SDK, instead of widening the exception filter.
/// </summary>
internal sealed class NoContentGuard(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new NoContentException();
        }
    }
}
