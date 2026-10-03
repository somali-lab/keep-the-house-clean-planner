# ADR-0013 — Redemptions as booked ledger entries

Status: Proposed

## Context

Points can be converted into money, and a person can redeem points for a payout or a reward. The household confirmed three choices: a configurable conversion factor from points to currency in the settings, people book their own redemption or payout directly, and every booking is audited.

ADR-0011 built the points ledger as a keyed projection of the occurrences and reserved a place in it for entries that are facts in their own right. ADR-0012 added the bonuses as derived kinds. Redemptions are the first entries that cannot be derived: nothing in the occurrences says that somebody paid out 40 points on a Thursday. Several facts constrain the design.

**A reconciliation owns the derived kinds only.** `reconcilePoints` loads the stored `execution` entries and the four bonus kinds, compares them with what the occurrences expect, and deletes whatever is stored and no longer expected. A kind it does not load is never compared and never deleted, so a booked kind survives every run.

**There are no transactions** (standalone MongoDB, ADR-0008) and only one application process runs (ADR-0005). A booking that checks the balance and then inserts is a read followed by a write, and two bookings that interleave could overdraw the balance.

**An export carries what cannot be rebuilt.** ADR-0011 chose to rebuild the ledger on import because every entry was derived, and said that P10c would have to add the booked entries to the export.

**Money is a display concern.** The ledger counts whole points. What a point is worth is a household choice that can change, and a payout that was agreed at 10 cents per point must stay worth what it was.

## Decision

### Settings: a currency and cents per point

Settings gain two fields:

- `currencyCode`: an ISO 4217 code, three capitals that the runtime knows as a currency. A missing value means `EUR`.
- `centsPerPoint`: an integer from `0` to `10000`. A missing value means `0`, which shows no money anywhere.

Money is always integer cents. The only division by 100 is where a number is shown, with `Intl.NumberFormat` in the active locale, and the points-to-cents multiplication needs no rounding because both factors are whole numbers.

`PATCH /api/settings` is already administrator-only and accepts both fields. A value equal to the one in force, where a missing value counts as its default, is dropped from the patch, so a no-op writes and audits nothing. A change is one settings `update` with the old and new value. `GET /api/settings` always returns both fields. Neither field is retroactive: a redemption keeps the factor of its own moment (below), and only the display of balances follows the factor in force.

### A redemption is a booked ledger entry

A redemption is a document in `pointEntries` with kind `redemption`:

```ts
{
  key: 'redemption:<_id>',      // unique, like every entry
  kind: 'redemption',
  personId, amount,             // amount is negative: -points
  date, weekStart,              // today in the household timezone, and that week's Monday
  occurrenceId: null, taskId: null, periodStart: null, titleSnapshot: '',
  note: string | null,          // optional, at most 200 characters, trimmed
  centsPerPointSnapshot: number,// the factor in force when it was booked
  requestId: string | null,     // idempotency key of the booking request
  source: 'live',
}
```

The date is always today: a redemption is a payout that happens now, not a correction of the past. `amount` was signed from ADR-0011 on for exactly this entry. A new partial unique index on `requestId` (where it is a string) makes a repeated request one booking. Redemptions are **never** loaded, compared, updated or deleted by `reconcilePoints`; this is covered by a test that recomputes with redemptions present, with an orphan entry repaired next to them, and after the earned points were taken away.

The snapshot `centsPerPointSnapshot` keeps what a point was worth when the redemption was booked. The entry list shows each redemption at its own snapshot; balances show money at the factor in force now, so that earned minus redeemed is always the balance in money.

### Booking and undoing

`POST /api/points/redemptions` takes `{ personId?, points, note?, requestId? }`. The person is the active profile unless an administrator names another. A member who names somebody else gets `403 permission_denied`; the role guard is `requireActor`, and the rule about whom a profile may book for lives in the route, like the other attribution rules. The person must be an active user (`400 validation_error` with `unknown_user` or `inactive_user`). `points` is an integer from 1; the note is trimmed and an empty note is stored as `null`.

The booking runs inside the same per-database queue as the reconciliation (`exclusively` in `domain/points.ts`). Within it the server:

1. looks up a known `requestId` first: the same person, points and note replay the stored booking with `200` and write nothing; anything else is `409 idempotency_key_conflict`, the same pattern as ADR-0009;
2. sums every ledger entry of the person, over the whole ledger and not the range on screen, and refuses a booking above that balance with `409 insufficient_balance` (the body carries `balance` and `requested`);
3. inserts the entry and audits it as `points` / `create` with `meta: { reason: 'redemption' }`.

The queue makes the check and the insert one step for a single process, which is the process model of ADR-0005. It does not cover a live execution sync that lands between the check and the insert, so a balance can still go negative afterwards; see Consequences.

`DELETE /api/points/redemptions/:id` takes a redemption back. An administrator can do that at any time. The person it belongs to can do it on the day it was booked, in the household timezone; later it answers `403 redemption_locked`. Anybody else gets `403 permission_denied`, and an id that is not a redemption, including every derived entry, is `404`. It is audited as `points` / `delete` with the entry before and `meta: { reason: 'redemption_undone' }`.

### Balances

`GET /api/points/balances` keeps `points`, which is now the balance: the sum of all entries in the range, earned minus redeemed. It gains `earned` (executions and bonuses), `redeemed` (a positive number) and, when `centsPerPoint > 0`, `money: { earned, redeemed, balance }` in cents at the factor in force now, otherwise `null`. The response carries `currencyCode` and `centsPerPoint` at the top. The entry view gains `note` and `centsPerPointSnapshot`, both `null` for a derived entry, and never shows the request key.

### Statistics reset

A redemption belongs to the history it was booked in, like the points it spends. **Product default, flagged:** starting over removes every redemption, and purging before a date removes the redemptions dated before it. The existing single reset audit entry gains `removedRedemptions` next to `removedPointEntries`, which now counts every removed ledger entry. A purge can therefore leave a person with a negative balance when a later redemption spent points that were earned before the boundary.

### Transfer

Redemptions are exported, because they cannot be derived: export `schemaVersion` becomes `5` and the file gains `collections.pointEntries`, which holds only entries of kind `redemption`. The derived entries are still rebuilt on import. Import accepts versions 1 to 5. An import replaces the ledger and puts the redemptions of the file back before the reconciliation rebuilds the rest, so a file of version 4 or older, which has none, drops the redemptions it replaces, like every other replaced collection. A version 5 file without `collections.pointEntries` is refused. Import checks every redemption in the API shape and in the storage shape, refuses one of a person who is not in the file, and refuses a duplicate key or a duplicate request key (`unknown_user`, `duplicate_key`, `duplicate_request_id`) before anything is written. Settings of the file carry `currencyCode` and `centsPerPoint` and are validated like the rest.

### Web

The Points tab gets a Redeem action for the active profile; an administrator also picks a person in the dialog. The dialog shows the available balance, the amount with a preview of the money it is worth, and an optional note, and cannot book more than the balance. A double click is safe: the request key belongs to the intent (person, points, note), is kept across retries, and is released after a success, the same way as recorded work (ADR-0009). Redemptions are listed with an icon and text and, when allowed, an undo button. A settings card for administrators sets the currency and the cents per point, next to the bonuses.

## Alternatives considered

- **A redemption as a derived entry, rebuilt from something else.** There is nothing to derive it from. Keeping the audit log as the source would make business rules read the log, which ADR-0012 already rules out.
- **Deleting the redemption through a compensating positive entry.** A booking that is undone on the day it was made is a mistake, not a payout and a refund. A compensating entry would leave two entries that cancel each other in every balance and list. The audit log already holds the story.
- **Storing money on the entry instead of a snapshot of the factor.** The entry would carry a number that can disagree with its points. The snapshot keeps one fact, the factor, and the amount is a product of two integers.
- **Converting earned points to money when they are earned.** Changing the factor would then need a migration, and the ledger would no longer count one thing.
- **A global lock for the balance check, or a MongoDB transaction.** There are no transactions here, and a lock per database is the mechanism the reconciliation already uses.
- **A request key that is required.** An API client that never retries would have to invent one. It stays optional like in ADR-0009, and the web client always sends one.

## Consequences

- A booking never makes the balance negative, but undoing earned work later can: an execution that is uncompleted after the points were redeemed leaves a negative balance. The booked redemption is never rewritten, the balance shows the negative number, and an administrator can undo the redemption. This is a product default; the alternatives are to refuse the uncomplete or to block the redemption of points that were earned in the last days.
- A purge before a date can leave a negative balance for the same reason, which is why the reset is flagged as a product default.
- Export version 5 cannot be imported by an older application. Downgrading was never supported.
- The reconciliation does not need to know about redemptions, because it only manages derived kinds. A new booked kind later needs its own writer and no change to it.
- Money in balances follows the factor in force now. Raising the factor raises every balance in money, including the points that were earned at the old one; the redemptions in the entry list keep the value they were booked at.
- **Open product questions,** answered here with defaults:
  - **Statistics reset.** The default is that a reset removes redemptions with the history. The alternative is that a reset keeps them and writes a carry-over entry per person.
  - **Undo window.** The default is the day of the booking for the owner, and any time for an administrator. The alternative is no undo for members, or a window of a fixed number of hours.
  - **Redeeming for others.** The default is that only an administrator books for someone else. The alternative is that planners can too.
  - **Negative balance after an uncomplete.** The default is that it is allowed. The alternative is that the uncomplete is refused while the points are spent.
