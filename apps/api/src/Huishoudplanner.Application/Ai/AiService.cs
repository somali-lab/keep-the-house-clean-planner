using System.Globalization;
using System.Text.Json;
using Huishoudplanner.Application.CyclePlans;
using Huishoudplanner.Domain.Ai;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Planning;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Settings;
using OneOf;
using DomainChatOptions = Huishoudplanner.Domain.Ai.ChatOptions;

namespace Huishoudplanner.Application.Ai;

/// <summary>
/// The AI use cases (requirements 5; port of <c>domain/ai/proposals.ts</c>, <c>assist.ts</c> and <c>routes/ai.ts</c>). A model is never asked
/// inside a transaction: the answer is validated first and only a valid proposal is stored, together with its audit entry. Suggestions and
/// explanations store nothing. The model is chosen per call from the stored provider settings, so a settings change applies at once.
/// </summary>
public sealed class AiService(
    ForStoringTasks tasks,
    ForStoringUsers users,
    ForStoringRooms rooms,
    ForStoringSettings settings,
    ForStoringCyclePlans plans,
    ForSelectingAModel models,
    ForRunningTransactions transactions,
    ForRecordingAudit audit,
    TimeProvider time) : IAiService
{
    private const string SettingsKeyTaskIds = "taskIds";

    private readonly AiReferenceReader references = new(tasks, users, rooms);

    // ---- prompt information and connection test

    public async Task<OneOf<AiPromptInfo, SettingsMissing, PortError>> GetPromptInfoAsync(CancellationToken cancellationToken)
    {
        var read = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
        return read.Match<OneOf<AiPromptInfo, SettingsMissing, PortError>>(
            current => PromptBuilder.CodeInfo(current.AiPrompts, current.AiPromptTemplates),
            missing => missing,
            error => error);
    }

    public async Task<OneOf<Success, ValidationErrors, AiUnavailable, AiProviderFailure, PortError>> TestConnectionAsync(AiProviderSettings provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (SettingsRules.Validate(new SettingsPatch { AiProvider = provider }) is { } invalid)
        {
            return invalid;
        }

        var asked = await AskAsync(
            models.ChooseFor(provider),
            AiRequestNames.ConnectionTest,
            new BuiltPrompt(PromptBuilder.ConnectionTestSystem, PromptBuilder.ConnectionTestUser),
            PromptBuilder.ConnectionTestSchema,
            cancellationToken).ConfigureAwait(false);
        if (asked.TryPickT1(out var unavailable, out var rest))
        {
            return unavailable;
        }

        if (rest.TryPickT1(out var failure, out var raw))
        {
            return failure;
        }

        if (!ModelJson.Extract(raw).TryPickT0(out var answer, out _))
        {
            return new AiProviderFailure("The AI connection worked, but the test answer was not valid JSON");
        }

        return answer is { ValueKind: JsonValueKind.Object } && answer.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
            ? new Success()
            : new AiProviderFailure("The AI connection worked, but the test answer was not usable");
    }

    // ---- proposals

    public Task<OneOf<PlanProposal, NotFound, ValidationErrors, SettingsMissing, AiUnavailable, AiProviderFailure, AiInvalidPlan, ConflictError, PortError>> ProposePlanAsync(
        Actor actor, ProposePlanCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        CheckConstraints(command.Constraints, errors);
        if (command.TaskIds is { } ids)
        {
            if (ids.Count == 0)
            {
                errors[SettingsKeyTaskIds] = ["out_of_range"];
            }

            for (var i = 0; i < ids.Count; i++)
            {
                if (!CyclePlanRules.IsId(ids[i]))
                {
                    errors[string.Create(CultureInfo.InvariantCulture, $"{SettingsKeyTaskIds}[{i}]")] = ["invalid_object_id"];
                }
            }
        }

        return errors.Count > 0
            ? Task.FromResult<OneOf<PlanProposal, NotFound, ValidationErrors, SettingsMissing, AiUnavailable, AiProviderFailure, AiInvalidPlan, ConflictError, PortError>>(new ValidationErrors(errors))
            : ProposeAsync(actor, PlanModes.Propose, command.TaskIds?.Select(id => id.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal), null, command.Constraints, cancellationToken);
    }

    public async Task<OneOf<PlanProposal, NotFound, ValidationErrors, SettingsMissing, AiUnavailable, AiProviderFailure, AiInvalidPlan, ConflictError, PortError>> RebalancePlanAsync(
        Actor actor, RebalancePlanCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        CheckConstraints(command.Constraints, errors);
        if (!CyclePlanRules.IsId(command.PlanId))
        {
            errors["planId"] = ["invalid_object_id"];
        }

        if (errors.Count > 0)
        {
            return new ValidationErrors(errors);
        }

        var found = await plans.FindAsync(command.PlanId.ToLowerInvariant(), cancellationToken).ConfigureAwait(false);
        if (found.TryPickT1(out var notFound, out var rest))
        {
            return notFound;
        }

        if (rest.TryPickT1(out var error, out var basePlan))
        {
            return error;
        }

        return await ProposeAsync(actor, PlanModes.Rebalance, null, basePlan, command.Constraints, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OneOf<PlanProposal, NotFound, ValidationErrors, SettingsMissing, AiUnavailable, AiProviderFailure, AiInvalidPlan, ConflictError, PortError>> ProposeAsync(
        Actor actor, string mode, HashSet<string>? selection, CyclePlan? basePlan, string? constraints, CancellationToken cancellationToken)
    {
        var read = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (read.TryPickT1(out var missing, out var readRest))
        {
            return missing;
        }

        if (readRest.TryPickT1(out var settingsError, out var current))
        {
            return settingsError;
        }

        var data = await ReadDataAsync(cancellationToken).ConfigureAwait(false);
        if (data.TryPickT1(out var dataError, out var household))
        {
            return dataError;
        }

        var activeTasks = household.Tasks.Where(t => t.Active).ToList();
        var chosen = activeTasks;
        if (selection is not null)
        {
            chosen = [.. activeTasks.Where(t => selection.Contains(t.Id))];
            if (chosen.Count != selection.Count)
            {
                return ValidationErrors.For(SettingsKeyTaskIds, "unknown_task");
            }
        }

        if (chosen.Count == 0)
        {
            return ValidationErrors.For(SettingsKeyTaskIds, "no_tasks");
        }

        var roomName = household.Rooms.ToDictionary(r => r.Id, r => r.Name, StringComparer.Ordinal);
        var interval = current.Intervals.ToDictionary(i => i.Key, StringComparer.Ordinal);
        var allowedTaskIds = chosen.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var activeUsers = household.Users.Where(u => u.Active).ToList();
        var basePayload = new PlanPromptPayload(
            mode,
            [.. chosen.Select(t => new PromptTask(
                t.Id,
                t.Name,
                roomName.GetValueOrDefault(t.RoomId),
                t.IntervalKey,
                interval.TryGetValue(t.IntervalKey, out var known) ? known.Label : t.IntervalKey,
                known?.PerCycle,
                known?.PeriodDays ?? 0,
                t.DurationMinutes))],
            [.. activeUsers.Select(u => new PromptUser(
                u.Id,
                u.Name,
                u.UnavailableWeekdays,
                new PromptMinutes(u.DailyBudgetMinutes.Weekday, u.DailyBudgetMinutes.Weekend),
                new PromptMinutes(u.MaxDailyMinutes.Weekday, u.MaxDailyMinutes.Weekend)))],
            basePlan is null ? null : [.. basePlan.Slots.Select(s => new PromptSlot(s.TaskId, s.WeekIndex, s.Weekday, s.AssigneeId))],
            string.IsNullOrEmpty(constraints?.Trim()) ? null : constraints!.Trim());
        var schema = PromptBuilder.PlanSchema(basePayload.Tasks.Select(t => t.Id), basePayload.Users.Select(u => u.Id));
        var validation = new PlanReferences(
            [.. household.Tasks.Select(t => new PlanTask(t.Id, t.Name, t.IntervalKey, t.DurationMinutes, t.Active))],
            [.. household.Users.Select(u => new PlanUser(
                u.Id,
                u.Name,
                u.Active,
                u.UnavailableWeekdays,
                new DayMinutes(u.DailyBudgetMinutes.Weekday, u.DailyBudgetMinutes.Weekend),
                new DayMinutes(u.MaxDailyMinutes.Weekday, u.MaxDailyMinutes.Weekend)))],
            current.Intervals);
        var model = models.ChooseFor(current.AiProvider);
        var template = mode == PlanModes.Rebalance ? current.AiPromptTemplates?.PlanRebalance : current.AiPromptTemplates?.PlanProposal;
        var custom = current.AiPromptTemplates is not null
            ? null
            : OrDefault(mode == PlanModes.Rebalance ? current.AiPrompts?.PlanRebalance : current.AiPrompts?.PlanProposal,
                mode == PlanModes.Rebalance ? SettingsDefaults.AiPrompts.PlanRebalance : SettingsDefaults.AiPrompts.PlanProposal);

        async Task<OneOf<Accepted, Rejected, AiUnavailable, AiProviderFailure>> TryOnceAsync(IReadOnlyList<string>? previousErrors)
        {
            var payload = previousErrors is null ? basePayload : basePayload with { PreviousErrors = previousErrors };
            var asked = await AskAsync(model, AiRequestNames.PlanProposal, PromptBuilder.BuildPlanPrompt(payload, schema, custom, template), schema, cancellationToken).ConfigureAwait(false);
            if (asked.TryPickT1(out var unavailable, out var askedRest))
            {
                return unavailable;
            }

            if (askedRest.TryPickT1(out var failure, out var raw))
            {
                return failure;
            }

            if (!AiOutputs.ParsePlan(raw).TryPickT0(out var proposed, out var parseErrors))
            {
                return new Rejected(parseErrors);
            }

            var outside = proposed.Slots
                .Select((slot, index) => (slot, index))
                .Where(x => !allowedTaskIds.Contains(x.slot.TaskId))
                .Select(x => ProposalErrors.NotInSelection(x.index, x.slot.TaskId));
            var proposedErrors = outside.Concat(validation.Validate(proposed.Slots).Errors.Select(ProposalErrors.Describe)).ToList();
            if (proposedErrors.Count > 0)
            {
                return new Rejected(proposedErrors);
            }

            var completed = PlanCompleter.CompleteRequiredOccurrences(basePayload, proposed.Slots);
            var unassigned = completed
                .Select((slot, index) => (slot, index))
                .Where(x => x.slot.AssigneeId is null)
                .Select(x => ProposalErrors.NoAssignee(x.index, x.slot.WeekIndex, x.slot.Weekday))
                .ToList();
            if (unassigned.Count > 0)
            {
                return new Rejected(unassigned);
            }

            var result = validation.Validate(completed);
            return result.Errors.Count > 0
                ? new Rejected([.. result.Errors.Select(ProposalErrors.Describe)])
                : new Accepted(completed, proposed.Rationale, result.Warnings);
        }

        var attempt = await TryOnceAsync(null).ConfigureAwait(false);
        if (attempt.TryPickT1(out var firstRejection, out _))
        {
            attempt = await TryOnceAsync(firstRejection.Errors).ConfigureAwait(false);
        }

        if (attempt.TryPickT2(out var unavailableModel, out var attemptRest))
        {
            return unavailableModel;
        }

        if (attemptRest.TryPickT2(out var providerFailure, out var attemptResult))
        {
            return providerFailure;
        }

        if (attemptResult.TryPickT1(out var rejected, out var accepted))
        {
            return new AiInvalidPlan(rejected.Errors);
        }

        var stored = await StoreProposalAsync(actor, mode, basePlan, accepted, cancellationToken).ConfigureAwait(false);
        return stored.Match<OneOf<PlanProposal, NotFound, ValidationErrors, SettingsMissing, AiUnavailable, AiProviderFailure, AiInvalidPlan, ConflictError, PortError>>(
            proposal => proposal,
            conflict => conflict,
            error => error);
    }

    private async Task<OneOf<PlanProposal, ConflictError, PortError>> StoreProposalAsync(
        Actor actor, string mode, CyclePlan? basePlan, Accepted accepted, CancellationToken cancellationToken)
    {
        var proposalId = Guid.NewGuid().ToString();
        var now = time.GetUtcNow();
        var name = basePlan is not null
            ? $"{basePlan.Name} (herbalanceerd)"
            : $"AI-voorstel {now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        var proposal = new NewPlanProposal(
            name,
            CyclePlanSlots.Sort(accepted.Slots),
            basePlan?.WeekThemes ?? CyclePlanRules.EmptyWeekThemes,
            proposalId,
            accepted.Rationale,
            now);
        var aiActor = new AuditActor(actor.ActorId, AuditSource.Ai);

        var ran = await transactions.RunAsync(
            async ct =>
            {
                var inserted = await plans.InsertProposalAsync(proposal, ct).ConfigureAwait(false);
                if (inserted.TryPickT1(out var insertError, out var plan))
                {
                    return TransactionOutcome.Abort<OneOf<PlanProposal, PortError>>(insertError);
                }

                var meta = basePlan is null
                    ? AuditObject.Of(("proposalId", AuditValue.FromString(proposalId)), ("mode", AuditValue.FromString(mode)))
                    : AuditObject.Of(("proposalId", AuditValue.FromString(proposalId)), ("mode", AuditValue.FromString(mode)), ("basePlanId", new AuditObjectId(basePlan.Id)));
                var recorded = await audit.RecordAsync(
                    ChangeSet.Between(null, CyclePlanAudit.Fields(plan)).ToEntry(aiActor, AuditEntity.CyclePlan, plan.Id, AuditAction.Create, meta),
                    ct).ConfigureAwait(false);
                return recorded.Match(
                    _ => TransactionOutcome.Commit<OneOf<PlanProposal, PortError>>(new PlanProposal(plan, proposalId, accepted.Warnings, accepted.Rationale)),
                    error => TransactionOutcome.Abort<OneOf<PlanProposal, PortError>>(error));
            },
            cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<PlanProposal, ConflictError, PortError>>(
            outcome => outcome.Match<OneOf<PlanProposal, ConflictError, PortError>>(proposalResult => proposalResult, error => error),
            conflict => conflict,
            error => error);
    }

    // ---- suggestions and explanations

    public async Task<OneOf<IReadOnlyList<TaskSuggestion>, NotFound, ValidationErrors, SettingsMissing, AiUnavailable, AiProviderFailure, AiInvalidResponse, PortError>> SuggestTasksAsync(
        string roomId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roomId);
        if (!CyclePlanRules.IsId(roomId))
        {
            return ValidationErrors.For("roomId", "invalid_object_id");
        }

        var id = roomId.ToLowerInvariant();
        var read = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (read.TryPickT1(out var missing, out var readRest))
        {
            return missing;
        }

        if (readRest.TryPickT1(out var settingsError, out var current))
        {
            return settingsError;
        }

        var data = await ReadDataAsync(cancellationToken).ConfigureAwait(false);
        if (data.TryPickT1(out var dataError, out var household))
        {
            return dataError;
        }

        var room = household.Rooms.FirstOrDefault(r => r.Id == id);
        if (room is null)
        {
            return new NotFound();
        }

        var roomName = household.Rooms.ToDictionary(r => r.Id, r => r.Name, StringComparer.Ordinal);
        var inRoom = household.Tasks.Where(t => t.RoomId == id).ToList();
        var payload = new TaskSuggestionPayload(
            room.Name,
            [.. inRoom.Select(t => new ExistingTaskInfo(t.Name, t.IntervalKey, t.DurationMinutes))],
            [.. household.Tasks.Where(t => t.RoomId != id && t.Active).Select(t => new OtherTaskInfo(roomName.GetValueOrDefault(t.RoomId), t.Name))],
            [.. current.Intervals.Select(i => new PromptInterval(i.Key, i.Label, i.PeriodDays))]);
        var prompt = PromptBuilder.BuildTaskSuggestionsPrompt(
            payload,
            current.AiPromptTemplates is not null ? null : OrDefault(current.AiPrompts?.TaskSuggestions, SettingsDefaults.AiPrompts.TaskSuggestions),
            current.AiPromptTemplates?.TaskSuggestions);

        var asked = await AskAsync(models.ChooseFor(current.AiProvider), AiRequestNames.TaskSuggestions, prompt, PromptBuilder.TaskSuggestionsSchema, cancellationToken).ConfigureAwait(false);
        if (asked.TryPickT1(out var unavailable, out var askedRest))
        {
            return unavailable;
        }

        if (askedRest.TryPickT1(out var failure, out var raw))
        {
            return failure;
        }

        if (AiOutputs.ParseTaskSuggestions(raw).TryPickT1(out var invalid, out var suggestions))
        {
            return invalid;
        }

        var filtered = AiOutputs.FilterSuggestions(suggestions, [.. current.Intervals.Select(i => i.Key)], inRoom.Select(t => t.Name));
        return OneOf<IReadOnlyList<TaskSuggestion>, NotFound, ValidationErrors, SettingsMissing, AiUnavailable, AiProviderFailure, AiInvalidResponse, PortError>.FromT0(filtered);
    }

    public async Task<OneOf<IReadOnlyList<string>, NotFound, ValidationErrors, SettingsMissing, AiUnavailable, AiProviderFailure, AiInvalidResponse, PortError>> ExplainPlanAsync(
        string planId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(planId);
        if (!CyclePlanRules.IsId(planId))
        {
            return ValidationErrors.For("planId", "invalid_object_id");
        }

        var found = await plans.FindAsync(planId.ToLowerInvariant(), cancellationToken).ConfigureAwait(false);
        if (found.TryPickT1(out var notFound, out var foundRest))
        {
            return notFound;
        }

        if (foundRest.TryPickT1(out var findError, out var plan))
        {
            return findError;
        }

        var data = await ReadDataAsync(cancellationToken).ConfigureAwait(false);
        if (data.TryPickT1(out var dataError, out var household))
        {
            return dataError;
        }

        var read = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (read.TryPickT1(out var missing, out var readRest))
        {
            return missing;
        }

        if (readRest.TryPickT1(out var settingsError, out var current))
        {
            return settingsError;
        }

        var taskById = household.Tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var userName = household.Users.ToDictionary(u => u.Id, u => u.Name, StringComparer.Ordinal);
        var payload = new ExplanationPayload(
            plan.Name,
            [.. household.Users.Where(u => u.Active).Select(u => new ExplanationUser(
                u.Id,
                u.Name,
                new PromptMinutes(u.DailyBudgetMinutes.Weekday, u.DailyBudgetMinutes.Weekend),
                new PromptMinutes(u.MaxDailyMinutes.Weekday, u.MaxDailyMinutes.Weekend)))],
            [.. plan.Slots.Select(s =>
            {
                taskById.TryGetValue(s.TaskId, out var task);
                return new ExplanationSlot(
                    task?.Name ?? s.TaskId,
                    s.WeekIndex,
                    s.Weekday,
                    s.AssigneeId is { } assignee ? userName.GetValueOrDefault(assignee) : null,
                    task?.DurationMinutes ?? 0);
            })]);
        var prompt = PromptBuilder.BuildExplanationPrompt(
            payload,
            current.AiPromptTemplates is not null ? null : OrDefault(current.AiPrompts?.PlanExplanation, SettingsDefaults.AiPrompts.PlanExplanation),
            current.AiPromptTemplates?.PlanExplanation);

        var asked = await AskAsync(models.ChooseFor(current.AiProvider), AiRequestNames.PlanExplanation, prompt, PromptBuilder.ExplanationSchema, cancellationToken).ConfigureAwait(false);
        if (asked.TryPickT1(out var unavailable, out var askedRest))
        {
            return unavailable;
        }

        if (askedRest.TryPickT1(out var failure, out var raw))
        {
            return failure;
        }

        return AiOutputs.ParseExplanation(raw).Match<OneOf<IReadOnlyList<string>, NotFound, ValidationErrors, SettingsMissing, AiUnavailable, AiProviderFailure, AiInvalidResponse, PortError>>(
            rationale => OneOf<IReadOnlyList<string>, NotFound, ValidationErrors, SettingsMissing, AiUnavailable, AiProviderFailure, AiInvalidResponse, PortError>.FromT0(rationale),
            invalid => invalid);
    }

    // ---- helpers

    private static string OrDefault(string? configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();

    private static void CheckConstraints(string? constraints, Dictionary<string, string[]> errors)
    {
        if (constraints is not null && constraints.Trim().Length > Domain.Limits.HouseholdLimits.Current.Ai.ConstraintsMaxLength)
        {
            errors["constraints"] = ["out_of_range"];
        }
    }

    private async Task<OneOf<Household, PortError>> ReadDataAsync(CancellationToken cancellationToken)
    {
        var allTasks = await references.ReadTasksAsync(cancellationToken).ConfigureAwait(false);
        if (allTasks.TryPickT1(out var taskError, out var taskList))
        {
            return taskError;
        }

        var allUsers = await references.ReadUsersAsync(cancellationToken).ConfigureAwait(false);
        if (allUsers.TryPickT1(out var userError, out var userList))
        {
            return userError;
        }

        var allRooms = await references.ReadRoomsAsync(cancellationToken).ConfigureAwait(false);
        return allRooms.Match<OneOf<Household, PortError>>(roomList => new Household(taskList, userList, roomList), error => error);
    }

    private static async Task<OneOf<string, AiUnavailable, AiProviderFailure>> AskAsync(
        ForChattingWithAModel model, string name, BuiltPrompt prompt, string schemaJson, CancellationToken cancellationToken)
    {
        using var schema = JsonDocument.Parse(schemaJson);
        var request = new ChatRequest(prompt.System, prompt.User, new DomainChatOptions(Name: name, ResponseSchema: schema.RootElement.Clone()));
        var reply = await model.ChatAsync(request, cancellationToken).ConfigureAwait(false);
        return reply.Match<OneOf<string, AiUnavailable, AiProviderFailure>>(
            chat => chat.Text,
            unavailable => unavailable,
            error => new AiProviderFailure(error.Message));
    }

    private sealed record Household(
        IReadOnlyList<Domain.Tasks.HouseholdTask> Tasks,
        IReadOnlyList<Domain.Users.User> Users,
        IReadOnlyList<Domain.Rooms.Room> Rooms);

    private sealed record Accepted(IReadOnlyList<CyclePlanSlot> Slots, IReadOnlyList<string> Rationale, IReadOnlyList<PlanIssue> Warnings);

    private sealed record Rejected(IReadOnlyList<string> Errors);
}
