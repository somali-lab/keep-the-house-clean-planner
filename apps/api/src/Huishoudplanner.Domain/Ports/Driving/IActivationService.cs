using Huishoudplanner.Domain.Activation;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>
/// The activation of a cycle plan (requirements 4.3, ADR-0008 as amended): a read-only preview of what activating would change, and the
/// activation itself, which confirms that preview. Who may call it (planners) is decided by the driving adapter.
/// </summary>
public interface IActivationService
{
    /// <summary>
    /// What activating the plan would change, with the token of the state it was computed from. Writes and audits nothing.
    /// A malformed id is a <see cref="ValidationErrors"/>, an unknown plan <see cref="NotFound"/>.
    /// </summary>
    Task<OneOf<ActivationPreview, NotFound, ValidationErrors, SettingsMissing, PortError>> PreviewAsync(string planId, CancellationToken cancellationToken);

    /// <summary>
    /// Activates the plan in one transaction: the shared guard document is written, the preview is recomputed and its token compared (a
    /// different one is the <see cref="ConflictError"/> <c>stale_activation_preview</c> and nothing is written), the other active plans are
    /// deactivated, the plan is activated (an AI draft stops being a draft), and the upcoming occurrences are replaced with the system as
    /// source. Two concurrent activations conflict on the guard: one wins and the other sees the stale preview.
    /// </summary>
    Task<OneOf<PlanActivated, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>> ActivateAsync(
        Actor actor, string planId, string previewToken, CancellationToken cancellationToken);
}
