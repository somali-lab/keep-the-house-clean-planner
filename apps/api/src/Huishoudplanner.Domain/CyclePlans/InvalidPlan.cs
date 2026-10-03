using Huishoudplanner.Domain.Planning;

namespace Huishoudplanner.Domain.CyclePlans;

/// <summary>
/// A plan breaks a hard rule, so it cannot be saved. Maps to <c>422 invalid_plan</c> (requirements section 8) and carries the whole
/// validation: the errors, the warnings and the summary a save would have returned.
/// </summary>
public sealed record InvalidPlan(PlanValidation Validation);
