# Huishoudplanner.Domain

Pure domain code (only `OneOf`). No clock access: pass an instant or a `TimeProvider`; `DateTime.Now`/`UtcNow` are forbidden.

## Calendar (`Huishoudplanner.Domain.Calendar`)

Day keys are `DateOnly`; timezones are `TimeZoneInfo` resolved from IANA ids (`DayKeys.FindZone`); instants are `DateTimeOffset`.
Names map 1:1 to `packages/shared/src/time.ts` and `cycle.ts`.

| TypeScript                                                                                 | C#                                                                                                |
| ------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------- |
| `APP_TIMEZONE`                                                                             | `DayKeys.AppTimezone`                                                                             |
| `isDayKey`, `isTimeOfDay`, `isMonday`, `isWeekend`                                         | `DayKeys.IsDayKey`, `IsTimeOfDay`, `IsMonday` (also on `string`), `IsWeekend`                     |
| `today(tz, now)`                                                                           | `DayKeys.Today(tz, TimeProvider)` / `Today(tz, DateTimeOffset)`                                   |
| `toDayKey`, `fromDayKey`, `fromDayKeyTime`                                                 | `DayKeys.ToDayKey`, `FromDayKey`, `FromDayKeyTime`                                                |
| `addDays`, `daysBetween`, `mondayOf`                                                       | `DayKeys.AddDays`, `DaysBetween`, `MondayOf`                                                      |
| `weekdaySun0`, `weekdayMon0`, `sun0ToMon0`, `mon0ToSun0`                                   | `DayKeys.WeekdaySun0`, `WeekdayMon0`, `Sun0ToMon0`, `Mon0ToSun0`                                  |
| `isoWeek`, `isoWeekLabel`, `mondayOfIsoWeek`                                               | `DayKeys.IsoWeek` (`IsoWeekNumber`), `IsoWeekLabel`, `MondayOfIsoWeek` (`DateOnly?`)              |
| (day key parsing, implicit in the TS string type)                                          | `DayKeys.Parse(string)`                                                                           |
| `CYCLE_DAYS`, `CYCLE_WEEKS`                                                                | `Cycles.CycleDays`, `Cycles.CycleWeeks`                                                           |
| `assertValidAnchor`, `cycleIndexFor`, `cycleStart`, `cycleEnd`, `weekIndexFor`, `slotDate` | `Cycles.AssertValidAnchor`, `CycleIndexFor`, `CycleStart`, `CycleEnd`, `WeekIndexFor`, `SlotDate` |

Error mapping of the TypeScript `RangeError`: unparsable day key is `FormatException`; unknown timezone, invalid time of
day, non-Monday anchor and out-of-range weekday or week index are `ArgumentOutOfRangeException`.

## Ports and errors

`Ports/Driven/ForRunningTransactions` runs a use case as one atomic unit (entity write plus audit entry, ADR-0021). The work delegate returns a `TransactionOutcome<T>`: `Commit(value)` or `Abort(value)` (roll back, still return the failure value). Results are `OneOf<T, ConflictError, PortError>`; see the interface remarks for retry, nesting and cancellation semantics. `PortError` messages never contain configuration values.

## Users (`Huishoudplanner.Domain.Users`)

The people of the household (requirements 2 and 3). `UserRules` owns what a valid person is (the limits of `packages/shared/src/schemas/users.ts`: `#rrggbb` colour, weekdays 0 to 6, non-negative minutes, at most six unique `HH:mm` notification moments), turns raw request values (`CreateUserInput`, `UpdateUserInput`, `BrowserNotificationsInput`) into `NewUser` / `UserPatch` or into field errors keyed by the Node dotted path, applies a patch, decides the last-administrator rule and shapes the audit object. Legacy documents read with defaults (`UserDefaults`): no role is an administrator, no daily maximum 480/480. Ports: driving `IUserService` and `IUserSeedService`, driven `ForStoringUsers`, `ForPreparingStorage` (startup) and the narrow identity lookup `ForFindingUsers`.

## Due (`Huishoudplanner.Domain.Due`)

Port of `packages/shared/src/due.ts`; the scheduling "hybrid" half. Pure: the caller passes `today` as a day key and the timezone.

| TypeScript                                      | C#                                                                                                |
| ----------------------------------------------- | ------------------------------------------------------------------------------------------------- |
| `DUE_RATIO`, `OVERDUE_RATIO`                    | `DueCalculator.DueRatio`, `OverdueRatio` (`const double`)                                         |
| `DEFAULT_INTERVALS`                             | `DueCalculator.DefaultIntervals` (`IReadOnlyList<Interval>`)                                      |
| `DueState` (`'ok'`, `'due'`, `'overdue'`)       | `enum DueState { Ok, Due, Overdue }`                                                              |
| `dueState(ratio)`                               | `DueCalculator.DueStateOf(ratio)`                                                                 |
| `DueTaskInput` (`_id`, `lastCompletedAt: Date`) | `DueTaskInput(Id, Active, IntervalKey, DateTimeOffset? LastCompletedAt, DateOnly InitialDueDate)` |
| `Interval`                                      | `Interval(Key, Label, int? PerCycle, PeriodDays)`                                                 |
| `DueResult`                                     | `DueResult(TaskId, DaysSince, PeriodDays, double Ratio, State)`                                   |
| `computeDue(tasks, intervals, today, tz)`       | `DueCalculator.ComputeDue(tasks, intervals, DateOnly today, TimeZoneInfo tz)`                     |

Differences: the timezone has no default (pass `DayKeys.FindZone(DayKeys.AppTimezone)`); the final tie-break by task id is
`StringComparer.Ordinal` where TypeScript used `localeCompare`; an interval with `PeriodDays == 0` is skipped like an unknown
key (TypeScript skipped it through `!periodDays`). `ComputeDue` throws nothing itself; an invalid day key or timezone
fails earlier, in `DayKeys.Parse` / `FindZone`.

The due list of `GET /api/v2/due` (slice 3.4, `computeDueList` and `summarizeDue` of `apps/server/src/domain/due.ts`) adds
`DueItem` (the ranked result with task, room and interval names, `InitialDueDate` and `DueNextOccurrence`), `DueList`
(`Today`, one page of `Items`, `NextCursor`, `DueSummary` of the whole list), `DueSummary(Due, Overdue)` (pure,
`DueSummary.Of(states | results | items)`; the `due` of the generation answer), `DueCursor` (position by days, period and task
id, so a page continues correctly even when the task it points at has dropped out) and
`DueCalculator.InitialDueDateOf(firstPlanned, createdAt, periodDays, tz)` (first planned day, else one interval after creation).
The data gathering lives in `Application.Due.DueService` behind `IDueService` and the read-only port `ForReadingDueOccurrences`.

## Settings (`Huishoudplanner.Domain.Settings`)

The singleton settings document (id `000000000000000000000001`). `HouseholdSettings` mirrors `apps/server/src/data/settings.ts`: optional values stay `null` when they are not stored and mean their default for the API (`SettingsDefaults`: EUR, 0 cents per point, automatic goals, no bonuses). There is no API key in it: `AI_API_KEY` is configuration only.

| TypeScript                                                  | C#                                                                                                                                                     |
| ----------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `Settings`, `SettingsDoc`                                   | `HouseholdSettings` (stored), `SettingsView` (what `GET` returns: defaults, `BonusesInForce`, rows with `StartsInFuture`)                              |
| `UpdateSettingsInput`                                       | `SettingsPatch` (what a client may send), `SettingsChanges` (what a write sets)                                                                        |
| `updateSettingsInputSchema` rules                           | `SettingsRules.Validate` (field paths and message codes as zod; the JSON shape is read in the HTTP adapter)                                            |
| `bonusAmountsOn`, `sameBonusAmounts`, `scheduleWithAmounts` | `BonusSchedule.AmountsOn`, `SameAmounts`, `WithAmounts` (golden vectors `bonuses.json`; the period, set and entry rules are `Bonuses.BonusCalculator`) |
| `DEFAULT_AI_PROMPTS`, `DEFAULT_INTERVALS`, seed             | `SettingsDefaults.AiPrompts`, `DueCalculator.DefaultIntervals`, `SettingsDefaults.ForNewInstallation`, `WithThreePerWeek`                              |
| `isTwoDecimalCurrency`, `Intl.supportedValuesOf`            | `Currencies.HasTwoDecimals`, `IsKnown` (from the region data of the platform)                                                                          |
| `diffFields` of a settings update                           | `SettingsAudit.ToAudit` (the audit value tree of a settings document) with `ChangeSet`                                                                 |

Driving ports `ISettingsService` (read, patch) and `ISettingsSeedService`; driven ports `ForStoringSettings` (read, set fields, insert once) and `ForStoringTasks` (its `GetIntervalKeysInUseAsync` answers the interval keys tasks use). `IntervalInUse(Keys)` is the `409 interval_in_use` value.

Differences: the Node compare-and-set on the stored schedule is replaced by the transaction that reads, checks and writes (a retried attempt recomputes the row), and `bonus_schedule_conflict` is the runner's exhausted write conflict on a patch that sets amounts.

## Rooms (`Huishoudplanner.Domain.Rooms`)

Port of `routes/rooms.ts` and `data/rooms.ts`. Driving port `IRoomService` (list, create, update, delete; the HTTP adapter decides who may write), driven ports `ForStoringRooms` and `ForStoringTasks` (its `CountInRoomAsync` answers how many tasks use a room). Create, update and delete are one transaction with their audit entry (`room`/`create`, `update`, `delete`; create records the four fields `name, sortOrder, active, virtual`, update the changed ones, delete the fields it removed). An update that changes nothing writes and audits nothing. A delete is refused with `RoomInUse(TaskCount)` while any task, active or inactive, uses the room. The list is ordered by sort order, name and id and paged with an opaque `RoomCursor`.

## Tasks (`Huishoudplanner.Domain.Tasks`)

Port of `routes/tasks.ts`, `domain/tasks.ts` and `data/tasks.ts`. Driving port `ITaskService` (list, create, update, bulk change of one room; planners write), driven port `ForStoringTasks`, which also answers the room and interval usage questions of the room and settings use cases. `HouseholdTask.Points` is the value in force (a stored task without points reads as `TaskPoints.DefaultForDuration`); create applies the same default when `points` is omitted. `TaskRules` holds the value rules, the use case checks that the room (active), interval and default assignee (active person) exist and names every failing field (`unknown_room`, `inactive_room`, `unknown_interval`, `unknown_user`, `inactive_user`). `TaskAudit` writes the audit shapes of the Node server: create records every field, a change of the default assignee is its own `assign` entry next to the `update` of the other fields. Every change is one transaction with its entries; a change that changes nothing writes and audits nothing. Not here yet: DELETE (it cascades into plans and badge rules). A room change points the room snapshots of the open occurrences from today on at the new room, in the same transaction (see Generation and occurrences).

## Notifications (`Huishoudplanner.Domain.Notifications`)

`NotifyMessage` (title, body, structured data) and the driven port `ForSendingNotifications` (never throws; `PortError` messages carry the notifier and HTTP status only, never URL or token; `IsEnabled` is false for the none notifier). `MorningMessage.Compose(MorningCounts)` is the pure Dutch morning text of `domain/notify/morning.ts`; the orchestration (one message per active user, once a day) belongs to the jobs slice.

## Audit log read (`Huishoudplanner.Domain.Audit`)

`AuditLogEntry` is the read model of a stored entry (wire names as strings, `before`/`after`/`meta` as `AuditObject`), `AuditLogFilter` and `AuditLogPage` its query and page, and `AuditCursor` the keyset cursor (same base64url `"{ISO instant}|{id}"` encoding as the Node server). Driving ports `IAuditLogService` (list, clear) and `IAuditRetentionService`; driven ports `ForReadingAuditLog`, `ForDeletingAuditEntries` (the only deletes of the log: clear and retention), `ForReadingOccurrenceContext` and `ForReadingAuditRetention` (`AUDIT_RETENTION_DAYS`).

## Cycle plans (`Huishoudplanner.Domain.CyclePlans`)

Port of `routes/cyclePlans.ts`, `domain/plans.ts`, `domain/slots.ts`, `domain/planDiff.ts` and `data/cyclePlans.ts`; the activation is described under Plan activation below. Saving the slots of the active plan synchronises the upcoming occurrences in the same transaction (`IGenerationService.ReplaceUpcomingAsync`, see Generation and occurrences) and the result carries `Synchronized`. Driving port `ICyclePlanService` (list, get, active, create or copy, update, delete, replace slots, compare with the active plan, validate a stored plan or an unsaved draft; planners write), `ICyclePlanSeedService` (the empty active plan "Standaard" on a first start), driven port `ForStoringCyclePlans`. Slots are saved after `PlanValidator` ran over the tasks, people and intervals read through the existing stores: a hard error is `InvalidPlan(PlanValidation)` (`422 invalid_plan`) and nothing is written. The oldest plan (`createdAt`, then id) is the default plan: `CyclePlanRules.DeleteConflict` gives `409 default_plan` before `409 active_plan`. `CyclePlanSlots` sorts (week, Monday-first weekday, sort order, task) and diffs slots by `taskId:weekIndex:weekday`; `CyclePlanAudit` writes the Node shapes (create records every stored field with `meta.copiedFrom` for a copy, a slot save only the added, removed and changed slots, delete the removed fields). `PlanDiffer` pairs slots per task (identical, same day with another assignee, remaining in cycle order). Every change is one transaction with its entry; a change that changes nothing writes and audits nothing.

## Generation and occurrences (`Huishoudplanner.Domain.Generation`, `Occurrences`)

Port of `domain/generation.ts`, `data/cycles.ts`, `data/occurrences.ts` (the insert, read and room snapshot parts), `routes/cycles.ts` and the model of `domain/occurrences.ts`. Driving ports `ICycleService` (the read-only cycle list) and `IGenerationService` (`GenerateUpcomingAsync`, the nightly run; `GenerateCycleAsync`; `ReplaceUpcomingAsync`, the replacement rule used by the slot save of the active plan and later by the activation), driven ports `ForStoringCycles` and `ForStoringOccurrences`. Every call is one transaction, or joins the caller's, with its audit entries; a run that changes nothing writes and audits nothing.

- `Occurrence` holds every stored field of an `occurrences` document (statuses `open`, `done`, `skipped`, origin `generated` or `adhoc`, the snapshots of name, duration and room, the optional points, request and period owner fields), so slice 3.2 adds actions on top of it without changing it. `NewGeneratedOccurrence` is what generation hands the store; `OccurrenceAudit` writes the Node shapes (a generated occurrence lists every field in `after`, a removal every field in `before`, `meta` carries the run id).
- `ForStoringOccurrences` is deliberately focused: the idempotent insert of generated occurrences, the two range reads generation needs (the generated ones by planned day, the replaceable ones), delete by id and the room snapshot refresh. 3.2 and 3.3 extend it with the reads and updates of the actions and the ad-hoc inserts. The Mongo adapter inserts with one upsert per draft on the key of the partial unique index `occurrences_generated_slot_unique` because a duplicate key error would abort a MongoDB transaction; the index still enforces the rule for concurrent writers.
- `OccurrencePlanner` is the pure rule: which occurrences the slots of a plan give in a cycle (inactive tasks, vacation days and days before today are skipped) and what a draft snapshots. `OccurrenceReconciliation` decides whether the upcoming occurrences must be replaced (a missing slot, or a replaceable occurrence with another day, assignee or plan; only generated occurrences occupy a slot, only open ones that were not dragged may be replaced). The TypeScript function takes database documents, so there are no golden vectors; the date scenarios of `generation.test.ts` are ported as direct tests.
- `Cycle`, `CycleAudit` and `CycleCursor` hold the cycle document (day keys as `DateOnly`), its audit shapes (create, anchor alignment, plan change) and the index cursor.

## Plan activation (`Huishoudplanner.Domain.Activation`)

Port of `domain/activationPreview.ts`, `domain/activation.ts` and `setActivePlan` of `data/cyclePlans.ts`. Driving port `IActivationService` (the read-only `PreviewAsync` and `ActivateAsync`), driven ports `ForActivatingCyclePlans` (the guard write and the plan writes) and `ForReadingOccurrencesForActivation` (the occurrences of the window and of the two cycles). `ActivationPreviewer` is the pure projection of the current and the next cycle: the open, generated, not moved occurrences from today on that the replacement removes, the occurrences the plan adds (a surviving generated occurrence on the same cycle, task and planned day blocks an insert, skipped and moved ones included; ad-hoc ones never do) and the groups that stay (done, skipped, moved, ad hoc). The token is a SHA-256 over that result and over what it depends on: the plan with its update time, the tasks of the plan with their room names, the occurrence state of the window, the active plan, the timezone, anchor and vacations, and the two cycle documents. `ActivationService.ActivateAsync` is one transaction: it writes the guard document (the `activationVersion` counter on `settings`) first, recomputes the preview and compares the token (`409 stale_activation_preview`, nothing written), deactivates the other active plans and activates the plan (an AI draft stops being a draft), records the entries of `ActivationAudit` (`update` per deactivated plan, `activate` with `meta.runId`) and replaces the upcoming occurrences through `IGenerationService.ReplaceUpcomingAsync` with the system as source and the same run id. Activating the already active plan is audited and regenerates, as in Node.

## Points, bonuses, rewards, badges (`Domain.Points`, `Bonuses`, `Rewards`, `Badges`)

The pure half of phase 4 (slice 4.0): ports of `packages/shared/src/points.ts`, `bonuses.ts`, `rewards.ts` and `badges.ts`, each pinned by golden vectors (`points.json`, `bonuses.json`, `rewards.json`, `badges.json`, plus the constants and `defaultPointsForDuration` in `limits.json`). Day keys are `DateOnly`, instants `DateTimeOffset`, person and task ids 24-character hex strings. Not here yet (slices 4.1 to 4.5): the ledger, the reconcile, redemptions, the progress endpoint and the badge awards, which read Mongo documents and map them onto these types. Rounding: the only rounding in these modules is `Math.round` in `defaultPointsForDuration` (halves round up, also for negative halves toward +infinity), ported as `TaskPoints.DefaultForDuration(double)` with `Math.Floor` and never `Math.Round` (banker's rounding); every other division is a floor of positive numbers (`rewardPercent`, `eggsForPercent`), done in 64-bit integers or `Math.Floor`.

| TypeScript                                                                                                    | C#                                                                                                                                                            |
| ------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `MIN_TASK_POINTS`, `MAX_TASK_POINTS`, `defaultPointsForDuration`                                              | `TaskPoints.Min`, `Max`, `DefaultForDuration(int)` and `(double)` (`Limits`)                                                                                  |
| `MIN/MAX_CENTS_PER_POINT`, `DEFAULT_CURRENCY_CODE`, `MAX_REDEMPTION_NOTE_LENGTH`                              | `HouseholdLimits.Points`, `Defaults` (`Limits`)                                                                                                               |
| `pointsToCents`                                                                                               | `PointsMoney.PointsToCents(long, long)` (`long`, checked: an overflow throws)                                                                                 |
| `isTwoDecimalCurrency`                                                                                        | `Settings.Currencies.HasTwoDecimals`                                                                                                                          |
| `formatCents`                                                                                                 | not ported: display with `Intl.NumberFormat` stays in the web app (`omitted` in `scripts/vectors.ts`)                                                         |
| `BONUS_KINDS`, `BonusKind`, `isBonusKind`                                                                     | `enum BonusKind`, `BonusKinds.All`, `ToLedgerKind()` (`bonus_week_done` ...), `TryParse`, `IsBonusKind`                                                       |
| `BonusAmounts`, `BonusScheduleRow`, `NO_BONUSES`, `bonusAmountsOn`, `sameBonusAmounts`, `scheduleWithAmounts` | stay in `Settings.BonusSchedule` (slice 1.3); `BonusKinds.AmountOf(amounts, kind)` is the `AMOUNT_OF_KIND` lookup                                             |
| `Period`, `PeriodUnit`, `weekOf`, `cycleOf`, `periodEnded`, `onTimeCutoff`                                    | `Period(Unit, Start, End)`, `Period.WeekOf`, `Period.CycleOf`, `HasEnded(today)`, `OnTimeCutoff(timezone)`                                                    |
| `BonusOccurrence`                                                                                             | `BonusOccurrence` (`RecordedDone` is a `bool`; `periodOwnerId` undefined/null/value is `Frozen`: `null`, `FrozenOwner(null)`, `FrozenOwner(id)`)              |
| `periodDayOf`, `periodOwnerOf`, `creditedOf`                                                                  | `BonusOccurrence.PeriodDay`, `PeriodOwnerId`, `CreditedId`                                                                                                    |
| `placementsOf`, `evaluateSet`, `SetEvaluation`                                                                | `BonusCalculator.PlacementsOf`, `EvaluateSet`, `SetEvaluation`                                                                                                |
| `bonusKey`, `expectedBonusEntries`, `BonusContext`, `ExpectedBonusEntry`                                      | `BonusCalculator.BonusKey`, `ExpectedEntries`, `BonusContext(Anchor, Timezone: TimeZoneInfo, Today, Schedule, Floor)`, `ExpectedBonusEntry`                   |
| reward constants, `RewardGoals`, `NO_REWARD_GOALS`, `sameRewardGoals`                                         | `RewardMeter.MinGoalPoints`, `MaxGoalPoints`, `EggCount`; `Settings.RewardGoals` (`Automatic`); `RewardMeter.SameGoals`                                       |
| `GoalOccurrence`, `AutomaticGoal`, `automaticGoal`                                                            | `GoalOccurrence(Occurrence, Points)`, `AutomaticGoal(Planned, Points)`, `RewardMeter.AutomaticGoalFor(items, personId, period)`                               |
| `ResolvedRewardGoal`, `resolveRewardGoal`, `rewardPercent`, `eggsForPercent`                                  | `ResolvedRewardGoal`, `RewardMeter.ResolveGoal`, `Percent(long, int?)`, `EggsForPercent(double)`                                                              |
| `BadgeRule`, `BADGE_RULE_TYPES`, `ruleCovers`, `evaluateBadgeRule`, `BadgeExecution`, `BadgeOutcome`          | `BadgeRule(Type, TaskIds, Threshold)` (`TaskIds` empty for on-time weeks), `BadgeRuleType`, `BadgeRules.Covers`, `Evaluate`, `BadgeExecution`, `BadgeOutcome` |
| `sniffBadgeImageType`, `BADGE_IMAGE_TYPES`                                                                    | `BadgeImages.Sniff(ReadOnlySpan<byte>)` giving `BadgeImageType?`, `ContentType()`                                                                             |
| `EXAMPLE_BADGES`, `ExampleBadge`, the task-name pattern                                                       | `ExampleBadges.All`, `ExampleBadge` (`TextFor(language)`, `Matches(taskName)`: `RegexOptions.ECMAScript \| IgnoreCase`, so `\b` stays ASCII as in JavaScript) |
| the zod schemas of `badges.ts` (`createBadgeInputSchema`, ...), `MAX_BADGE_*`                                 | not in this slice: request validation belongs to the HTTP adapter and the limits to `HouseholdLimits.Badges` (slice 4.5)                                      |

Differences: `OccurrenceStatus` lives in `Domain.Bonuses` until the occurrence model of phase 3 takes it over. `Percent` takes `long` earned points so a large balance cannot overflow; `PointsToCents` is `long`. Instants compare in whole milliseconds like JavaScript `Date`. Ties in the badge order and the bonus entry order use `StringComparer.Ordinal` (JavaScript's `<` on strings). A badge threshold below 1 never awards a count rule (there is no 0th execution) but awards a minutes rule at the first execution, exactly as TypeScript. `Currencies.HasTwoDecimals` is case-sensitive and false for a code the platform does not know, where `isTwoDecimalCurrency` accepts any well-formed code (`eur`, `ABC` give true there); settings validation already requires a known upper-case code, so no vector pins those inputs.

## AI (`Huishoudplanner.Domain.Ai`)

Port of `apps/server/src/domain/ai/`. `ForChattingWithAModel` (text in, text out, failures as values: `AiUnavailable`, `PortError`) and `ForSelectingAModel` (the model for the provider settings of one call, so a settings change applies at once). `PromptBuilder` holds the prompts, the JSON schemas and `CodeInfo` (prompt-info); `PromptPayloads` are the user messages as the model receives them; `AiOutputs` reads what a model answered (path-listed problems, the suggestion filter); `PlanCompleter` is `completeRequiredOccurrences`; `ProposalErrors` formats the rejections that are fed back on the one re-prompt. The use cases are `IAiService` (implemented in `Application/Ai`); a stored proposal is a draft plan (`NewPlanProposal`, `ForStoringCyclePlans.InsertProposalAsync`) audited with source `ai`.
