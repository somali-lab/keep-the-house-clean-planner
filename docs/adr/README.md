# Architecture decision records

Each record states one durable architectural decision, the problem it solves, and what it costs. Records are written in the present tense and describe the system as it is meant to work, not the order in which it was built.

A record is only added when a decision picks one option over a real alternative and the reason is not derivable from the requirement itself. Behaviour the system must exhibit belongs in `../huishoudplanner-requirements.md`; repository conventions belong in `../../AGENTS.md`; implementation detail that the code already states — library versions, function names, constants — belongs nowhere but the code.

Records are never rewritten to hide history. When a decision is replaced, the old record is marked `Superseded by ADR-xxxx` and the new record explains what changed and why.

| #                                                                                   | Title                                                               | Status   |
| ----------------------------------------------------------------------------------- | ------------------------------------------------------------------- | -------- |
| [0001](0001-monorepo-without-a-build-step.md)                                       | Monorepo without a build step for server and shared code            | Accepted |
| [0002](0002-shared-api-contract-and-the-bson-boundary.md)                           | Shared API contract and the BSON boundary                           | Accepted |
| [0003](0003-profile-selection-instead-of-authentication.md)                         | Profile selection instead of authentication                         | Accepted |
| [0004](0004-every-write-goes-through-the-data-layer-and-produces-an-audit-entry.md) | Every write goes through the data layer and produces an audit entry | Accepted |
| [0005](0005-one-application-container-separate-backup-container.md)                 | One application container, separate backup container                | Accepted |
| [0006](0006-server-side-pdf-rendering.md)                                           | Server-side PDF rendering                                           | Accepted |
| [0007](0007-release-automation-and-build-identity.md)                               | Release automation and build identity                               | Accepted |
| [0008](0008-optimistic-activation-preview.md)                                        | Optimistic activation preview                                        | Accepted |
| [0009](0009-extra-executions-and-one-off-tasks.md)                                   | Extra executions and one-off tasks as ad-hoc occurrences             | Proposed |
