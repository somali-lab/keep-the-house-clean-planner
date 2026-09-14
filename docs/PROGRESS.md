# Voortgang

Laatst bezig met: T5.2. Alle taken zijn klaar.

Legenda: `[ ]` open · `[x]` klaar · `[~]` geblokkeerd (zie BLOCKERS.md)

## Fase 0 — Bootstrap
- [x] T0.1 Repository initialiseren
- [x] T0.2 Monorepo & tooling
- [x] T0.3 `packages/shared` basis
- [x] T0.4 Server-skelet
- [x] T0.5 Web-skelet
- [x] T0.6 Docker

## Fase 1 — Domeinmodel, taken, template, generatie, vandaag
- [x] T1.1 Identity-module & audit-helper
- [x] T1.2 Seed
- [x] T1.3 API: users, rooms, settings
- [x] T1.4 API: tasks
- [x] T1.5 Gedeelde planvalidatie
- [x] T1.6 API: cyclePlans
- [x] T1.7 Cyclusgeneratie & activatie
- [x] T1.8 API: occurrences
- [x] T1.9 Web: profielkeuze
- [x] T1.10 Web: takenbeheer
- [x] T1.11 Web: template-editor
- [x] T1.12 Web: Vandaag

## Fase 1b — Audit trail
- [x] T1b.1 Audit-API + volledige dekking
- [x] T1b.2 Web: geschiedenis

## Fase 1c — PDF-export
- [x] T1c.1 PDF-renderer
- [x] T1c.2 Web: export-dialoog

## Fase 2 — Due engine, achterstand, verslepen, promote
- [x] T2.1 Due engine
- [x] T2.2 Web: achterstand & overdue
- [x] T2.3 Verslepen / herplannen
- [x] T2.4 Promote-to-template
- [x] T2.5 Interval-wijziging semantiek

## Fase 3 — AI-assistent
- [x] T3.1 Provider-laag
- [x] T3.2 Voorstellen & herbalanceren
- [x] T3.3 Taaksuggesties & uitleg
- [x] T3.4 Diff & toepassen

## Fase 4 — Statistiek, eerlijkheid, notificaties, data
- [x] T4.1 Statistiek-API
- [x] T4.2 Web: statistiek & eerlijkheid
- [x] T4.3 Notificaties (stretch maandelijkse PDF: niet gedaan)
- [x] T4.4 Data: export/import, backup, retentie
- [x] T4.5 (Stretch) Offline afvinken (Playwright-offlinescenario: `apps/web/e2e/offline.spec.ts`, T5.1)

## Fase 5 — Eindverificatie
- [x] T5.1 E2E-suite (Playwright)
- [x] T5.2 Oplevering

## Eindstatus (2026-09-13)

- T0.1 t/m T5.2 zijn klaar.
- `npm run verify`: groen. 66 testbestanden, 501 tests (shared; server met mongodb-memory-server; web met jsdom), plus lint en typecheck, ook van de E2E-suite.
- `npm run test:e2e`: groen. Acht Playwright-scenario's: de zeven uit het plan en offline afvinken uit T4.5.
- `node scripts/smoke.mjs`: geslaagd. Handmatig gedraaid na de run, omdat `docker version` niet op de allowlist stond.
- Niet gedaan (stretch): maandelijkse PDF als ntfy-bijlage (T4.3).

### Samenvatting DECISIONS.md

- **Stack.** npm-workspaces-monorepo (`packages/shared`, `apps/server`, `apps/web`). Node 24 met native type stripping, TypeScript 6. Server: Fastify 5, MongoDB-driver 7, Zod 4, Luxon. Web: React 19, Vite 8, React Router, TanStack Query, dnd-kit, PWA. Playwright voor de PDF's en de E2E-tests.
- **Schrijven en audit.**
  - Mongo-schrijfacties zijn alleen toegestaan in `apps/server/src/data/`; een lint-regel bewaakt dat, en elke schrijfactie legt een audit-entry vast.
  - Een dekkingstest controleert dat elke schrijvende route auditeert, of aantoonbaar niets schrijft.
  - De audit log is append-only. De enige uitzondering is de optionele retentie-job.
- **Tijd en generatie.** Kalenderdagen zijn day keys; occurrences zijn Dates op lokale middernacht in Europe/Amsterdam. Generatie maakt nooit dagen vóór vandaag en is idempotent via een unieke index. Voor het activeren van een plan geldt een vaste vervangregel (zie T1.7 in DECISIONS.md).
- **Plan en werkelijkheid.** Het plan van vier weken zegt wanneer; occurrences zijn wat er echt gebeurt. Verslepen verandert het plan niet. De achterstandslijst kijkt naar de laatste keer gedaan, niet naar het plan. Herhaald verplaatsen levert een voorstel op om het plan aan te passen.
- **AI.** Pluggable providers; de sleutel komt alleen uit de omgeving. Voorstellen worden gevalideerde concepten die pas na expliciet "Toepassen" actief worden. Tests gebruiken uitsluitend de mock.
- **Statistiek.** Vaste, gevalideerde kleurslots per persoon; elke grafiek heeft ook een tabelweergave, en identiteit gaat nooit alleen via kleur.
- **Beheer.**
  - Backups met `mongodump` en retentie.
  - JSON-export en -import met volledige validatie vóór het eerste schrijven.
  - Notificaties die nooit hard falen.
  - Offline afvinken via een IndexedDB-wachtrij die verstuurt namens het profiel dat de wijziging maakte.
- **Verificatie.** E2E met een eigen server, database en vaste klok per test. De smoke-test draait in een eigen compose-project, zodat een echte installatie onaangeroerd blijft.

### Samenvatting BLOCKERS.md

- Geen openstaande blokkades. De enige blokkade (T5.2, Docker-smoke-run door een permissie) is opgelost: de smoke-test is geslaagd.
