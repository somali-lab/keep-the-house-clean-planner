# ADR-0004 — Every write goes through the data layer and produces an audit entry

Status: Accepted

## Context

Two people share a household schedule and there is no login. "Who marked this done?" and "why did this task move?" are the questions the system exists to answer. A history that can be edited answers neither, and a history assembled from several independent write paths is incomplete in ways nobody notices until it matters.

The guarantee is only as strong as its weakest write. If a route handler, a scheduled job, or a test can reach the database directly, "everything is audited" becomes a convention that holds until somebody is in a hurry.

## Decision

All database writes live in the data layer. Routes parse and authorise, domain code decides, and only the data layer mutates. A lint rule enforces this across the whole repository, including test code, and the rule itself is covered by a test, so weakening it fails the build rather than silently widening the hole.

Every state change records one audit entry through one helper, holding the actor, the moment, the entity, the action, the changed fields before and after, and the origin of the change — the interface, the API, an AI proposal, or a scheduled job.

The log is append-only. No interface path edits or deletes an entry. The single exception is an optional retention job that removes entries older than a configured age; it writes no audit entry of its own, because a log that records its own cleanup grows exactly where it is being trimmed.

A coverage test walks every registered write route and asserts that it either produces the expected audit entry or provably writes nothing. A new write route that is not accounted for fails the test.

## Consequences

- The set of places that can change state is small enough to review, which makes the audit guarantee checkable rather than aspirational.
- Test setup is more verbose than inserting documents directly, and deliberately so: fixtures are audited exactly like real changes, and a test cannot create a state that production could not have reached.
- Deleting a task is not supported; deactivating it is. Removing a task would orphan its history, which is the opposite of the point.
- Entries hold identifiers, not names, so they carry enough context — such as the task name at the time — to stay readable after the referenced entity changes or disappears.
- Adding a write route costs the author a coverage scenario. That is the mechanism, not an inconvenience.

## Amendment 2026-10-03 — transactions

In the rebuilt backend an entity write and its audit entry are committed in one database transaction, so a change without its audit entry, or the reverse, can no longer occur. A write that changes nothing still writes and audits nothing. See ADR-0021.
