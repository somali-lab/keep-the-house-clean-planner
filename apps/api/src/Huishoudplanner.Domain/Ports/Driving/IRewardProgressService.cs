using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Rewards;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>The reward meter as the API sees it (requirements 4.12): how far one person is towards the goal of the current week or cycle. A pure read.</summary>
public interface IRewardProgressService
{
    /// <summary>
    /// The progress of one person in the week or cycle of today in the household timezone. Earned points are the ledger entries of executions and
    /// bonuses dated in the period, so a redemption never lowers them; the goal is the explicit goal of the settings, else the points of the work
    /// planned for the person as its owner. A <c>personId</c> that is no id is a validation error.
    /// </summary>
    Task<OneOf<RewardProgress, ValidationErrors, SettingsMissing, PortError>> ProgressAsync(RewardProgressRequest request, CancellationToken cancellationToken);
}
