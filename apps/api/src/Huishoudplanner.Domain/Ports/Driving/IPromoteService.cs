using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Promotion;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

public interface IPromoteService
{
    /// <summary>
    /// The suggestions to change a slot of the active plan (requirements 4.6), one per slot at most. Needs no actor and writes nothing. Without
    /// settings or an active plan the answer is an empty list, like the Node route.
    /// </summary>
    Task<OneOf<IReadOnlyList<PromoteSuggestion>, PortError>> GetSuggestionsAsync(CancellationToken cancellationToken);
}
