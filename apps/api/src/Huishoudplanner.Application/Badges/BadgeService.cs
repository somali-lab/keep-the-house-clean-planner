using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Limits;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Tasks;
using OneOf;

namespace Huishoudplanner.Application.Badges;

/// <summary>
/// The badge use cases (requirements 4.13, ADR-0014): definitions, examples, the awards and the progress of a person. Port of <c>routes/badges.ts</c>,
/// <c>domain/badges.ts</c> and <c>data/badges.ts</c>. Every state change runs in one transaction together with its audit entry; a change that changes
/// nothing writes and audits nothing (ADR-0004). After a change that can alter awards the awards are evaluated again in a transaction of their own,
/// like <c>reconcileBadgesSafely</c> of the Node server: a failure there is logged and never fails the request.
/// </summary>
public sealed class BadgeService(
    ForStoringBadges badges,
    ForStoringTasks tasks,
    ForReadingBadgeEvidence evidence,
    ForStoringBadgeAwards awards,
    IBadgeAwardService evaluation,
    ForRunningTransactions transactions,
    ForRecordingAudit audit,
    TimeProvider time) : IBadgeService
{
    private const int TaskPageSize = TaskListQuery.MaxLimit;

    private static BadgeLimits Limits => HouseholdLimits.Current.Badges;

    // ---- reads

    public async Task<OneOf<BadgeList, ValidationErrors, PortError>> ListAsync(bool? active, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var take = limit ?? BadgeListQuery.DefaultLimit;
        if (take is < 1 or > BadgeListQuery.MaxLimit)
        {
            return ValidationErrors.For("limit", "must be a whole number from 1 to " + BadgeListQuery.MaxLimit);
        }

        BadgeCursor? after = null;
        if (cursor is not null)
        {
            if (!BadgeCursor.TryDecode(cursor, out var decoded))
            {
                return ValidationErrors.For("cursor", "invalid_cursor");
            }

            after = decoded;
        }

        var found = await badges.ListAsync(active, after, take + 1, cancellationToken).ConfigureAwait(false);
        return found.Match<OneOf<BadgeList, ValidationErrors, PortError>>(
            items => items.Count > take
                ? new BadgeList([.. items.Take(take)], BadgeCursor.After(items[take - 1]).Encode())
                : new BadgeList(items, null),
            error => error);
    }

    public async Task<OneOf<BadgeAwardList, ValidationErrors, PortError>> AwardsAsync(string? personId, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var take = limit ?? BadgeAwardQuery.DefaultLimit;
        if (take is < 1 or > BadgeAwardQuery.MaxLimit)
        {
            errors["limit"] = ["must be a whole number from 1 to " + BadgeAwardQuery.MaxLimit];
        }

        if (personId is not null && !BadgeIds.IsValid(personId))
        {
            errors["personId"] = ["invalid_object_id"];
        }

        BadgeAwardCursor? after = null;
        if (cursor is not null)
        {
            if (BadgeAwardCursor.TryDecode(cursor, out var decoded))
            {
                after = decoded;
            }
            else
            {
                errors["cursor"] = ["invalid_cursor"];
            }
        }

        if (errors.Count > 0)
        {
            return new ValidationErrors(errors);
        }

        var query = new BadgeAwardQuery(personId?.ToLowerInvariant(), take, after);
        var found = await awards.ListAsync(query, take + 1, cancellationToken).ConfigureAwait(false);
        return found.Match<OneOf<BadgeAwardList, ValidationErrors, PortError>>(
            items => items.Count > take
                ? new BadgeAwardList([.. items.Take(take)], BadgeAwardCursor.After(items[take - 1]).Encode())
                : new BadgeAwardList(items, null),
            error => error);
    }

    public async Task<OneOf<BadgeProgress, ValidationErrors, PortError>> ProgressAsync(string personId, CancellationToken cancellationToken)
    {
        if (!BadgeIds.IsValid(personId))
        {
            return ValidationErrors.For("personId", "invalid_object_id");
        }

        var person = personId.ToLowerInvariant();
        var read = await badges.ListAllAsync(true, cancellationToken).ConfigureAwait(false);
        if (read.TryPickT1(out var badgesFailure, out var active))
        {
            return badgesFailure;
        }

        // Executions are only read when an active badge counts them.
        IReadOnlyList<BadgeExecution> executions = [];
        if (active.Any(b => b.Rule.Type != BadgeRuleType.OnTimeWeeks))
        {
            var found = await evidence.FindCreditedExecutionsAsync([person], cancellationToken).ConfigureAwait(false);
            if (found.TryPickT1(out var failure, out var credited))
            {
                return failure;
            }

            executions = [.. credited.Select(c => c.Execution)];
        }

        IReadOnlyList<DateTimeOffset> onTime = [];
        if (active.Any(b => b.Rule.Type == BadgeRuleType.OnTimeWeeks))
        {
            var found = await evidence.FindOnTimeWeeksAsync([person], cancellationToken).ConfigureAwait(false);
            if (found.TryPickT1(out var failure, out var weeks))
            {
                return failure;
            }

            onTime = [.. weeks.Select(w => w.Date)];
        }

        return new BadgeProgress(
            person,
            [.. active.Select(badge =>
            {
                var outcome = BadgeRules.Evaluate(badge.Rule, executions, onTime);
                return new BadgeProgressItem(badge.Id, outcome.Current, badge.Rule.Threshold, outcome.AwardedAt);
            })]);
    }

    public async Task<OneOf<BadgeImageData, NotFound, PortError>> ImageAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        return BadgeIds.IsValid(id) ? await badges.FindImageAsync(id.ToLowerInvariant(), cancellationToken).ConfigureAwait(false) : new NotFound();
    }

    // ---- create

    public async Task<OneOf<Badge, ValidationErrors, BadgeLimitReached, ConflictError, PortError>> CreateAsync(Actor actor, CreateBadgeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = BadgeValidation.CheckName(command.Name, Limits, errors);
        var description = BadgeValidation.CheckDescription(command.Description, Limits, errors);
        var rule = BadgeValidation.CheckRule(command.Rule, Limits, errors);
        var image = BadgeValidation.CheckImage(command.Image, Limits, errors);
        if (errors.Count > 0)
        {
            return new ValidationErrors(errors);
        }

        var ran = await transactions.RunAsync(ct => CreateInTransactionAsync(actor, new NewBadge(name!, description!, rule!, command.Active ?? true, null, image, Now()), ct), cancellationToken).ConfigureAwait(false);
        if (!ran.TryPickT0(out var inner, out var runFailure))
        {
            return runFailure.Match<OneOf<Badge, ValidationErrors, BadgeLimitReached, ConflictError, PortError>>(conflict => conflict, error => error);
        }

        if (!inner.TryPickT0(out var created, out var failure))
        {
            return failure.Match<OneOf<Badge, ValidationErrors, BadgeLimitReached, ConflictError, PortError>>(invalid => invalid, limit => limit, error => error);
        }

        await evaluation.ReconcileSafelyAsync(AuditActor.From(actor), BadgeEvalTrigger.Badge, null, cancellationToken).ConfigureAwait(false);
        return created;
    }

    private async Task<TransactionOutcome<OneOf<Badge, ValidationErrors, BadgeLimitReached, PortError>>> CreateInTransactionAsync(Actor actor, NewBadge badge, CancellationToken ct)
    {
        static TransactionOutcome<OneOf<Badge, ValidationErrors, BadgeLimitReached, PortError>> Abort(OneOf<Badge, ValidationErrors, BadgeLimitReached, PortError> value) =>
            TransactionOutcome.Abort(value);

        var counted = await badges.CountAsync(ct).ConfigureAwait(false);
        if (counted.TryPickT1(out var countFailure, out var count))
        {
            return Abort(countFailure);
        }

        if (count >= Limits.MaxBadges)
        {
            return Abort(new BadgeLimitReached(Limits.MaxBadges));
        }

        var resolved = await ExistingTasksAsync(badge.Rule, ct).ConfigureAwait(false);
        if (resolved.TryPickT1(out var resolveFailure, out var rule))
        {
            return Abort(resolveFailure.Match<OneOf<Badge, ValidationErrors, BadgeLimitReached, PortError>>(invalid => invalid, error => error));
        }

        var inserted = await badges.InsertAsync(badge with { Rule = rule }, ct).ConfigureAwait(false);
        if (inserted.TryPickT1(out var insertFailure, out var stored))
        {
            return Abort(insertFailure);
        }

        var recorded = await audit.RecordAsync(BadgeAudit.ForCreate(AuditActor.From(actor), stored), ct).ConfigureAwait(false);
        return recorded.Match(
            _ => TransactionOutcome.Commit<OneOf<Badge, ValidationErrors, BadgeLimitReached, PortError>>(stored),
            error => Abort(error));
    }

    /// <summary>
    /// The rule with the tasks that do not exist dropped (a deleted one); a rule that named tasks and names none that exist is refused, because an
    /// empty list would silently count every task.
    /// </summary>
    private async Task<OneOf<BadgeRule, OneOf<ValidationErrors, PortError>>> ExistingTasksAsync(BadgeRule rule, CancellationToken ct)
    {
        if (rule.Type == BadgeRuleType.OnTimeWeeks || rule.TaskIds.Count == 0)
        {
            return rule;
        }

        var found = await tasks.FindManyAsync(rule.TaskIds, ct).ConfigureAwait(false);
        if (found.TryPickT1(out var failure, out var existing))
        {
            return OneOf<ValidationErrors, PortError>.FromT1(failure);
        }

        var known = existing.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var kept = rule.TaskIds.Where(known.Contains).ToList();
        return kept.Count == 0
            ? OneOf<ValidationErrors, PortError>.FromT0(ValidationErrors.For("rule.taskIds", BadgeValidation.UnknownTask))
            : rule with { TaskIds = kept };
    }

    // ---- update

    public async Task<OneOf<Badge, NotFound, ValidationErrors, ConflictError, PortError>> UpdateAsync(Actor actor, string id, BadgePatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(patch);
        if (!BadgeIds.IsValid(id))
        {
            return ValidationErrors.For("id", "invalid_object_id");
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = patch.Name is null ? null : BadgeValidation.CheckName(patch.Name, Limits, errors);
        var description = patch.Description is null ? null : BadgeValidation.CheckDescription(patch.Description, Limits, errors);
        var rule = patch.Rule is null ? null : BadgeValidation.CheckRule(patch.Rule, Limits, errors);
        var image = patch.Image?.Image is null ? null : BadgeValidation.CheckImage(patch.Image.Image, Limits, errors);
        if (errors.Count > 0)
        {
            return new ValidationErrors(errors);
        }

        var checkedPatch = new CheckedPatch(name, description, rule, patch.Active, image, patch.Image is { Image: null });
        var ran = await transactions.RunAsync(ct => UpdateInTransactionAsync(actor, id.ToLowerInvariant(), checkedPatch, ct), cancellationToken).ConfigureAwait(false);
        if (!ran.TryPickT0(out var inner, out var runFailure))
        {
            return runFailure.Match<OneOf<Badge, NotFound, ValidationErrors, ConflictError, PortError>>(conflict => conflict, error => error);
        }

        if (!inner.TryPickT0(out var updated, out var failure))
        {
            return failure.Match<OneOf<Badge, NotFound, ValidationErrors, ConflictError, PortError>>(notFound => notFound, invalid => invalid, error => error);
        }

        if (updated.AffectsAwards)
        {
            await evaluation.ReconcileSafelyAsync(AuditActor.From(actor), BadgeEvalTrigger.Badge, null, cancellationToken).ConfigureAwait(false);
        }

        return updated.Badge;
    }

    private sealed record CheckedPatch(string? Name, string? Description, BadgeRule? Rule, bool? Active, BadgeImageData? Image, bool ClearImage);

    private sealed record Updated(Badge Badge, bool AffectsAwards);

    private async Task<TransactionOutcome<OneOf<Updated, NotFound, ValidationErrors, PortError>>> UpdateInTransactionAsync(Actor actor, string id, CheckedPatch patch, CancellationToken ct)
    {
        static TransactionOutcome<OneOf<Updated, NotFound, ValidationErrors, PortError>> Abort(OneOf<Updated, NotFound, ValidationErrors, PortError> value) =>
            TransactionOutcome.Abort(value);

        var found = await badges.FindAsync(id, ct).ConfigureAwait(false);
        if (found.TryPickT1(out var notFound, out var rest))
        {
            return Abort(notFound);
        }

        if (rest.TryPickT1(out var findFailure, out var before))
        {
            return Abort(findFailure);
        }

        var rule = patch.Rule;
        if (rule is not null)
        {
            var resolved = await ExistingTasksAsync(rule, ct).ConfigureAwait(false);
            if (resolved.TryPickT1(out var resolveFailure, out rule))
            {
                return Abort(resolveFailure.Match<OneOf<Updated, NotFound, ValidationErrors, PortError>>(invalid => invalid, error => error));
            }
        }

        var after = before with
        {
            Name = patch.Name ?? before.Name,
            Description = patch.Description ?? before.Description,
            Rule = rule ?? before.Rule,
            Active = patch.Active ?? before.Active,
            Image = patch.ClearImage ? null : patch.Image?.Info ?? before.Image,
        };
        var change = BadgeAudit.Between(before, after);
        if (change.IsNoOp)
        {
            return TransactionOutcome.Commit<OneOf<Updated, NotFound, ValidationErrors, PortError>>(new Updated(before, false));
        }

        var changes = new BadgeChanges(
            after.Name != before.Name ? after.Name : null,
            after.Description != before.Description ? after.Description : null,
            change.Diff.After["rule"] is not null ? after.Rule : null,
            after.Active != before.Active ? after.Active : null,
            patch.Image is not null && after.Image != before.Image ? patch.Image : null,
            patch.ClearImage && before.Image is not null);
        var written = await badges.UpdateAsync(id, changes, Now(), ct).ConfigureAwait(false);
        if (written.TryPickT1(out var gone, out var restWritten))
        {
            return Abort(gone);
        }

        if (restWritten.TryPickT1(out var writeFailure, out var stored))
        {
            return Abort(writeFailure);
        }

        var recorded = await audit.RecordAsync(BadgeAudit.ForUpdate(AuditActor.From(actor), id, change), ct).ConfigureAwait(false);
        return recorded.Match(
            _ => TransactionOutcome.Commit<OneOf<Updated, NotFound, ValidationErrors, PortError>>(new Updated(stored, BadgeAudit.AffectsAwards(change))),
            error => Abort(error));
    }

    // ---- delete

    public async Task<OneOf<Success, NotFound, ValidationErrors, ConflictError, PortError>> DeleteAsync(Actor actor, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(id);
        if (!BadgeIds.IsValid(id))
        {
            return ValidationErrors.For("id", "invalid_object_id");
        }

        var ran = await transactions.RunAsync(ct => DeleteInTransactionAsync(actor, id.ToLowerInvariant(), ct), cancellationToken).ConfigureAwait(false);
        if (!ran.TryPickT0(out var inner, out var runFailure))
        {
            return runFailure.Match<OneOf<Success, NotFound, ValidationErrors, ConflictError, PortError>>(conflict => conflict, error => error);
        }

        if (!inner.TryPickT0(out var deleted, out var failure))
        {
            return failure.Match<OneOf<Success, NotFound, ValidationErrors, ConflictError, PortError>>(notFound => notFound, error => error);
        }

        // The badge is gone, so its name travels along for the history of the awards that are withdrawn with it.
        await evaluation.ReconcileSafelyAsync(
            AuditActor.From(actor),
            BadgeEvalTrigger.Badge,
            new Dictionary<string, string> { [deleted.Id] = deleted.Name },
            cancellationToken).ConfigureAwait(false);
        return new Success();
    }

    private async Task<TransactionOutcome<OneOf<Badge, NotFound, PortError>>> DeleteInTransactionAsync(Actor actor, string id, CancellationToken ct)
    {
        static TransactionOutcome<OneOf<Badge, NotFound, PortError>> Abort(OneOf<Badge, NotFound, PortError> value) => TransactionOutcome.Abort(value);

        var found = await badges.FindAsync(id, ct).ConfigureAwait(false);
        if (found.TryPickT1(out var notFound, out var rest))
        {
            return Abort(notFound);
        }

        if (rest.TryPickT1(out var findFailure, out var before))
        {
            return Abort(findFailure);
        }

        var deleted = await badges.DeleteAsync(id, ct).ConfigureAwait(false);
        if (deleted.TryPickT1(out var gone, out var restDeleted))
        {
            return Abort(gone);
        }

        if (restDeleted.TryPickT1(out var deleteFailure, out _))
        {
            return Abort(deleteFailure);
        }

        var recorded = await audit.RecordAsync(BadgeAudit.ForDelete(AuditActor.From(actor), before), ct).ConfigureAwait(false);
        return recorded.Match(
            _ => TransactionOutcome.Commit<OneOf<Badge, NotFound, PortError>>(before),
            error => Abort(error));
    }

    // ---- examples

    public async Task<OneOf<AddedExampleBadges, BadgeLimitReached, ConflictError, PortError>> AddExamplesAsync(Actor actor, BadgeLanguage language, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var ran = await transactions.RunAsync(ct => AddExamplesInTransactionAsync(actor, language, ct), cancellationToken).ConfigureAwait(false);
        if (!ran.TryPickT0(out var inner, out var runFailure))
        {
            return runFailure.Match<OneOf<AddedExampleBadges, BadgeLimitReached, ConflictError, PortError>>(conflict => conflict, error => error);
        }

        if (!inner.TryPickT0(out var added, out var failure))
        {
            return failure.Match<OneOf<AddedExampleBadges, BadgeLimitReached, ConflictError, PortError>>(limit => limit, error => error);
        }

        if (added.Created.Count > 0)
        {
            await evaluation.ReconcileSafelyAsync(AuditActor.From(actor), BadgeEvalTrigger.Badge, null, cancellationToken).ConfigureAwait(false);
        }

        return added;
    }

    private async Task<TransactionOutcome<OneOf<AddedExampleBadges, BadgeLimitReached, PortError>>> AddExamplesInTransactionAsync(Actor actor, BadgeLanguage language, CancellationToken ct)
    {
        static TransactionOutcome<OneOf<AddedExampleBadges, BadgeLimitReached, PortError>> Abort(OneOf<AddedExampleBadges, BadgeLimitReached, PortError> value) =>
            TransactionOutcome.Abort(value);

        var activeTasks = await ActiveTasksAsync(ct).ConfigureAwait(false);
        if (activeTasks.TryPickT1(out var tasksFailure, out var taskList))
        {
            return Abort(tasksFailure);
        }

        var created = new List<Badge>();
        var skipped = 0;
        foreach (var example in ExampleBadges.All)
        {
            var existing = await badges.FindByExampleKeyAsync(example.Key, ct).ConfigureAwait(false);
            if (existing.TryPickT2(out var findFailure, out var rest))
            {
                return Abort(findFailure);
            }

            if (rest.IsT0)
            {
                skipped++;
                continue;
            }

            var counted = await badges.CountAsync(ct).ConfigureAwait(false);
            if (counted.TryPickT1(out var countFailure, out var count))
            {
                return Abort(countFailure);
            }

            if (count >= Limits.MaxBadges)
            {
                return Abort(new BadgeLimitReached(Limits.MaxBadges));
            }

            var matched = example.TaskNamePattern is null ? [] : taskList.Where(t => example.Matches(t.Name)).Select(t => t.Id).ToList();
            var rule = example.RuleType == BadgeRuleType.OnTimeWeeks
                ? BadgeRule.OnTimeWeeks(example.Threshold)
                : new BadgeRule(example.RuleType, BadgeValidation.SortedIds(matched), example.Threshold);
            var text = example.TextFor(language);
            var newBadge = new NewBadge(text.Name, text.Description, rule, example.TaskNamePattern is null || matched.Count > 0, example.Key, null, Now());
            var inserted = await badges.InsertAsync(newBadge, ct).ConfigureAwait(false);
            if (inserted.TryPickT1(out var insertFailure, out var stored))
            {
                return Abort(insertFailure);
            }

            var entry = BadgeAudit.ForCreate(AuditActor.From(actor), stored, AuditObject.Of(("example", example.Key)));
            var recorded = await audit.RecordAsync(entry, ct).ConfigureAwait(false);
            if (recorded.TryPickT1(out var recordFailure, out _))
            {
                return Abort(recordFailure);
            }

            created.Add(stored);
        }

        return TransactionOutcome.Commit<OneOf<AddedExampleBadges, BadgeLimitReached, PortError>>(new AddedExampleBadges(created, skipped));
    }

    /// <summary>Every active task, read in pages: the examples find their tasks by name.</summary>
    private async Task<OneOf<IReadOnlyList<HouseholdTask>, PortError>> ActiveTasksAsync(CancellationToken ct)
    {
        var all = new List<HouseholdTask>();
        TaskCursor? after = null;
        while (true)
        {
            var page = await tasks.ListAsync(null, true, after, TaskPageSize, ct).ConfigureAwait(false);
            if (page.TryPickT1(out var failure, out var items))
            {
                return failure;
            }

            all.AddRange(items);
            if (items.Count < TaskPageSize)
            {
                return all;
            }

            after = TaskCursor.After(items[^1]);
        }
    }

    // ---- a deleted task leaves the rules

    public async Task<OneOf<Success, ValidationErrors, ConflictError, PortError>> RemoveTaskFromRulesAsync(Actor actor, string taskId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(taskId);
        if (!BadgeIds.IsValid(taskId))
        {
            return ValidationErrors.For("taskId", "invalid_object_id");
        }

        var ran = await transactions.RunAsync(ct => RemoveTaskInTransactionAsync(actor, taskId.ToLowerInvariant(), ct), cancellationToken).ConfigureAwait(false);
        if (!ran.TryPickT0(out var inner, out var runFailure))
        {
            return runFailure.Match<OneOf<Success, ValidationErrors, ConflictError, PortError>>(conflict => conflict, error => error);
        }

        if (!inner.TryPickT0(out var changed, out var failure))
        {
            return failure;
        }

        if (changed)
        {
            await evaluation.ReconcileSafelyAsync(AuditActor.From(actor), BadgeEvalTrigger.Badge, null, cancellationToken).ConfigureAwait(false);
        }

        return new Success();
    }

    private async Task<TransactionOutcome<OneOf<bool, PortError>>> RemoveTaskInTransactionAsync(Actor actor, string taskId, CancellationToken ct)
    {
        static TransactionOutcome<OneOf<bool, PortError>> Abort(PortError error) => TransactionOutcome.Abort<OneOf<bool, PortError>>(error);

        var found = await badges.FindNamingTaskAsync(taskId, ct).ConfigureAwait(false);
        if (found.TryPickT1(out var findFailure, out var affected))
        {
            return Abort(findFailure);
        }

        var changed = false;
        foreach (var before in affected.Where(b => b.Rule.Type != BadgeRuleType.OnTimeWeeks))
        {
            var remaining = before.Rule.TaskIds.Where(id => !string.Equals(id, taskId, StringComparison.Ordinal)).ToList();
            var after = before with
            {
                Rule = before.Rule with { TaskIds = remaining },
                Active = remaining.Count == 0 ? false : before.Active,
            };
            var change = BadgeAudit.Between(before, after);
            if (change.IsNoOp)
            {
                continue;
            }

            var written = await badges.UpdateAsync(
                before.Id,
                new BadgeChanges(Rule: after.Rule, Active: after.Active != before.Active ? after.Active : null),
                Now(),
                ct).ConfigureAwait(false);
            if (written.TryPickT2(out var writeFailure, out _))
            {
                return Abort(writeFailure);
            }

            var meta = AuditObject.Of(("reason", "task_deleted"), ("taskId", new AuditObjectId(taskId)));
            var recorded = await audit.RecordAsync(BadgeAudit.ForUpdate(AuditActor.From(actor), before.Id, change, meta), ct).ConfigureAwait(false);
            if (recorded.TryPickT1(out var recordFailure, out _))
            {
                return Abort(recordFailure);
            }

            changed |= BadgeAudit.AffectsAwards(change);
        }

        return TransactionOutcome.Commit<OneOf<bool, PortError>>(changed);
    }

    private DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
}
