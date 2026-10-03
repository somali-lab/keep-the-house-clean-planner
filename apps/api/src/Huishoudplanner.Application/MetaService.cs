using Huishoudplanner.Domain.Limits;
using Huishoudplanner.Domain.Ports.Driving;

namespace Huishoudplanner.Application;

public sealed class MetaService : IMetaService
{
    public HouseholdLimits GetLimits() => HouseholdLimits.Current;
}
