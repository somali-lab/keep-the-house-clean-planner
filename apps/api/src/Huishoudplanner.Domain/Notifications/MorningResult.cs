namespace Huishoudplanner.Domain.Notifications;

/// <summary>How a morning notification run ended. <see cref="Error"/> is a read that failed before anything was sent.</summary>
public enum MorningStatus
{
    /// <summary>No notification channel is configured: nothing was read or sent.</summary>
    Disabled,

    /// <summary>The run went through every active person; deliveries that failed are counted in <see cref="MorningResult.Failed"/>.</summary>
    Done,

    /// <summary>The data for the messages could not be read; nothing was sent.</summary>
    Error,
}

/// <summary>What a morning run did (<c>MorningResult</c> of the Node server): the household day, the messages sent and failed, and the people with nothing to report.</summary>
public sealed record MorningResult(MorningStatus Status, DateOnly? Date, int Sent, int Failed, int Quiet);
