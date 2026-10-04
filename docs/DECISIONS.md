# Decisions

A review list of choices made during the work that is running right now. One line per decision: what was decided, and where it has been written down permanently.

The decision itself is never stored here. It goes straight to its permanent home, because a decision that lives in two places will drift apart:

- Behaviour the system must exhibit goes to [huishoudplanner-requirements.md](huishoudplanner-requirements.md).
- A choice between real alternatives, with a rationale that the requirement does not already imply, becomes a record in [adr](adr/README.md).
- A repository convention goes to [AGENTS.md](../AGENTS.md).
- A choice the code already states on its own is not recorded at all.

The list exists so the maintainer can see in one place what was decided without reading the whole diff. Once an entry has been reviewed it is removed, and this file is empty again.
- Backend rewrite D1, D15, D17 and D20 (parallel build, side-by-side run, switch, integration branch): [plans/dotnet-rewrite.md](plans/dotnet-rewrite.md) §9 and §10.
- Backend rewrite D2, D8, D9, D11 (MongoDB kept, hexagon, one project per ring, ObjectId ids): [ADR-0016](adr/0016-hexagonal-dotnet-backend-on-mongodb.md).
- Backend rewrite D3, D4 (API v2, thin web app): [ADR-0017](adr/0017-api-v2-and-a-thin-web-application.md).
- Backend rewrite D5, D19 (identity port, provider-neutral OIDC note): [ADR-0018](adr/0018-identity-port-with-a-provider-neutral-oidc-path.md).
- Backend rewrite D6, D18 (OpenTelemetry over OTLP to an EDOT Collector): [ADR-0019](adr/0019-opentelemetry-over-otlp-to-an-edot-collector.md).
- Backend rewrite D7 (QuestPDF): [ADR-0020](adr/0020-questpdf-for-pdf-sheets.md).
- Backend rewrite D12 (replica set and transactions): [ADR-0021](adr/0021-mongodb-replica-set-and-transactional-writes.md).
- Backend rewrite D10, D13, D14 (test stack, adapted skills): skill `xunit-tdd-workflow` and `.agents/skills`, slice 0.2.
- ADR numbers 0016–0021 are one higher than the plan's 0015–0020 because 0015 is retired: [plans/dotnet-rewrite.md](plans/dotnet-rewrite.md) §2.
- Slice 7.6, PWA cache of `/api/v2/` reads kept per profile (cache key carries `X-Profile-Id`; audit, health, AI, import, jobs and exports stay uncached; offline replay classification): [huishoudplanner-requirements.md](huishoudplanner-requirements.md) §7.3.
