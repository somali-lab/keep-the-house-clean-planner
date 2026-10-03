# Golden vectors

Language-neutral test vectors for the calendar and scheduling rules (`docs/plans/dotnet-rewrite.md`
section 7.2). The TypeScript implementation in `packages/shared/src` produced the expected values;
the C# domain tests read the same files and must produce the same results. When `packages/shared`
is deleted, these files remain the executable memory of the rules.

Do not edit the JSON by hand. The cases live in `scripts/vectors.ts`; regenerate with
`npm run export:vectors`. The vitest test `packages/shared/src/vectors.test.ts` fails when a checked-in
file differs from what the TypeScript functions return today.

## Files

One file per TypeScript module: `time.json`, `cycle.json`, `due.json`. Later slices add
`bonuses.json`, `points.json`, `badges.json`, `rewards.json` and `validation.json` in the same format.

## Format

```json
{
  "module": "time",
  "cases": [
    {
      "name": "spring forward gap: 02:30 lands after the gap",
      "function": "fromDayKeyTime",
      "input": { "key": "2026-03-29", "time": "02:30", "tz": "Europe/Amsterdam" },
      "expected": "2026-03-29T01:30:00.000Z"
    },
    {
      "name": "weekIndex 4 throws",
      "function": "slotDate",
      "input": { "cycleStartDate": "2026-09-14", "weekIndex": 4, "weekday": 0 },
      "throws": "RangeError"
    }
  ]
}
```

- `function` is the TypeScript function name (camelCase); the C# test maps it to the ported method.
- `input` holds the named arguments of the function, using the TypeScript parameter names.
- A case has either `expected` or `throws`, never both. `throws` is the TypeScript error class; the C# port
  throws its equivalent for invalid input (`RangeError` maps to `ArgumentOutOfRangeException` or the
  domain's own exception).
- `expected: null` is a real result (for example `mondayOfIsoWeek` of an invalid label). `assertValidAnchor`
  returns nothing, recorded as `null`.

## Value conventions

- Day keys are `YYYY-MM-DD` strings in the household timezone (`DateOnly` in C#).
- Instants are ISO 8601 UTC strings with a trailing `Z` (`DateTimeOffset` in C#). Inputs may also carry
  an explicit offset such as `+00:00`.
- Timezones are IANA ids (`Europe/Amsterdam`, `UTC`). An unknown id is an error case.
- Weekdays are `0` = Sunday .. `6` = Saturday, except in functions that say Monday-first (`weekdayMon0`).
- `isoWeek` returns `{ "year", "week" }`; `isoWeekLabel` returns `YYYY-Www`.
- `computeDue` takes `tasks`, `intervals`, `today` and `timezone` and returns the ranked list of
  `{ taskId, daysSince, periodDays, ratio, state }`. Compare `ratio` as a double. The order of the list is part
  of the expectation.
- `dueState` takes a `ratio` and returns `ok`, `due` or `overdue`.
