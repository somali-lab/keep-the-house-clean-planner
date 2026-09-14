# Huishoudplanner — Implementatieplan (unattended)

Bron: [huishoudplanner-requirements.md](huishoudplanner-requirements.md). Dit plan is geschreven om **zonder menselijke tussenkomst** door een coding agent uitgevoerd te worden. Alle open keuzes zijn hieronder vastgelegd; de uitvoerder stelt geen vragen, maar volgt dit document en legt eventuele afwijkingen vast in `docs/DECISIONS.md`.

---

## 0. Uitvoeringsprotocol (lees dit eerst)

### 0.1 Werkwijze
1. Werk de taken **strikt in volgorde** af (T0.1 → T5.2). Een taak is pas klaar als alle acceptatiecriteria groen zijn.
2. Houd voortgang bij in `docs/PROGRESS.md`: één checkbox per taak-ID, plus een regel "laatst bezig met". Lees dit bestand bij (her)start en ga verder bij de eerste open taak. Zo overleeft het werk een context-reset of herstart.
3. Na elke afgeronde taak:
   - `npm run verify` (= lint + typecheck + unit/integratietests) moet slagen.
   - Commit met Conventional Commit-bericht, bv. `feat(server): cycle generation (T1.7)`, afgesloten met de regel
     `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.
   - Vink de taak af in `PROGRESS.md` (in dezelfde commit).
4. **Geen vragen stellen.** Bij een onduidelijkheid: kies de eenvoudigste oplossing die met de requirements en sectie 1 van dit plan in lijn is, en noteer die in `docs/DECISIONS.md` (datum, taak-ID, keuze, reden).
5. **Blokkade-regel:** als een acceptatiecriterium na 3 serieuze pogingen niet haalbaar is, noteer het in `docs/BLOCKERS.md` (taak, foutmelding, wat geprobeerd is), markeer de taak in `PROGRESS.md` als `[~]` en ga door met de volgende taak die er niet van afhangt. Nooit tests uitschakelen, `skip`pen of assertions afzwakken om groen te krijgen.
6. **Verboden:** `git push`, echte AI-API's aanroepen (alleen de mock-provider in tests), secrets in code of commits, `--no-verify`, destructieve acties buiten deze repo.
7. Documentatie van libraries: gebruik de huidige stabiele versie op het moment van uitvoeren; controleer bij twijfel de officiële docs. Leg gekozen majorversies vast in `DECISIONS.md`.
8. Omgeving: Windows 11, Node 24, npm 11, Docker 29 + Compose v5, git. Alle npm-scripts moeten cross-platform werken (geen bash-only constructies; gebruik `cross-env`/node-scripts waar nodig).

### 0.2 Globale Definition of Done
- `npm run verify` groen.
- Geen `any` zonder `// eslint-disable-next-line` + reden.
- Elke schrijvende API-route heeft een integratietest die ook de audit-entry controleert (zie T1.1).
- UI-teksten Nederlands (via `apps/web/src/i18n/nl.ts`), code/API/identifiers Engels.

### 0.3 Voorgestelde startopdracht
```bash
claude -p "Voer docs/implementation-plan.md uit volgens het uitvoeringsprotocol in sectie 0. Begin bij de eerste open taak in docs/PROGRESS.md." --permission-mode acceptEdits
```
Zorg vooraf dat `.claude/settings.json` een allowlist heeft voor `npm`, `npx`, `node`, `git` (behalve push) en `docker`/`docker compose`, zodat de run niet op permissieprompts blijft hangen.

---

## 1. Vastgelegde beslissingen

### 1.1 Tech stack
| Onderdeel | Keuze |
|---|---|
| Taal | TypeScript (strict) overal |
| Repo | npm workspaces monorepo: `apps/server`, `apps/web`, `packages/shared` |
| Server | Fastify, officiële `mongodb` driver (geen Mongoose), Zod voor validatie, pino JSON-logging (Fastify default) |
| Tijd | Luxon; alle tijdconversie uitsluitend via `packages/shared/src/time.ts` |
| Scheduler | `node-cron` in-process, timezone `Europe/Amsterdam` |
| Web | React + Vite, React Router, TanStack Query, `@dnd-kit/core` (Pointer- + TouchSensor), `vite-plugin-pwa` |
| PDF | Playwright (Chromium) server-side, HTML/CSS-template als TS template strings |
| Tests | Vitest (unit + integratie via `fastify.inject`), `mongodb-memory-server` (of `MONGO_TEST_URL` indien gezet), Playwright E2E, `pdf-parse` voor PDF-asserties |
| AI | Provider-interface met implementaties `none`, `mock`, `anthropic` (`@anthropic-ai/sdk`), `openai-compatible` (fetch), `ollama` (fetch) |
| Deploy | Eén app-image (serveert API + gebouwde web-assets via `@fastify/static`) + `mongo:8`, één `docker-compose.yml` |

### 1.2 Projectstructuur
```
/package.json                 workspaces + root scripts (verify, test, build, dev, test:e2e)
/tsconfig.base.json
/eslint.config.js
/apps/server/src/
  config.ts                   env → zod-gevalideerde config
  app.ts, main.ts
  identity/                   ENIGE plek die profiel → actor bepaalt (vervangbaar door echte auth)
  audit/                      audit.record(), diffFields()
  data/                       repositories; ENIGE plek met Mongo-schrijfoperaties
  domain/                     generation, due, promote, ai, pdf, stats, notify, backup
  routes/                     één bestand per resource
  jobs/                       nightly scheduler
/apps/web/src/
  i18n/nl.ts, api/, identity/, layouts/{MobileLayout,DesktopLayout}.tsx, features/*
/packages/shared/src/
  time.ts, cycle.ts, schemas/*.ts, validation/plan.ts, due.ts
/docker/Dockerfile
/docker-compose.yml
/.env.example
/docs/{PROGRESS,DECISIONS,BLOCKERS}.md
```

### 1.3 Antwoorden op open vragen (requirements §9)
1. **Dagelijkse taken** krijgen gewoon een occurrence per dag. Eén uniform model; het datavolume voor twee personen is triviaal.
2. **Wijziging van interval/duur** geldt vanaf de **volgende** generatie. Al gegenereerde occurrences blijven staan (duur staat al als snapshot vast).
3. **Occurrence slepen naar een dag waarop de assignee niet beschikbaar is:** toegestaan, maar met waarschuwing (API geeft `warnings[]` terug, UI toont die). Alleen de template-editor blokkeert.
4. **Claim-model:** een slot met `assigneeId: null` levert een occurrence met `assigneeId: null` op ("wie pakt 'm"). `POST /api/occurrences/:id/claim` zet de assignee alleen als die nog `null` is (atomaire `updateOne` met filter `assigneeId: null`; anders 409). Afvinken van een ongeclaimde occurrence claimt hem impliciet voor `completedBy`.

### 1.4 Overige invullingen van gaten in de requirements
| Onderwerp | Beslissing |
|---|---|
| Cycli | Extra collectie `cycles`: `{ _id, index, startDate, endDate, planId, generatedAt, generationRunId }`. `index = floor(dagen(anchor → datum) / 28)`. `weekIndex = floor((dagen mod 28) / 7)`. |
| Datums | `occurrences.date`/`plannedDate` = BSON Date van 00:00 Europe/Amsterdam, opgeslagen in UTC. Helpers `toDayKey(date) → 'YYYY-MM-DD'` en `fromDayKey()`. Tijdstippen (`completedAt`, `at`) gewoon UTC. |
| Anchor | `cycleAnchorDate` wordt bij eerste start de maandag van de huidige ISO-week. Moet een maandag zijn (validatie). |
| Intervallen | Opgeslagen in `settings.intervals[]` (`key, label, perCycle|null, periodDays`). Seed: `daily, 2w, 1w, 2wk, 4wk, quarter`. `perCycle: null` ⇒ geen slot-telling in de editor ("n.v.t."), taak wordt door de due engine aangestuurd. |
| Ad-hoc occurrences | `POST /api/occurrences` `{ taskId, date, assigneeId? }` — nodig om kwartaaltaken uit de achterstandslijst in te plannen. `plannedDate = date`, `cycleId` afgeleid. |
| Weekthema | `cyclePlans.weekThemes: [string, string, string, string]` (optioneel, leeg = geen thema). Gebruikt in editor en PDF-header. |
| Dubbele slots | Dezelfde taak twee keer op dezelfde dag in één plan = **harde fout** (maakt de idempotentiesleutel `(cycleId, taskId, plannedDate)` betrouwbaar). Unieke index hierop. |
| Plan-activatie midden in een cyclus | Occurrences met `status: 'open'`, `date >= vandaag`, `date == plannedDate` (niet versleept), gegenereerd door systeem, in huidige + volgende cyclus, worden verwijderd en opnieuw gegenereerd uit het nieuwe plan. Elke verwijdering wordt geaudit (`action: 'delete'`, `source: 'system'`). Alles wat afgevinkt, overgeslagen, versleept of ad-hoc is blijft staan. |
| Undo | Occurrence krijgt `statusBeforeCompletion`. `uncomplete` zet status terug naar die waarde (open of skipped) en herberekent `task.lastCompletedAt` als max `completedAt` van done-occurrences. |
| System-actor | `SYSTEM_ACTOR_ID = ObjectId('000000000000000000000000')` voor nachtelijke jobs. |
| Audit-extra's | Optioneel veld `meta` (bv. `{ runId }`, `{ proposalId }`, `{ completedBy }`). Audit-entity uitgebreid met `'cycle' | 'room' | 'import'`. |
| Transacties | Geen (Mongo single node, geen replica set). Audit wordt direct na de succesvolle write geschreven; faalt dat, dan `log.error` met volledige context. |
| Promote-to-template | Suggestie wanneer voor hetzelfde template-slot (planId, taskId, weekIndex, weekday) in de laatste `settings.promoteThreshold` (default 2) opeenvolgende cycli de occurrence naar **dezelfde weekday** (en evt. dezelfde andere assignee) is versleept. |
| Today view volgorde | 1) eigen open van vandaag, 2) ongeclaimde van vandaag, 3) die van de ander, 4) overdue (ouder dan vandaag, open). |
| Nachtelijke job | 03:00 Europe/Amsterdam: genereer huidige + volgende cyclus (idempotent), due engine-run (loggen), notificatie (fase 4). 03:30: `mongodump`. 04:00: audit-retentie (alleen als `AUDIT_RETENTION_DAYS` gezet). |
| Profiel-header | Client stuurt `X-Profile-Id` bij elke request. Schrijvende requests zonder geldig actief profiel → 400 `profile_required`. |
| Seed-gebruikers | Env `SEED_USERS` (JSON), default `[{"name":"Persoon 1","color":"#2563eb"},{"name":"Persoon 2","color":"#db2777"}]`, budget weekday 60 / weekend 120 min. Alleen bij lege `users`-collectie. |
| Seed-ruimtes | Keuken, Badkamer, Toilet, Woonkamer, Slaapkamer, Hal, "Hele huis" (`virtual: true`). |
| Layouts | Twee aparte layouts: `MobileLayout` (< 900px: Vandaag, Week, Achterstand) en `DesktopLayout` (≥ 900px: Planner, Taken, Statistiek, Geschiedenis, AI, Instellingen). Beide layouts bereikbaar via menu, maar met eigen componenten, geen gedeelde responsive compromis. |

### 1.5 Environment-variabelen (`.env.example`)
```
PORT=3000
MONGO_URL=mongodb://mongo:27017/huishoudplanner
TZ_APP=Europe/Amsterdam
SEED_USERS=
LOG_LEVEL=info
BACKUP_DIR=/backups
BACKUP_RETENTION_DAYS=14
AUDIT_RETENTION_DAYS=            # leeg = onbeperkt
AI_API_KEY=                      # alleen secret; type/endpoint/model staan in settings
NOTIFY_TYPE=none                 # none | ntfy | homeassistant
NOTIFY_URL=
NOTIFY_TOKEN=
DISABLE_SCHEDULER=false          # true in tests
```

---

## Fase 0 — Bootstrap

### T0.1 Repository initialiseren
- `git init`, `.gitignore` (node_modules, dist, .env, backups, coverage, playwright-report), `.editorconfig`, `.gitattributes` (`* text=auto eol=lf`).
- Maak `docs/PROGRESS.md` (alle taak-ID's uit dit plan als checkboxes), `docs/DECISIONS.md`, `docs/BLOCKERS.md`.
- **Acceptatie:** `git log` toont een eerste commit; PROGRESS.md bevat elke taak-ID uit dit plan.

### T0.2 Monorepo & tooling
- Root `package.json` met workspaces, scripts: `dev`, `build`, `lint`, `typecheck`, `test`, `test:e2e`, `verify` (= lint && typecheck && test).
- `tsconfig.base.json` (strict, `noUncheckedIndexedAccess`), ESLint flat config + Prettier, Vitest workspace-config.
- ESLint-regel (`no-restricted-syntax` of `no-restricted-properties`): aanroepen van `insertOne|insertMany|updateOne|updateMany|replaceOne|deleteOne|deleteMany|findOneAndUpdate|bulkWrite` zijn **verboden buiten `apps/server/src/data/`**.
- **Acceptatie:** `npm run verify` slaagt op lege packages met één dummy-test per package.

### T0.3 `packages/shared` basis
- `time.ts`: `today(tz)`, `toDayKey`, `fromDayKey`, `addDays`, `isoWeek`, `isoWeekLabel` ('2026-W38'), `weekdayMon0`/`weekdaySun0` conversie (domein gebruikt 0=zo..6=za zoals requirements; grid toont maandag eerst).
- `cycle.ts`: `cycleIndexFor(date, anchor)`, `cycleStart(index, anchor)`, `slotDate(cycleStart, weekIndex, weekday)`, `weekIndexFor(date, anchor)`.
- `schemas/`: Zod-schema's + types voor users, rooms, tasks, intervals, cyclePlans, cycles, occurrences, auditLog, settings, plus API request/response DTO's.
- **Acceptatie:** unit tests dekken DST-overgangen (laatste zondag maart/oktober), jaarwisseling ISO-week 53, anchor-validatie, `slotDate` voor alle 28 combinaties.

### T0.4 Server-skelet
- `config.ts` (Zod over `process.env`, faalt hard bij ongeldige config).
- Fastify app-factory `buildApp({ db, config, clock })` — `clock` injecteerbaar voor tests.
- Mongo-verbinding, `ensureIndexes()` bij opstart met alle indexen uit requirements §2 + §3.8 + unieke index `occurrences {cycleId, taskId, plannedDate}` + `cycles {index}` uniek.
- `GET /api/health` → `{ status: 'ok', mongo: 'ok' }` (503 als ping faalt).
- Graceful shutdown (SIGTERM).
- Test-helper `createTestApp()` met in-memory Mongo, verse database per testbestand, vaste klok.
- **Acceptatie:** integratietest op `/api/health`; test dat alle verwachte indexen bestaan.

### T0.5 Web-skelet
- Vite + React + TS, React Router, TanStack Query, `vite-plugin-pwa` (manifest `lang: 'nl'`, naam "Huishoudplanner").
- `i18n/nl.ts` (plat object met sleutels), `api/client.ts` (fetch-wrapper die `X-Profile-Id` meestuurt en `warnings` doorgeeft).
- Layout-switch op basis van `matchMedia('(min-width: 900px)')`.
- Dev-proxy `/api` → `localhost:3000`.
- **Acceptatie:** `npm run build -w apps/web` slaagt; Vitest-render-test van de app-shell.

### T0.6 Docker
- `docker/Dockerfile` multi-stage: build (node:24) → runtime op basis van de Playwright-image die bij de geïnstalleerde Playwright-versie hoort (`mcr.microsoft.com/playwright:v<versie>-noble`), plus `mongodb-database-tools` (voor `mongodump`). Draait als non-root user.
- `docker-compose.yml`: `app` (port 3000, `env_file`, healthcheck op `/api/health`, bind mount `./backups:/backups`, `depends_on: mongo: condition: service_healthy`) en `mongo` (`mongo:8`, named volume `mongo-data`, healthcheck `mongosh --eval "db.adminCommand('ping')"`).
- Geen configbestanden in de image.
- **Acceptatie:** `docker compose build` slaagt; `docker compose up -d` → binnen 60 s is `app` healthy en `curl http://localhost:3000/api/health` geeft 200; daarna `docker compose down`.

---

## Fase 1 — Domeinmodel, taken, template, generatie, vandaag

### T1.1 Identity-module & audit-helper
- `identity/`: Fastify-plugin die `request.actor = { actorId, source }` zet. `source` = `'ui'` als header `X-Client: web`, anders `'api'`. Validatie: profiel bestaat en `active`. Exporteert `requireActor` voor schrijvende routes. Geen andere code leest de header.
- `audit/diffFields(before, after)`: geeft `{ before, after }` met alleen gewijzigde (diep vergeleken) velden; negeert `updatedAt`.
- `audit/record(ctx, { entity, entityId, action, before?, after?, meta? })`: enige insert in `auditLog`. Er bestaat **geen** update/delete-functie voor `auditLog` (behalve de retentiejob in T4.4, die expliciet `source: 'system'` is en via config aan staat).
- Repositories in `data/` krijgen altijd een `ctx` mee en roepen `record` aan.
- **Audit-dekkingstest (harness):** helper `expectAudited(app, fn, { entity, action })` die het aantal audit-entries voor en na telt en de inhoud checkt. Daarnaast een test die de Mongo command-monitoring gebruikt: tijdens een write-request moet elke write naar een niet-audit-collectie vergezeld gaan van ≥ 1 insert in `auditLog`.
- **Acceptatie:** unit tests `diffFields` (geneste objecten, arrays, ObjectIds, Dates); lint faalt aantoonbaar op een testbestand met `collection.updateOne` buiten `data/` (verwijder dat bestand daarna weer).

### T1.2 Seed
- Bij opstart, idempotent: `settings` (anchor, `weekStartsOn: 1`, timezone, `vacationRanges: []`, intervals, `aiProvider: { type: 'none' }`, `promoteThreshold: 2`), users uit `SEED_USERS` (alleen als leeg), rooms (alleen als leeg). Audit met `source: 'system'`.
- **Acceptatie:** twee keer booten levert geen duplicaten; test met custom `SEED_USERS` van 3 personen.

### T1.3 API: users, rooms, settings
- `GET/POST/PATCH /api/users`, `GET/POST/PATCH /api/rooms`, `GET/PATCH /api/settings` (inclusief intervals en vacationRanges; anchor-validatie; interval-keys die in gebruik zijn kunnen niet verwijderd worden → 409).
- Deactiveren i.p.v. verwijderen; geen DELETE-routes.
- **Acceptatie:** integratietests incl. audit-controle; test dat een nieuw interval `year` (periodDays 365, perCycle null) via settings toegevoegd kan worden en daarna bruikbaar is voor een taak — zonder codewijziging.

### T1.4 API: tasks
- `GET /api/tasks?roomId=&active=`, `POST /api/tasks`, `PATCH /api/tasks/:id`.
- Verplicht: `name`, `roomId`, `intervalKey` (moet bestaan in settings), `durationMinutes` (integer ≥ 1).
- Bulk: `POST /api/rooms/:id/tasks/bulk` `{ op: 'deactivate' | 'reassign', defaultAssigneeId? }` — één audit-entry per taak.
- **Acceptatie:** validatiefouten 400 met veldnamen; audit toont oude/nieuwe waarden van naam/interval/duur/ruimte; `defaultAssigneeId`-wijziging gelogd als `action: 'assign'`.

### T1.5 Gedeelde planvalidatie (`packages/shared/src/validation/plan.ts`)
Pure functie `validatePlan({ slots, tasks, users, intervals })` → `{ errors[], warnings[], summary }`:
- **errors (hard):** slot op weekday in `unavailableWeekdays` van assignee; zelfde taak 2× op zelfde dag; onbekende/inactieve taak of user; weekIndex/weekday buiten bereik.
- **warnings:** `placed ≠ perCycle` per taak (overgeslagen als `perCycle` null); dagbudget per user overschreden (weekday vs weekend budget).
- **summary:** per taak `placed/required`; per dag per user minuten + budget; per week per user totaal minuten. Slots met `assigneeId: null` tellen voor beide users als "onverdeeld" en worden apart getotaliseerd.
- Deze functie wordt gebruikt door de editor (client), `PUT /slots` (server) én AI-validatie (T3.2).
- **Acceptatie:** table-driven unit tests voor elke regel, incl. `2w` met 5 slots = warning, geen error.

### T1.6 API: cyclePlans
- `GET /api/cycle-plans`, `GET /api/cycle-plans/active`, `GET /api/cycle-plans/:id`, `POST /api/cycle-plans` (`{ name, copyFromId? }`), `PATCH /api/cycle-plans/:id` (naam, weekThemes), `PUT /api/cycle-plans/:id/slots`.
- `PUT slots`: `validatePlan`; errors → 422 met details; anders opslaan en `{ plan, warnings, summary }` teruggeven. Audit: before/after bevat alleen toegevoegde/verwijderde/gewijzigde slots (diff op slot-sleutel `taskId+weekIndex+weekday`).
- Eerste start zonder plannen: maak leeg actief plan "Standaard".
- **Acceptatie:** integratietests voor 422-pad, warning-pad en audit-inhoud.

### T1.7 Cyclusgeneratie & activatie
- `domain/generation.ts`: `generateCycle(ctx, cycleIndex, { runId })`:
  - upsert `cycles`-document; voor elk slot van het actieve plan → occurrence (`plannedDate = date = slotDate`, `assigneeId = slot.assigneeId`, `durationMinutesSnapshot`, `taskNameSnapshot`, `planId`, `status: 'open'`, `origin: 'generated'`).
  - datums in `vacationRanges` overslaan; inactieve taken overslaan.
  - idempotent via unieke index (`insertMany` met `ordered: false`, duplicate-key-fouten negeren). Alleen daadwerkelijk ingevoegde occurrences worden geaudit (`source: 'system'`, `meta.runId`).
- `POST /api/cycle-plans/:id/activate`: deactiveer andere plannen, activeer dit, voer vervangingsregel uit (§1.4) voor huidige + volgende cyclus, genereer. Audit `action: 'activate'`.
- `jobs/nightly.ts`: cron 03:00 → genereer huidige + volgende cyclus. `DISABLE_SCHEDULER=true` in tests. Ook aanroepbaar via `POST /api/jobs/nightly` (voor handmatig/on demand).
- **Acceptatie:** tests: (a) dubbele generatie → zelfde aantal occurrences; (b) vacation-dagen leeg; (c) activatie midden in cyclus laat done/skipped/versleepte occurrences staan en vervangt de rest; (d) generatie over DST-grens geeft juiste lokale datums; (e) `2w`-taak met 8 slots → 8 occurrences.

### T1.8 API: occurrences (dagelijks gebruik)
- `GET /api/occurrences?from=&to=&assigneeId=&status=` (dayKeys), respons verrijkt met `isOverdue` (`status open && date < vandaag`) en `movedFrom` (als `date ≠ plannedDate`).
- `PATCH /api/occurrences/:id` met discriminated union:
  - `{ action: 'complete', completedBy? }` → `statusBeforeCompletion = status`, `status: 'done'`, `completedAt = now`, `completedBy = completedBy ?? actor`; update `task.lastCompletedAt`. Audit `complete`, `meta: { completedBy, wasAssignee }`.
  - `{ action: 'uncomplete' }` → herstel `statusBeforeCompletion`, wis completedAt/By, herbereken `lastCompletedAt`. Audit `uncomplete` (nieuwe entry, originele blijft).
  - `{ action: 'skip', reason? }` → `status: 'skipped'`. Audit `skip`.
  - `{ action: 'reschedule', date }` en `{ action: 'assign', assigneeId }` → geïmplementeerd in T2.3; hier al het schema.
- `POST /api/occurrences/:id/claim` (§1.3.4).
- **Acceptatie:** tests: undo na skip→done herstelt `skipped`; `lastCompletedAt` klopt na complete/uncomplete-reeks; `completedBy` ≠ actor wordt beide gelogd; claim-race (twee parallelle claims → één 200, één 409).

### T1.9 Web: profielkeuze
- Eerste bezoek: fullscreen profielkeuze (naam + kleur). Opslag in `localStorage` (try/catch). Header met één-tik profielwisselaar (avatars). Alle writes via `api/client` sturen header mee.
- Module `web/src/identity/` is de enige plek die het profiel leest/schrijft.
- **Acceptatie:** component tests: zonder profiel → keuzescherm; wisselen update header; profiel dat inactief is geworden → terug naar keuzescherm.

### T1.10 Web: takenbeheer (desktop)
- Lijst gegroepeerd per ruimte, formulier (naam, ruimte, interval, duur verplicht, standaard-assignee incl. "wie dan ook", notities, tags), deactiveren, bulkacties per ruimte, knop "Geschiedenis" (T1b.2).
- **Acceptatie:** component tests voor validatie (duur verplicht) en groepering.

### T1.11 Web: template-editor (desktop)
- Grid 4 weken × 7 dagen, maandag eerst; per dag één kolom per actieve user plus kolom "Samen/wie dan ook". Pool met ongeplande/onvolledig geplande taken (met `placed/required`-badge).
- dnd-kit: slepen pool → cel, cel → cel, cel → pool (verwijderen). Pointer- en TouchSensor.
- Client-side `validatePlan` bij elke wijziging: drop op niet-beschikbare dag wordt geweigerd met toast met uitleg; over-budget cel krijgt duidelijke rand + minutenlabel (niet alleen kleur); per week per user totalen; interval-mismatch als waarschuwingsicoon.
- Opslaan → `PUT slots` (debounced 800 ms), weekthema's bewerkbaar, plan kiezen/kopiëren/activeren (bevestigingsdialoog met uitleg vervangingsregel).
- Uitlegblok (requirements §1): "Het plan zegt wanneer we het willen doen; de lijst Vandaag zegt wat er echt gebeurt."
- **Acceptatie:** component tests: geweigerde drop toont melding; budgetstatus rekent weekend-budget op za/zo. E2E volgt in T5.1.

### T1.12 Web: Vandaag (mobiel)
- Secties volgens §1.4-volgorde; grote tap-targets (≥ 44px); afvinken met één tik, undo-snackbar (5 s) + undo ook later via item; overslaan met optionele reden; "afgevinkt door"-selector (lang indrukken of menu); claim-knop bij ongeclaimde items; overdue-items visueel gemarkeerd (icoon + tekst, niet alleen kleur).
- Optimistic updates via TanStack Query met rollback bij fout.
- **Acceptatie:** component tests voor sectievolgorde en undo-herstel.

---

## Fase 1b — Audit trail

### T1b.1 Audit-API + volledige dekking
- `GET /api/audit?entity=&entityId=&actorId=&source=&from=&to=&cursor=&limit=` — gesorteerd `at desc`, cursor-paginering.
- Geen schrijvende audit-routes.
- **Dekkingstest:** één testbestand dat **elke** schrijvende route (enumerate via Fastify's route-lijst, methode ≠ GET) aanroept met een geldige payload en controleert dat er een audit-entry ontstond. Een nieuwe schrijvende route zonder testcase laat deze test falen (lijst met bekende routes moet volledig zijn).
- **Acceptatie:** dekkingstest groen; filters getest.

### T1b.2 Web: geschiedenis
- Globale feed (desktop) met filters actor, entity-type, datumrange; per entity een "Geschiedenis"-paneel (taak, plan, occurrence). Weergave: "Persoon 1 wijzigde duur van Badkamer schoonmaken: 30 → 45 min", AI-acties met label "via AI-voorstel".
- **Acceptatie:** component test voor weergave van before/after en AI-label.

---

## Fase 1c — PDF-export

### T1c.1 PDF-renderer
- `domain/pdf/`: één gedeelde Chromium-browser (lazy start, hergebruikt, gesloten bij shutdown). HTML via TS-templates + inline CSS; fonts uit de image (geen externe requests).
- `GET /api/export/pdf?fromWeek=2026-W38&weeks=1|2|4&orientation=portrait|landscape&totals=true|false`
  - Bron = **occurrences** (niet template), zonder statusinformatie.
  - Als een gevraagde week niet (volledig) gegenereerd is → 409 `{ code: 'weeks_not_generated', weeks: [...] }`.
  - Portrait: één week per pagina. Landscape + `weeks=2`: twee weken naast elkaar op één pagina.
  - Layout: dagen als rijen (ma eerst), kolom per user + "wie dan ook"; lege dagen zichtbaar; per regel taaknaam, ruimte, checkbox `5mm × 5mm` met `0.4mm` zwarte rand; header: "Week N van de cyclus", datums, weekthema; footer: "Gegenereerd op …" + regel "Afvinken op papier wordt niet automatisch in de app verwerkt."; optioneel minuten per dag per user.
  - Zwart-wit veilig: geen kleurcodering.
  - `Content-Disposition: attachment; filename="huishoudschema-2026-w38-w39.pdf"`.
- `GET /api/export/pdf/day?date=YYYY-MM-DD` → `huishoudschema-2026-09-14.pdf`.
- `GET /api/export/pdf/due` → `achterstand-2026-09-14.pdf` (beschikbaar zodra T2.1 klaar is; tot dan 501).
- **Acceptatie:** tests met `pdf-parse`: paginatelling 1/2/4 (portrait) en 1 (landscape 2 weken); tekst bevat taaknamen van verplaatste occurrences op hun nieuwe dag; bevat geen "done"/afgevinkt-markering; 409 bij niet-gegenereerde week; bestandsnaam klopt. Test draait met Playwright Chromium lokaal (`npx playwright install chromium` in de testsetup).

### T1c.2 Web: export-dialoog
- Keuze bereik (dag / 1 / 2 / 4 weken / achterstand), startweek (uit gegenereerde weken), oriëntatie (alleen bij 2 weken), totalen-toggle. Niet-gegenereerde weken disabled met uitleg.
- **Acceptatie:** component test voor disabled-state en uitlegtekst.

---

## Fase 2 — Due engine, achterstand, verslepen, promote

### T2.1 Due engine
- `packages/shared/src/due.ts`: pure `computeDue(tasks, intervals, today)` → per actieve taak `{ daysSince, ratio, state: 'ok'|'due'|'overdue' }`; `ratio >= 1.0` due, `>= 1.5` overdue; gesorteerd op ratio desc. `daysSince` op basis van `lastCompletedAt ?? createdAt` in lokale dagen. Vakantie telt mee (geen correctie). Skipped telt als niet gedaan (verandert `lastCompletedAt` niet).
- `GET /api/due` → gerankte lijst met taak, ruimte, interval, daysSince, ratio, state, eerstvolgende geplande open occurrence (indien aanwezig).
- Nachtelijke job logt samenvatting. PDF `/due` activeren.
- **Acceptatie:** unit tests grenswaarden (ratio exact 1.0 en 1.5, DST); integratie: taak 3 cycli overgeslagen verschijnt als overdue terwijl grid "netjes" is.

### T2.2 Web: achterstand & overdue
- Mobiel tabblad "Achterstand": gerankte lijst, prominente markering overdue (icoon + label "Flink achter"). Actie "Inplannen" → datum/assignee kiezen → `POST /api/occurrences`. Actie "Nu gedaan" → maakt ad-hoc occurrence en vinkt direct af.
- Uitleg-tooltip bij een taak die zowel op een dag staat als in achterstand ("Staat nog open op za 12 sep — het plan zegt wanneer, dit zegt hoelang geleden").
- **Acceptatie:** component test; integratie `POST /api/occurrences` met audit `create`, `source: 'ui'`.

### T2.3 Verslepen / herplannen
- Server: `reschedule` (behoudt `plannedDate`, audit `reschedule` met from/to dayKey; waarschuwing indien assignee niet beschikbaar; `cycleId` herberekend als datum in andere cyclus valt) en `assign` (audit `assign`).
- Web mobiel: weekweergave met sleepbare items (dnd-kit TouchSensor, delay 200 ms) + alternatief via menu "Verplaats naar…" (toegankelijkheid). Web desktop: weekgrid met slepen.
- Versleepte items tonen "verplaatst van di 8 sep".
- **Acceptatie:** tests voor warning-pad en cyclus-overschrijding; component test menu-alternatief.

### T2.4 Promote-to-template
- `domain/promote.ts` volgens §1.4. `GET /api/promote-suggestions` → `{ planId, taskId, fromSlot, toWeekday, toAssigneeId?, evidence: occurrenceIds[] }`.
- `POST /api/promote-suggestions/apply` → wijzigt slot in actief plan via dezelfde `validatePlan` (errors → 422); audit op cyclePlan `update` met `meta.promotedFrom`.
- Afwijzen: `POST /api/promote-suggestions/dismiss` → opgeslagen in `settings.dismissedPromotions` zodat dezelfde suggestie niet terugkomt tot er nieuw bewijs is.
- Web: banner in Vandaag/Week en in de editor: "Je verplaatst ‘Badkamer’ steeds van dinsdag naar woensdag. Plan aanpassen?"
- **Acceptatie:** tests: 1× verplaatst → geen suggestie; 2 cycli gelijk → suggestie; verschillende doeldagen → geen suggestie; apply faalt op niet-beschikbare dag.

### T2.5 Interval-wijziging semantiek
- Test die borgt dat een `PATCH` op interval/duur geen bestaande occurrences wijzigt en de volgende generatie wel de nieuwe duur snapshot.
- **Acceptatie:** test groen.

---

## Fase 3 — AI-assistent

### T3.1 Provider-laag
- Interface `AiProvider { completeJson({ system, user, schema }): Promise<string> }`.
- Implementaties: `none` (gooit `AiDisabledError` → API 503 `ai_disabled`), `mock` (deterministisch; leest fixture-antwoorden, kan "eerste keer ongeldig" simuleren), `anthropic` (model uit settings, key uit `AI_API_KEY`), `openai-compatible` (`endpoint` + `model`, `response_format: json_object`), `ollama` (`/api/chat`, `format: 'json'`).
- Factory leest `settings.aiProvider`; secrets alleen uit env; key nooit in logs of responses.
- **Acceptatie:** unit tests met gemockte `fetch`/SDK voor request-vorm per provider; test dat de app volledig werkt met `type: 'none'` (alle niet-AI integratietests draaien met `none`).

### T3.2 Voorstellen & herbalanceren
- `domain/ai/prompt.ts`: bouwt input (actieve taken met naam/ruimte/interval/perCycle/duur, users met beschikbaarheid/budget, huidige slots bij rebalance, vrije-tekst-constraints). Output-contract: `{ slots: Slot[], rationale: [string,string,string,string] }` (Zod-schema, ook als JSON-schema in de prompt).
- Flow: parse → Zod → `validatePlan`; bij parse-fout of `errors` → **één** her-prompt met de foutlijst; faalt het opnieuw → 422 `{ code: 'ai_invalid_plan', errors }`. Warnings zijn toegestaan en worden meegegeven.
- Succes → nieuw inactief `cyclePlan` met `draft: true`, `source: 'ai'`, `proposalId` (UUID), `rationale`. Audit `create` met `source: 'ai'`, `meta.proposalId`.
- `POST /api/ai/propose-plan` `{ taskIds?, constraints? }` en `POST /api/ai/rebalance` `{ planId, constraints? }` → `{ planId, proposalId, warnings, rationale }`.
- **Acceptatie:** tests met mock-provider: geldig pad; eerste ongeldig/tweede geldig; twee keer ongeldig → 422 en géén plan opgeslagen; nooit auto-activatie.

### T3.3 Taaksuggesties & uitleg
- `POST /api/ai/suggest-tasks` `{ roomId }` → lijst `{ name, intervalKey, durationMinutes, notes }` (niet opgeslagen; alleen bekende interval-keys, dubbele namen gefilterd).
- `POST /api/ai/explain` `{ planId }` → 4 strings.
- **Acceptatie:** mock-tests incl. filtering van onbekende interval-keys.

### T3.4 Diff & toepassen
- `GET /api/cycle-plans/:id/diff?against=active` → `{ added[], removed[], moved[] }` per slot + samenvattingen (minuten per user per week, voor/na).
- `POST /api/cycle-plans/:id/apply-proposal` → alleen voor `draft: true`; activeert via dezelfde activatie-flow, audit `ai-apply` met `source: 'ai'`, `meta.proposalId`. `POST /api/cycle-plans/:id/discard` → `active: false, discarded: true` (audit `update`).
- Web (desktop) scherm "AI-assistent": constraints-invoer, knoppen Voorstel/Herbalanceer/Taken voorstellen/Uitleg; diff-weergave in het 4×7-grid (toegevoegd/verwijderd/verplaatst met iconen + tekst), rationale per week, warnings, knoppen "Toepassen"/"Weggooien". Knoppen verborgen met uitleg als AI uit staat.
- Instellingen-scherm: provider-type, endpoint, model (geen key-veld; uitleg dat de key via env gaat).
- **Acceptatie:** integratietest diff; audit-entry van apply is onderscheidbaar (`source: 'ai'` + proposalId); component test AI-uit-status.

---

## Fase 4 — Statistiek, eerlijkheid, notificaties, data

### T4.1 Statistiek-API
- `GET /api/stats/workload?cycles=N` → per user per week en per cyclus: gepland minuten (snapshot) vs. werkelijk voltooid minuten (`completedBy`).
- `GET /api/stats/completion?cycles=N&groupBy=task|room|user` → done / (done+skipped+open-in-verleden).
- `GET /api/stats/intervals?cycles=N` → per taak gemiddelde werkelijke dagen tussen voltooiingen vs. `periodDays`, met afwijkingsfactor.
- Allemaal via aggregation pipelines op `occurrences` (snapshots, zodat taakwijzigingen historie niet veranderen).
- **Acceptatie:** tests met vaste fixture-dataset en exact verwachte cijfers; test dat halveren van task-duur de historische workload niet verandert.

### T4.2 Web: statistiek & eerlijkheid
- Desktop: eerlijkheidsweergave (gepland vs. gedaan per user, per week/cyclus), completion-tabellen, interval-realiteitsrapport ("wensdenken"-lijst gesorteerd op afwijking), workload-trend. Grafieken zonder informatie in alleen kleur (labels/patronen). Eenvoudige SVG-componenten of een lichte chartlibrary.
- **Acceptatie:** component tests met fixture-data.

### T4.3 Notificaties
- `domain/notify/`: interface + `ntfy` (POST naar `NOTIFY_URL`, optioneel bearer token) en `homeassistant` (webhook POST JSON). `none` default.
- Dagelijkse ochtendmelding 07:30: per user aantal open taken vandaag + aantal overdue.
- Stretch: maandelijkse PDF (4 weken) als ntfy-attachment op de 1e van de maand.
- **Acceptatie:** tests met gemockte fetch voor beide providers; job faalt nooit hard (fout → log.error).

### T4.4 Data: export/import, backup, retentie
- `GET /api/export/json` → alle collecties (incl. auditLog) als één JSON-bestand met `schemaVersion`.
- `POST /api/import/json?mode=replace&confirm=true` → valideert met Zod, vervangt alle collecties behalve dat auditLog-entries uit de import behouden blijven; schrijft daarna één audit-entry `entity: 'import'`. Zonder `confirm=true` → 400.
- `domain/backup.ts`: 03:30 `mongodump --uri=$MONGO_URL --archive=$BACKUP_DIR/huishoudplanner-YYYYMMDD.archive.gz --gzip`, verwijdert archieven ouder dan `BACKUP_RETENTION_DAYS`.
- Audit-retentie: alleen als `AUDIT_RETENTION_DAYS` gezet; verwijdert oudere entries (enige uitzondering op append-only, uitsluitend via deze job in `data/auditRetention.ts`).
- Web: instellingen-scherm met export-knop en import (met bevestigingsdialoog), vakantieperiodes beheren, anchor-datum, gebruikers (naam, kleur, niet-beschikbare dagen, budgetten), ruimtes.
- **Acceptatie:** round-trip test export → lege DB → import → identieke data; backup-functie getest met gemockte `child_process` (commando en retentie); in de Docker-smoke (T5.2) daadwerkelijke `mongodump` via `POST /api/jobs/backup`.

### T4.5 (Stretch) Offline afvinken
- Service worker + IndexedDB-wachtrij voor `complete/uncomplete/skip`; sync bij `online`-event; conflicten (409/404) tonen als melding.
- Alleen uitvoeren als T0–T4.4 volledig groen zijn; anders overslaan en in PROGRESS.md markeren als "niet gedaan (stretch)".
- **Acceptatie:** Playwright-test met `context.setOffline(true)` → afvinken → online → server-status done.

---

## Fase 5 — Eindverificatie

### T5.1 E2E-suite (Playwright)
Draait tegen `npm run build` + server met test-Mongo en vaste klok (`APP_FAKE_NOW` env, alleen toegestaan als `NODE_ENV=test`). Scenario's:
1. Eerste bezoek → profiel kiezen → header toont profiel → wisselen.
2. Taak aanmaken (duur verplicht) → in editor slepen naar cel → drop op niet-beschikbare dag geweigerd met melding → plan activeren.
3. Vandaag (mobiele viewport 375×812): afvinken → undo → overslaan met reden → afvinken "door" andere persoon → geschiedenis toont beide actoren.
4. Occurrence verslepen naar andere dag (touch-emulatie) → "verplaatst van …" zichtbaar.
5. Achterstand toont taak met ratio ≥ 1.5 als "Flink achter" → inplannen.
6. PDF-export 2 weken → download bestandsnaam klopt, 2 pagina's.
7. AI (mock-provider): voorstel → diff zichtbaar → toepassen → geschiedenis toont "via AI-voorstel".
- **Acceptatie:** `npm run test:e2e` groen.

### T5.2 Oplevering
- `README.md` (NL): wat het is, `docker compose up -d`, env-variabelen, backup/restore (`mongorestore`-commando), AI-provider configureren, reverse-proxy-opmerking (geen auth!), ontwikkelen & testen.
- Docker-smoke script `scripts/smoke.mjs`: `docker compose up -d --build`, wacht op healthy, doorloop via HTTP: profiel ophalen, taak aanmaken, slots zetten, activeren, occurrences van vandaag ophalen, afvinken, PDF downloaden (content-type `application/pdf`, > 1 KB), backup-job triggeren en archief controleren in `./backups`, `docker compose down`.
- Werk `PROGRESS.md` bij met eindstatus en een samenvatting van `DECISIONS.md` en `BLOCKERS.md`.
- **Acceptatie:** `npm run verify`, `npm run test:e2e` en `node scripts/smoke.mjs` allemaal groen; laatste commit bevat bijgewerkte PROGRESS.md.

---

## Bijlage A — Traceability requirements → taken

| Requirement | Taken |
|---|---|
| §1 hybride / overdue blijft staan / template-model | T1.7, T1.8, T2.1–T2.3, uitleg in T1.11/T2.2 |
| §2 domeinmodel + indexen | T0.3, T0.4, T1.2 |
| §3.1 taakbeheer | T1.4, T1.10 |
| §3.2 template-editor | T1.5, T1.6, T1.11 |
| §3.3 generatie | T1.7 |
| §3.4 dagelijks gebruik | T1.8, T1.12, T2.3, T2.4 |
| §3.5 due engine | T2.1, T2.2 |
| §3.6 users | T1.2, T1.3, T4.2 |
| §3.7 statistiek | T4.1, T4.2 |
| §3.8 audit | T1.1, T1b.1, T1b.2 |
| §4 AI | T3.1–T3.4 |
| §5 PDF | T1c.1, T1c.2, T4.3 (maandelijks) |
| §6 deploy / frontend / identity / data / i18n | T0.5, T0.6, T1.9, T4.4, T4.5, T5.2 |
| §7 API | T1.3–T4.4 (uitgebreid met ad-hoc occurrences, claim, promote, diff, export/import) |
| §9 open vragen | §1.3 van dit plan |

## Bijlage B — Afwijkingen t.o.v. requirements (bewust)
- Zevende collectie `cycles` (traceerbaarheid generatie-runs en cyclus-identiteit).
- Extra velden: `occurrences.taskNameSnapshot`, `planId`, `origin`, `statusBeforeCompletion`; `cyclePlans.weekThemes`, `draft`, `source`, `proposalId`, `rationale`, `discarded`; `auditLog.meta`; `settings.intervals`, `promoteThreshold`, `dismissedPromotions`; `rooms.virtual`.
- Audit-entity-enum uitgebreid met `cycle`, `room`, `import`.
- `fromWeek` in de PDF-API is een ISO-weeklabel (`2026-W38`).
