# Architecture decision records

Each record states one durable architectural decision, the problem it solves, and what it costs. Records are written in the present tense and describe the system as it is meant to work, not the order in which it was built.

A record is only added when a decision picks one option over a real alternative and the reason is not derivable from the requirement itself. Behaviour the system must exhibit belongs in `../huishoudplanner-requirements.md`; repository conventions belong in `../../AGENTS.md`; implementation detail that the code already states — library versions, function names, constants — belongs nowhere but the code.

Numbers are not reused: 0013 and 0015 were retired when their content turned out to be behaviour and moved to the requirements, which is why the list skips them.

Records are never rewritten to hide history. When a decision is replaced, the old record is marked `Superseded by ADR-xxxx` and the new record explains what changed and why.

| #                                                                                   | Title                                                                       | Status                         |
| ----------------------------------------------------------------------------------- | --------------------------------------------------------------------------- | ------------------------------ |
| [0001](0001-monorepo-without-a-build-step.md)                                       | Monorepo without a build step for server and shared code                    | Superseded in part by ADR-0016 |
| [0002](0002-shared-api-contract-and-the-bson-boundary.md)                           | Shared API contract and the BSON boundary                                   | Accepted                       |
| [0003](0003-profile-selection-instead-of-authentication.md)                         | Profile selection instead of authentication                                 | Accepted                       |
| [0004](0004-every-write-goes-through-the-data-layer-and-produces-an-audit-entry.md) | Every write goes through the data layer and produces an audit entry         | Accepted                       |
| [0005](0005-one-application-container-separate-backup-container.md)                 | One application container, separate backup container                        | Accepted                       |
| [0006](0006-server-side-pdf-rendering.md)                                           | Server-side PDF rendering                                                   | Superseded by ADR-0020         |
| [0007](0007-release-automation-and-build-identity.md)                               | Release automation and build identity                                       | Accepted                       |
| [0008](0008-optimistic-activation-preview.md)                                       | Optimistic activation preview                                               | Accepted                       |
| [0009](0009-extra-executions-and-one-off-tasks.md)                                  | Extra executions and one-off tasks as ad-hoc occurrences                    | Accepted                       |
| [0010](0010-browser-notifications-while-the-planner-is-open.md)                     | Browser notifications while the planner is open                             | Accepted                       |
| [0011](0011-points-ledger-as-a-projection-of-executions.md)                         | Points ledger as a projection of executions                                 | Accepted                       |
| [0012](0012-week-and-cycle-bonuses-as-derived-ledger-entries.md)                    | Week and cycle bonuses as derived ledger entries                            | Accepted                       |
| [0014](0014-badges-as-derived-awards.md)                                            | Badges as derived awards                                                    | Accepted                       |
| [0016](0016-hexagonal-dotnet-backend-on-mongodb.md)                                 | Hexagonal .NET backend on MongoDB, built in parallel                        | Accepted                       |
| [0017](0017-api-v2-and-a-thin-web-application.md)                                   | API v2 and a thin web application                                           | Accepted                       |
| [0018](0018-identity-port-with-a-provider-neutral-oidc-path.md)                     | Identity port with a provider-neutral OIDC path                             | Accepted                       |
| [0019](0019-opentelemetry-over-otlp-to-an-edot-collector.md)                        | OpenTelemetry over OTLP to an EDOT Collector                                | Accepted                       |
| [0020](0020-questpdf-for-pdf-sheets.md)                                             | QuestPDF for the PDF sheets                                                 | Accepted                       |
| [0021](0021-mongodb-replica-set-and-transactional-writes.md)                        | MongoDB replica set and transactional writes (amends ADR-0004 and ADR-0008) | Accepted                       |
