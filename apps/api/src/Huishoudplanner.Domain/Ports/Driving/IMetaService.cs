using Huishoudplanner.Domain.Limits;

namespace Huishoudplanner.Domain.Ports.Driving;

public interface IMetaService
{
    /// <summary>All limits and defaults the web app needs.</summary>
    HouseholdLimits GetLimits();
}
