namespace Huishoudplanner.Domain.Identity;

/// <summary>A person's role. Ordered by privilege: a higher role may do everything a lower one may.</summary>
public enum Role
{
    Member = 0,
    Planner = 1,
    Admin = 2,
}
