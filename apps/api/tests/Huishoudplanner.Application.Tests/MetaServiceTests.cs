using Huishoudplanner.Domain.Limits;

namespace Huishoudplanner.Application.Tests;

public class MetaServiceTests
{
    [Fact]
    public void Limits_are_the_domain_limits() =>
        new MetaService().GetLimits().Should().BeSameAs(HouseholdLimits.Current);
}
