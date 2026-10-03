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
