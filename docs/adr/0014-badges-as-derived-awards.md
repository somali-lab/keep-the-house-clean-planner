# ADR-0014 — Badges as derived awards

Status: Proposed

## Context

An administrator can create badges: a name, a picture and a rule on chosen tasks, the number of executions or the number of executed minutes. Badges are shown to the people who earned them, and the household wants a few examples to start from ("alles op tijd", "Toiletjuffrouw" after ten times the toilet task, "Dweilkampioen" after so many minutes of mopping). Names and thresholds stay editable. The rules are evaluated on the same audited execution data as the points, a change of a rule is audited, and recomputing must never award a badge twice.

ADR-0011 made the points ledger a keyed projection of the occurrences, ADR-0012 added the bonuses as derived entries, and ADR-0013 added booked entries. A badge award is a new kind of fact with the same shape as the first two: a pure function of data that already exists. Several facts constrain the design.

**There is no stored "executions of a person" counter.** The facts are the done occurrences. The person who did the work is `completedBy`, else the assignee (ADR-0011); that covers a check-off on behalf of the assignee, a take-over, recorded extra work and one-off tasks. The audit log records every change but is not a data source.

**There are no transactions** (ADR-0008) and one application process runs (ADR-0005). The points reconciliation and the execution sync already run in one per-database queue, so a read-compare-write of the awards can run in the same queue.

**An award must not depend on the wall clock.** A badge that is awarded "now" would change its date every time somebody recomputes it, and could not be rebuilt after an import.

**The week bonuses are final only after their period ended** (ADR-0012), and only exist while bonus amounts are configured.

## Decision

### Two collections

`badges` holds the definitions:

```ts
{
  _id, name,                    // 1..60 characters, trimmed
  description,                  // at most 200 characters, '' when none
  rule: { type: 'executions' | 'minutes', taskIds: ObjectId[], threshold }
      | { type: 'onTimeWeeks', threshold },
  active: boolean,
  exampleKey: string | null,    // set on an example badge; unique when a string
  image: { data: Binary, contentType, size, hash } | null,
  createdAt, updatedAt,
}
```

`badgeAwards` holds the derived awards: `{ key: 'badge:<badgeId>:<personId>', badgeId, personId, awardedAt }` with a unique key. There is at most one award per badge and person, however often it is computed.

### Rules

- **`executions`** counts the done occurrences credited to the person, **`minutes`** adds their `durationMinutesSnapshot`. `taskIds` limits the rule to chosen tasks; an empty list means every task. A one-off task has no task and therefore only counts for a rule on every task. Recorded extra work counts like any execution. The snapshot is used, so a later change of a task's duration never rewrites what was done.
- **`onTimeWeeks`** counts the person's `bonus_week_ontime` ledger entries (ADR-0012). It therefore only ever fires while bonus amounts are configured, and the editor says so.
- **The moment of an award** is the moment of the execution that crossed the threshold: executions are ordered by completion instant (the date when there is none) and then by id, and the threshold-th one (for minutes the one that makes the running sum reach the threshold) gives `awardedAt`. For `onTimeWeeks` it is the date of the threshold-th bonus entry, the last day of that week. Because it comes from the data, recomputing gives the same date, and an import rebuilds it.

### The awards are a projection, evaluated in the points queue

`evaluateBadgeAwards(ctx, personIds | null, audit)` computes the expected awards of the given people (everybody for null) for the active badges, compares them with the stored ones and inserts, updates or deletes the differences, with a compare-and-set on the award that was read. It runs only from inside the points queue:

- **As the fifth step of `reconcilePoints`**, after the bonuses of step 4 are final, so startup, the nightly run, an import and `POST /api/points/recompute` rebuild all awards. Its changes are one summary audit entry, separate from the points summary so that result keeps its shape.
- **After every `syncExecutionPoints`**, for the person the execution is credited to now and the person its ledger entry belonged to before. When an undo, a retract or a correction concerns work that earned no ledger entry (a task of 0 points) the previous holder is unknown, and everybody is evaluated. A failure is logged and never fails the check-off; the nightly run repairs it.
- **After a badge is created, deleted or changed in its rule or active flag**, and after a statistics reset, through `reconcileBadges`, which takes the same queue.

Awards appear and disappear with the data. Deactivating or deleting a badge withdraws its awards; reactivating it gives them back with the same dates. Undoing work below the threshold revokes the award, and doing it again awards it at the new moment.

### Audit

Every real change is audited and a no-op writes nothing. A badge is `entity: 'badge'` with `create`, `update` (changed fields only, rule fields nested) and `delete`. An image is recorded by `{ contentType, size, hash }`, never by its bytes. A live award change is its own `badgeAward` entry with `create`, `update` or `delete` and `meta: { reason }` (the reason of the points sync). A bulk run records one `badgeAward` / `recompute` entry with a fixed id and `meta: { trigger, created, updated, removed, changes, changesTotal, changesTruncated }`, `trigger` being `startup`, `nightly`, `import`, `admin`, `badge` or `reset`; at most 100 changes are listed.

### Images

The picture is uploaded by the administrator as base64 in the create or update request (`image: { contentType, data }`; `image: null` removes it). It is limited to 256 KB and to PNG, JPEG and WebP. The server decodes it, refuses anything that is not valid base64, larger than the limit, or whose first bytes are not a PNG, JPEG or WebP signature, and refuses a declared type that is not the real type. An SVG is never accepted because it can carry script. The bytes are stored as BSON `Binary` in the badge, with the content type, the size and the SHA-256 hash.

`GET /api/badges/:id/image` serves the bytes without a profile (an `<img>` cannot send one) with `Content-Type` of the checked type, `ETag` of the hash, `Cache-Control: public, max-age=31536000, immutable`, `X-Content-Type-Options: nosniff` and `Content-Security-Policy: default-src 'none'; sandbox`, and answers `304` to a matching `If-None-Match`. The badge view carries `image.url` with `?v=<hash prefix>`, so a changed picture has a new address and the long cache is safe. A badge without a picture shows a standard medal icon in the web app; nothing is generated or stored for it.

### Example badges

`POST /api/badges/examples` (administrator, `{ language }`) creates the examples that do not exist yet, by their stable `exampleKey`: "Alles op tijd" (4 weeks on time), "Toiletjuffrouw" (10 executions of the toilet task) and "Dweilkampioen" (300 minutes of the mopping task). Nothing is created at startup. The tasks are found by name (`toilet` or `wc`; `dweil`, `zwabber` or `mop`); an example that finds none is created inactive, because an empty task list would count every task. Calling it again, or after an example was renamed, changes nothing; the key is unique, so a concurrent call cannot duplicate one. A deleted example is created again by the next call.

### Transfer and statistics reset

The definitions travel in the export, with their images as extended-JSON binary: `schemaVersion` becomes `6`, `collections.badges` is required from version 6, and import accepts versions 1 to 6. The awards are not exported: an import clears them and the reconciliation rebuilds them from the imported executions, which gives the same dates. A file of version 5 or older has no badges, so the import replaces the existing badges with none, like every other replaced collection. The import refuses, before anything is written, a rule that names a task which is not in the file, an image whose size or hash does not match its bytes or that is not a PNG, JPEG or WebP of its declared type, and a duplicate example key.

A statistics reset rebuilds the awards from what remains: purging old history can revoke awards that depended on it, starting over revokes all. The definitions stay.

### API and web

`GET /api/badges`, `/api/badges/awards?personId` and `/api/badges/progress?personId` need no profile; every write needs an administrator. `GET /api/badges/progress` returns for the active badges how far a person is (`current`, `threshold`, `awardedAt`), evaluated on the data, so the earned state and the progress always agree.

The Badges page under management (administrators) creates, edits, deactivates and deletes badges, uploads and previews the picture with the size and type checked before sending, chooses tasks, and adds the examples. A person's badges are shown in the Points tab for the chosen person and in a small "Mijn badges" section on the Today page. Pictures have the badge name as their alternative text; earned badges say when they were earned and open badges say "not earned yet" with their progress as "7/10", so the state is never only a dimmed picture.

## Alternatives considered

- **A running counter per person and badge**, incremented on every check-off. It drifts after an undo, a correction, an import or a retroactive badge, and it cannot award at "the moment of crossing" after the fact. The projection is rebuilt from the same data and cannot disagree with it.
- **Awards that never go away once earned.** This is what many games do, but it makes an award depend on a moment nobody can reproduce, and an undo or a corrected completion would leave a badge that the data no longer supports. It would also need a stored fact that an import has to carry. It is the open product question below.
- **Awarding at the time the recompute runs.** The date would move on every rebuild and an import would reset them all.
- **Storing the image outside the database** (a file on a volume or object storage). A second thing to back up, restore and export, in an application that is one container with one database (ADR-0005). A few hundred kilobytes per badge in the document is within reach of a household and travels with the existing backup and export.
- **A separate collection for image bytes.** Cleaner for listing, but the list never loads the bytes anyway (the view has no bytes) and a second collection would need its own export and consistency rules.
- **Multipart upload.** Needs a new dependency and a second body parser for one endpoint; base64 in the JSON body fits the existing validation and error contract, at 33% overhead on at most 256 KB.
- **SVG pictures.** They scale well, but an SVG can carry script and would need sanitising. Raster formats are enough for a badge.
- **Evaluating only the affected person in every case.** Work that earned no ledger entry does not say who held it before. Evaluating everybody in that case keeps the award correct at the price of one aggregate over the done occurrences, which is cheap at household size.
- **Folding the badge summary into the points summary.** It would change the shape of an existing result and audit meta that other code and tests rely on; a summary of its own keeps both stable.
- **Generating placeholder pictures for the examples.** A build step or stored placeholders for something a standard icon covers. The icon is neutral and always available.

## Consequences

- **Awards can be taken away.** An undo, a deletion or correction of a completion, a purge of history, deactivating or deleting a badge, or an administrator lowering and raising a threshold changes who holds a badge. This is a product default and an open question for the maintainer: the alternative is to make awards permanent facts, which then have to be exported and are not rebuilt.
- **`onTimeWeeks` is not live.** The week bonuses are only written by a reconciliation, so this badge appears with the nightly run (Monday 03:00 for the week that ended), at startup, after an import or through the recompute endpoint, not at the check-off. It never fires without configured bonuses.
- **A first evaluation is silent per entry.** Creating a badge that people already earned writes one summary entry, not one per person; live changes after a check-off write one entry each.
- **A failed live evaluation is repaired later.** It is logged, and the nightly run (or the recompute endpoint) repairs it. A zero-point task whose completion is moved from one person to another leaves the first person's award until then.
- **More audit entries per check-off.** A check-off that crosses a threshold writes one more entry, and one that does not writes none.
- **Version 6 exports.** An application older than this decision cannot import a version-6 export. Images make an export larger by up to 256 KB per badge.
- **Open product questions,** answered here with defaults: awards follow the data (above); the examples are 4 weeks on time, 10 toilet executions and 300 mopping minutes; a badge without a picture shows a standard medal; an example about tasks that do not exist is created inactive.
