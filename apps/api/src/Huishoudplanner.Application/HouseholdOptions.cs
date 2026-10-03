namespace Huishoudplanner.Application;

/// <summary>What the use cases need to know about the household. Filled by the composition root from configuration.</summary>
/// <param name="Timezone">IANA id of the household timezone (<c>TZ_APP</c>).</param>
public sealed record HouseholdOptions(string Timezone);
