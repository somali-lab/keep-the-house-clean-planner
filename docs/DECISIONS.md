# Decisions

A review list of choices made during the work that is running right now. One line per decision: what was decided, and where it has been written down permanently.

The decision itself is never stored here. It goes straight to its permanent home, because a decision that lives in two places will drift apart:

- Behaviour the system must exhibit goes to [huishoudplanner-requirements.md](huishoudplanner-requirements.md).
- A choice between real alternatives, with a rationale that the requirement does not already imply, becomes a record in [adr](adr/README.md).
- A repository convention goes to [AGENTS.md](../AGENTS.md).
- A choice the code already states on its own is not recorded at all.

The list exists so the maintainer can see in one place what was decided without reading the whole diff. Once an entry has been reviewed it is removed, and this file is empty again.
- Backend rewrite decisions D1–D16 (parallel build, MongoDB kept, API v2, thin web app, identity port, OTLP to Elastic, QuestPDF, skill-style hexagon, ObjectId ids, replica set) are recorded in [plans/dotnet-rewrite.md](plans/dotnet-rewrite.md) §2; the ADRs 0015–0020 follow in slice 0.1.
