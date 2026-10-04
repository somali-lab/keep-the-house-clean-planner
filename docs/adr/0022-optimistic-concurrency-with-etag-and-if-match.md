# ADR-0022 — Optimistic concurrency with ETag and If-Match

Status: Accepted

## Context

The planner is edited by several people on a trusted network, and the editors are long-lived: a person opens the plan, the task list or the settings, thinks, and saves minutes later. The Node server has no general protection against two such saves crossing. A PATCH sends only the fields that changed, so a stale editor rarely overwrites a field it did not touch, but it does overwrite one it did (two people renaming the same task, a plan editor that replaces all slots with the list it loaded earlier, the settings screen that saves a stale form), and nothing tells the loser. The one exception is the bonus schedule of the settings, a compare-and-set that answers `409 bonus_schedule_conflict`. With ADR-0021 every write is a transaction against a snapshot, which makes a conditional write cheap and exact; with ADR-0017 the web app is thin, so the client must be able to carry a validator without any domain rule.

## Decision

Every entity write of API v2 is conditional on the version the client read.

- Each versioned document (users, rooms, tasks, cycle plans, badges and the settings) carries an integer `version`. Every real change of the document raises it by one, in the same write as the change. A write that changes nothing writes nothing and keeps the version. A document without a version (created by the Node server or imported before this decision) reads as version 0, and its first change makes it 1; a document this application creates starts at 1.
- The ETag of an entity is the strong validator `"<version>"`. The read of one entity answers it in the `ETag` header, and the entity carries the same number as the member `version`, so a list-based editor builds its `If-Match` from the list without an extra read.
- A PATCH, PUT or DELETE on an entity must send that validator as `If-Match`. A missing header is `428 precondition_required`. A malformed one is `400 validation_error` on `If-Match`; a weak validator, a list and the wildcard `*` are not accepted, because a client that sends `*` has not read anything, which is the case this decision exists to catch. A version that is no longer the stored one is `412 precondition_failed`, with the current ETag in the response header, and nothing is written. A successful write answers the new ETag.
- The comparison is made inside the transaction of the write, as a conditional write (the version is part of the filter of the update or delete), not as a read followed by a write. The use case also compares after it has read the entity, because the no-op rule needs the stored state: a stale `If-Match` on a patch that would change nothing is still a 412, and a current one on such a patch is a `200` with the current ETag and no write and no audit entry.
- The intent endpoints keep what they have and take no `If-Match`: the creates and the actions (complete, skip, assign, claim, reschedule, retract, redemptions, bulk change of a room, activation, import, jobs) are `POST`, idempotent through a `requestId` or guarded by their own state. The four non-POST writes that are not an edit of a document the client read are listed in an audit test with a reason each: the correction of a completion, the undo of a redemption, the clearing of the audit log and the reset of the statistics.
- The version is metadata. It is not part of the audited fields, so a version bump creates no audit noise and a no-op never looks like a change. An import replaces whole collections and gives every document of a versioned collection one version above the highest one the collection had, so an ETag handed out before the import never matches an imported document.
- The mechanism is built once: the Domain knows the version and the `PreconditionFailed` value, the HTTP adapter has one endpoint extension that requires, parses and documents `If-Match` and sets the `ETag`, and the Mongo adapter has one helper for the conditional filter and the increment. A structural test fails when a PATCH, PUT or DELETE of the API lacks the requirement, in the endpoint metadata and in the OpenAPI document.

## Alternatives considered

- A `version` field in the request body instead of a header: works, but it mixes a precondition with the data, cannot be applied to a DELETE without a body, and gives up the standard vocabulary (`ETag`, `If-Match`, 412, 428) that HTTP clients, proxies and the generated client already understand.
- Last write wins, as today: no work, and a stale save silently destroys a newer one. The conditional write costs one integer per document.
- A content hash as the ETag instead of a counter: needs no stored field, but the hash must be computed over the representation on every read and every write, and two documents with the same content would share an ETag. A counter is cheap, monotonic and makes "did anything change" a comparison of two integers.
- `428` optional (a missing `If-Match` writes unconditionally): the web app and any script would keep working unchanged, and no stale save would ever be caught, because the clients that are stale are exactly the ones that do not send the header. Requiring it makes the protection structural.
- Locking while an editor is open: needs sessions, a lease and a way to break it, for a household of a few people.

## Consequences

- Every client that edits an entity must read it first and send the ETag; the web app has to carry the ETag of each entity it edits and send it on every save (slice 6.7b). A web write to an entity endpoint of API v2 without the header is answered `428`.
- A person who saves a stale form gets a `412` and must read again and redo the change; the response carries the current ETag, and the web app decides how much of the edit to keep.
- The settings are one document, so two administrators editing different settings still conflict. The bonus schedule compare-and-set (`409 bonus_schedule_conflict`) stays as the second defence for a caller without a precondition and for a write that keeps losing to concurrent ones after the last attempt of the transaction runner; a caller that holds the current ETag meets the generic `412` first.
- The browser notification moments of a person are a field of the person's document, so setting them is conditional on the person's version like any other change of the person.
- Operations that change a versioned document without being an entity write (a completion that updates `lastCompletedAt` of a task, the activation of a plan, the dismissal of a promote suggestion) raise the version as well, so an editor that read before them is told.
- Node writes do not raise `version`. After a rollback to Node and a later return to .NET, an ETag held from before can match a document that Node changed in between, so clients re-read after any switch.
- Every store write on a versioned collection carries the increment; a forgotten increment would let a stale editor through, which is why the helper is shared and the contract test runs against every entity.
