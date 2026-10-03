using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Promotion;
using Huishoudplanner.Domain.Settings;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

public interface IPromoteService
{
    /// <summary>
    /// The suggestions to change a slot of the active plan (requirements 4.6), one per slot at most. Needs no actor and writes nothing. Without
    /// settings or an active plan the answer is an empty list, like the Node route.
    /// </summary>
    Task<OneOf<IReadOnlyList<PromoteSuggestion>, PortError>> GetSuggestionsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Changes one slot of the ACTIVE plan to the suggested weekday (and person), validated like a slot save: a hard error is an
    /// <see cref="InvalidPlan"/> and nothing changes. A plan that is not the active one is a <c>409 plan_not_active</c>, a slot that is gone a
    /// <see cref="SlotNotFound"/>. The slot save is audited with <c>meta.promotedFrom</c> and the upcoming occurrences are synchronised in the
    /// same transaction with the system as source, exactly like saving the slots of the active plan.
    /// </summary>
    Task<OneOf<PlanSlotsSaved, SlotNotFound, ValidationErrors, InvalidPlan, ConflictError, PortError, SettingsMissing>> ApplyAsync(
        Actor actor, ApplyPromotionCommand command, CancellationToken cancellationToken);

    /// <summary>Remembers a dismissed suggestion (an earlier dismissal of the same target is replaced); audited as a settings update. Dismissing what is already stored writes nothing.</summary>
    Task<OneOf<Success, ValidationErrors, ConflictError, PortError, SettingsMissing>> DismissAsync(
        Actor actor, DismissedPromotion promotion, CancellationToken cancellationToken);
}
