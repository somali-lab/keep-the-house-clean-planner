namespace Huishoudplanner.Domain.Identity;

/// <summary>
/// What an identity adapter may look at, free of any HTTP type: the raw values of the profile and client headers
/// (<c>null</c> when the header is absent). A later bearer-token adapter adds its own field here.
/// </summary>
public sealed record ActorRequest(string? ProfileId, string? Client);
