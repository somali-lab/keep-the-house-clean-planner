using System.Diagnostics;
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
/// Only an exception thrown from inside the OpenAI assembly counts; one raised anywhere else still surfaces as a bug.
/// </summary>
internal sealed class NoContentGuard(IChatClient inner) : DelegatingChatClient(inner)
{
    private static readonly System.Reflection.Assembly OpenAiAssembly = typeof(OpenAI.Chat.ChatCompletion).Assembly;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException ex) when (ThrownByOpenAi(ex))
        {
            throw new NoContentException();
        }
    }

    private static bool ThrownByOpenAi(Exception exception)
    {
        // The throw site is the top frame; framework collection frames sit above the SDK method that indexed the list.
        foreach (var frame in new StackTrace(exception).GetFrames())
        {
            var assembly = frame.GetMethod()?.DeclaringType?.Assembly;
            if (assembly == OpenAiAssembly)
            {
                return true;
            }

            if (assembly is not null && assembly != typeof(List<>).Assembly)
            {
                return false;
            }
        }

        return false;
    }
}
