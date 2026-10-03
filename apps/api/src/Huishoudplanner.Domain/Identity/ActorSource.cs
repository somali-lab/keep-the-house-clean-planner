namespace Huishoudplanner.Domain.Identity;

/// <summary>Where a change came from: the web interface (<c>X-Client: web</c>) or any other client.</summary>
public enum ActorSource
{
    Ui,
    Api,
}
