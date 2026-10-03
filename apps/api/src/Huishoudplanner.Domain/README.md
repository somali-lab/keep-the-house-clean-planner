# Huishoudplanner.Domain

Pure domain code (only `OneOf`). No clock access: pass an instant or a `TimeProvider`; `DateTime.Now`/`UtcNow` are forbidden.

## Calendar (`Huishoudplanner.Domain.Calendar`)

Day keys are `DateOnly`; timezones are `TimeZoneInfo` resolved from IANA ids (`DayKeys.FindZone`); instants are `DateTimeOffset`.
Names map 1:1 to `packages/shared/src/time.ts` and `cycle.ts`.

| TypeScript                                                   | C#                                                                       |
| ------------------------------------------------------------ | ------------------------------------------------------------------------ |
| `APP_TIMEZONE`                                               | `DayKeys.AppTimezone`                                                    |
| `isDayKey`, `isTimeOfDay`, `isMonday`, `isWeekend`           | `DayKeys.IsDayKey`, `IsTimeOfDay`, `IsMonday` (also on `string`), `IsWeekend` |
| `today(tz, now)`                                             | `DayKeys.Today(tz, TimeProvider)` / `Today(tz, DateTimeOffset)`          |
| `toDayKey`, `fromDayKey`, `fromDayKeyTime`                   | `DayKeys.ToDayKey`, `FromDayKey`, `FromDayKeyTime`                       |
| `addDays`, `daysBetween`, `mondayOf`                         | `DayKeys.AddDays`, `DaysBetween`, `MondayOf`                             |
| `weekdaySun0`, `weekdayMon0`, `sun0ToMon0`, `mon0ToSun0`     | `DayKeys.WeekdaySun0`, `WeekdayMon0`, `Sun0ToMon0`, `Mon0ToSun0`         |
| `isoWeek`, `isoWeekLabel`, `mondayOfIsoWeek`                 | `DayKeys.IsoWeek` (`IsoWeekNumber`), `IsoWeekLabel`, `MondayOfIsoWeek` (`DateOnly?`) |
| (day key parsing, implicit in the TS string type)            | `DayKeys.Parse(string)`                                                  |
| `CYCLE_DAYS`, `CYCLE_WEEKS`                                  | `Cycles.CycleDays`, `Cycles.CycleWeeks`                                  |
| `assertValidAnchor`, `cycleIndexFor`, `cycleStart`, `cycleEnd`, `weekIndexFor`, `slotDate` | `Cycles.AssertValidAnchor`, `CycleIndexFor`, `CycleStart`, `CycleEnd`, `WeekIndexFor`, `SlotDate` |

Error mapping of the TypeScript `RangeError`: unparsable day key is `FormatException`; unknown timezone, invalid time of
day, non-Monday anchor and out-of-range weekday or week index are `ArgumentOutOfRangeException`.

## Ports and errors

`Ports/Driven/ForRunningTransactions` runs a use case as one atomic unit (entity write plus audit entry, ADR-0021). The work delegate returns a `TransactionOutcome<T>`: `Commit(value)` or `Abort(value)` (roll back, still return the failure value). Results are `OneOf<T, ConflictError, PortError>`; see the interface remarks for retry, nesting and cancellation semantics. `PortError` messages never contain configuration values.

## Users (`Huishoudplanner.Domain.Users`)

The people of the household (requirements 2 and 3). `UserRules` owns what a valid person is (the limits of `packages/shared/src/schemas/users.ts`: `#rrggbb` colour, weekdays 0 to 6, non-negative minutes, at most six unique `HH:mm` notification moments), turns raw request values (`CreateUserInput`, `UpdateUserInput`, `BrowserNotificationsInput`) into `NewUser` / `UserPatch` or into field errors keyed by the Node dotted path, applies a patch, decides the last-administrator rule and shapes the audit object. Legacy documents read with defaults (`UserDefaults`): no role is an administrator, no daily maximum 480/480. Ports: driving `IUserService` and `IUserSeedService`, driven `ForStoringUsers`, `ForPreparingStorage` (startup) and the narrow identity lookup `ForFindingUsers`.

## Due (`Huishoudplanner.Domain.Due`)

Port of `packages/shared/src/due.ts`; the scheduling "hybrid" half. Pure: the caller passes `today` as a day key and the timezone.

| TypeScript                                       | C#                                                                                                |
| ------------------------------------------------ | ------------------------------------------------------------------------------------------------- |
| `DUE_RATIO`, `OVERDUE_RATIO`                     | `DueCalculator.DueRatio`, `OverdueRatio` (`const double`)                                         |
| `DEFAULT_INTERVALS`                              | `DueCalculator.DefaultIntervals` (`IReadOnlyList<Interval>`)                                      |
| `DueState` (`'ok'`, `'due'`, `'overdue'`)        | `enum DueState { Ok, Due, Overdue }`                                                              |
| `dueState(ratio)`                                | `DueCalculator.DueStateOf(ratio)`                                                                 |
| `DueTaskInput` (`_id`, `lastCompletedAt: Date`)  | `DueTaskInput(Id, Active, IntervalKey, DateTimeOffset? LastCompletedAt, DateOnly InitialDueDate)` |
| `Interval`                                       | `Interval(Key, Label, int? PerCycle, PeriodDays)`                                                 |
| `DueResult`                                      | `DueResult(TaskId, DaysSince, PeriodDays, double Ratio, State)`                                   |
| `computeDue(tasks, intervals, today, tz)`        | `DueCalculator.ComputeDue(tasks, intervals, DateOnly today, TimeZoneInfo tz)`                     |

Differences: the timezone has no default (pass `DayKeys.FindZone(DayKeys.AppTimezone)`); the final tie-break by task id is
`StringComparer.Ordinal` where TypeScript used `localeCompare`; an interval with `PeriodDays == 0` is skipped like an unknown
key (TypeScript skipped it through `!periodDays`). `ComputeDue` throws nothing itself; an invalid day key or timezone
fails earlier, in `DayKeys.Parse` / `FindZone`.

## Rooms (`Huishoudplanner.Domain.Rooms`)

Port of `routes/rooms.ts` and `data/rooms.ts`. Driving port `IRoomService` (list, create, update, delete; the HTTP adapter decides who may write), driven ports `ForStoringRooms` and `ForCheckingRoomUsage` (a read port on the `tasks` collection until the task domain exists). Create, update and delete are one transaction with their audit entry (`room`/`create`, `update`, `delete`; create records the four fields `name, sortOrder, active, virtual`, update the changed ones, delete the fields it removed). An update that changes nothing writes and audits nothing. A delete is refused with `RoomInUse(TaskCount)` while any task, active or inactive, uses the room. The list is ordered by sort order, name and id and paged with an opaque `RoomCursor`.

## Notifications (`Huishoudplanner.Domain.Notifications`)

`NotifyMessage` (title, body, structured data) and the driven port `ForSendingNotifications` (never throws; `PortError` messages carry the notifier and HTTP status only, never URL or token; `IsEnabled` is false for the none notifier). `MorningMessage.Compose(MorningCounts)` is the pure Dutch morning text of `domain/notify/morning.ts`; the orchestration (one message per active user, once a day) belongs to the jobs slice.
