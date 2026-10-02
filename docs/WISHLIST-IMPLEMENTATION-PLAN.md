# Uitvoerbaar implementatieplan — wensenlijst oktober 2026

## Doel en status

Dit document verdeelt de wensenlijst in kleine, zelfstandig te beoordelen wijzigingen.
**Status: P01, P02 en P04 afgerond; na P04 gepauzeerd op verzoek van de opdrachtgever.**
Een agent voert **één werkpakket tegelijk** uit en controleert de genoemde
acceptatiecriteria. Nieuwe, tijdens uitvoering ontdekte productvragen worden
aan de opdrachtgever gesteld en niet door de agent ingevuld. Dit is een blijvende roadmap;
`docs/BUILD.md` blijft de tijdelijke checklist voor het pakket dat daadwerkelijk loopt.

De wensen komen uit een braindump van de opdrachtgever, letterlijk opgenomen in de
[bijlage](#bijlage-oorspronkelijke-braindump). De antwoorden uit de vraag-en-
antwoordronde daarover staan onder [Bevestigde productkeuzes](#bevestigde-productkeuzes).

De migratie naar .NET 10, hexagonale architectuur, OpenTelemetry en OAuth2 valt op
uitdrukkelijk verzoek van de opdrachtgever **buiten dit plan**.

### Startprompt voor een uitvoerende agent

> Voer werkpakket `Pxx` uit uit `docs/WISHLIST-IMPLEMENTATION-PLAN.md`.
> Controleer of het pakket actief is, of de afhankelijkheden klaar zijn en of
> de open vragen in de kolom **Wacht op** beantwoord zijn; stel ze anders eerst.
> Voer een gepauzeerd pakket alleen uit na een nieuw expliciet verzoek.
> Volg daarna `AGENTS.md` en de vaste werkwijze
> in dit plan. Werk als orkestrator volgens *Agentverdeling en modelkeuze*:
> besteed verkennen, bouwen, controleren en reviewen uit aan subagents met het
> daar genoemde model, en houd het zo goedkoop mogelijk. Schrijf de concrete stappen tijdelijk in `docs/BUILD.md`, implementeer
> de kleinste volledige wijziging, werk tests en permanente documentatie bij,
> vraag bij nieuwe productkeuzes om een antwoord, verifieer de acceptatiecriteria
> en commit het resultaat. Rapporteer de
> uitgevoerde controles en eventuele open punten. Maak alleen een PR als ik
> daar afzonderlijk om vraag.

## Bevestigde productkeuzes

Deze keuzes komen uit de antwoorden van de opdrachtgever en zijn geen defaults:

- Eerst fouten en dagelijks gebruik, daarna uitbreidingen en gamification.
- Windowsmeldingen zijn alleen nodig terwijl de planner open is.
- Meldmomenten worden per persoon ingesteld. Een melding bevat open taken
  voor vandaag en achterstallige taken van die persoon.
- Vóór planactivatie moet een voorbeeld van de gevolgen zichtbaar zijn.
- Bij activeren van een ander plan worden alleen onaangeroerde toekomstige
  open taken vervangen. Afgeronde, overgeslagen, zelf verplaatste en ad-hoc
  uitvoeringen blijven bestaan.
- AI-plannen worden als gewone conceptplannen in Planbeheer beoordeeld;
  activeren en verwijderen gebeuren daar.
- Extra uitvoeringen van bestaande taken, ook meermaals op één dag, én losse
  eenmalige taken zonder centraal taakrecord zijn allebei gewenst.
- Gamificationpunten gaan naar degene die het werk werkelijk deed.
- Week- en cyclusbonussen tellen geplande en ad-hoc taken mee; overgeslagen
  taken gelden niet als gedaan. “Op tijd” betekent vóór het einde van de
  betreffende kalenderweek of cyclus.
- Bonuspunten zijn per persoon, per kalenderweek en per cyclus. Taakpunten,
  beide bonussen en valutaomrekening zijn instelbaar.
- Gebruikers boeken eigen inwisselingen of uitbetalingen direct. Ook bestaande
  uitvoeringshistorie krijgt met terugwerkende kracht punten.
- De eerder gekozen PDF-uitbreiding staat op pauze; als die later wordt
  hervat, zijn een gekozen plansjabloon en werkelijk ingeplande taken aparte
  bronnen en zijn niet-toegewezen taken apart aan of uit te zetten.
- Mijn taken gebruikt schuivende perioden vanaf vandaag, met zichtbare datums
  en cyclusweeknummers.
- Alle filterkeuzes in de app blijven na Ctrl+F5 behouden.
- De Home-knop gaat altijd naar het weekoverzicht. Twee kolommen bij
  **Vandaag → iedereen** gelden alleen waar het scherm breed genoeg is.
- Een badge-avatar is een door de beheerder geüploade afbeelding.
- Badges verschijnen ook op het tabblad van de beloningsmeter; de precieze
  indeling wordt bij de start van P12 met de opdrachtgever afgestemd.
- P00 versnelt alleen de bestaande CI-jobs; Playwright-E2E aan CI toevoegen
  valt buiten dit plan.
- De About-pagina toont de datum en tijd van de laatste release.
- De .NET-migratie hoort niet in dit plan.

De uitvoerende agent legt de relevante gekozen regel vast in
`docs/huishoudplanner-requirements.md` tegelijk met de eerste implementatie
ervan. Waar de huidige requirements een andere regel noemen, geldt deze
bevestigde keuze voor het nieuwe werk; markeer het conflict in de wijziging.

## Herkomst: van wens naar werkpakket

Iedere regel uit de braindump, in de volgorde van de bijlage. **Wens** is iets dat
gebouwd moet worden; **vraag** is een vraag van de opdrachtgever die met een
bevestigde keuze of bestaand gedrag is beantwoord.

| Braindump (samengevat) | Soort | Waar |
| --- | --- | --- |
| Windowsmelding zoals bij een mindful check-in, met instelbare tijdstippen | Wens | P08 |
| Taken toegevoegd in de planner verschijnen niet in weekoverzicht of mobiele takenlijst | Wens (defect) | P01 — afgerond |
| AI-plan beoordelen is niet gebruiksvriendelijk; liever gewoon een plan aanmaken en dat in Planbeheer beoordelen of verwijderen | Wens | P03 |
| Pipelinetests sneller, bijvoorbeeld parallel, zonder onderlinge conflicten | Wens | P00 |
| PDF van plannen voor één persoon of een selectie | Wens | P07 — on hold |
| Mijn taken: datum per week en cyclusweeknummer zichtbaar, niet alles achter elkaar | Wens | P04 — afgerond |
| About-pagina met versie, datum/tijd laatste update, licentie(link) en changelog-link | Wens | P09 |
| Wat gebeurt er met ingeplande, afgeronde, overgeslagen en zelf verplaatste taken als een ander plan actief wordt? | Vraag | Bevestigde keuze; zichtbaar gemaakt in P02 — afgerond |
| Ctrl+F5 moet filterkeuzes behouden | Wens | P04 — afgerond |
| Tandwiel rechtsboven vervangen door Home; linkernavigatie blijft werken | Wens | P05 |
| README bijwerken met betere screenshots | Wens | Doorlopend, zie *Documentatie- en PR-afsluiting* |
| Vóór een PR documentatie en agentcontext controleren | Wens | Afgerond in #50 (`AGENTS.md`) |
| Vandaag → iedereen in twee kolommen naast elkaar | Wens | P05 |
| Wat houdt overslaan van een taak in? | Vraag | Requirements; uitleg in de UI via P02 — afgerond |
| Planner: totaal minuten per week, ook voor de andere cyclusweken | Wens | P04 — afgerond |
| Specifieke CI-run sneller | Wens | P00 |
| Filteren op (een deel van) de taaknaam in planner en weekoverzicht | Wens | P04 — afgerond |
| Weekoverzicht: schakelaar voor cyclusweeknummer op de kaart | Wens | P04 — afgerond |
| Extra uitvoering van een taak registreren, ook als die niet op korte termijn gepland stond | Wens | P06 |
| Ad-hoc taak uitvoeren, of toch eerst in de centrale takenlijst aanmaken? | Vraag | Bevestigde keuze: allebei; P06 |
| Gamification: punten per taak, omrekening naar valuta, bonus voor alles gedaan en extra bonus voor alles op tijd, per week en per cyclus | Wens | P10a–P10c |
| Badges met avatar en regel op taken, minuten of aantal keer gedaan | Wens | P11 |
| Tabblad met beloningsmeter: kip, eieren in een mand, animatie als de mand vol is | Wens | P12 |
| Backend naar .NET 10, hexagonaal, OpenTelemetry, voorbereid op OAuth2/Keycloak | Wens | Buiten dit plan |
| AI-hint: activiteiten zoveel mogelijk op dezelfde dagen en in een ritme | Wens | P03 |

## Open vragen

Er staan op dit moment geen open vragen. Een nieuwe vraag krijgt een volgnummer
(`Qxx`, verder vanaf Q03) en wordt ingevuld in de kolom **Wacht op** van het pakket
dat erop wacht. Een werkpakket begint pas als die vragen beantwoord zijn. Leg het
antwoord vast onder *Bevestigde productkeuzes* en verwijder de vraag hier.

| ID | Vraag | Blokkeert | Voorstel |
| --- | --- | --- | --- |
| — | — | — | — |

## Agentverdeling en modelkeuze

Om credits en tokens te sparen werkt de uitvoerende agent als **orkestrator**: hij
houdt het overzicht en neemt de beslissingen, en besteedt afgebakend werk uit aan
subagents met het lichtste model dat de taak aankan. Geldt voor iedere agenttool;
de kolom *Claude Code* noemt de modelaliassen voor het `model`-veld van een
subagent, andere tools kiezen hun vergelijkbare lichte, standaard of zware model.

| Niveau | Claude Code | Gebruik voor |
| --- | --- | --- |
| Licht | `haiku` | Code en tests opzoeken, CI-tijden meten, `npm run verify` draaien en alleen de fouten samenvatten, i18n-sleutels aanvullen, README/screenshots en agentcontext controleren. |
| Standaard | `sonnet` | De orkestrator zelf, implementatie per verticale slice, tests schrijven, diffreview bij gewone pakketten. |
| Zwaar | `opus` | Alleen ontwerpwerk met datamodel-, migratie- of idempotentierisico: de ADR's van P06 en P10a, de periodegrenzen van P10b, en de review van die pakketten vóór de commit. |

**Rollen per pakket**

1. **Orkestrator (standaard).** Leest het plan, stelt open vragen, schrijft
   `docs/BUILD.md`, deelt het werk op, beoordeelt de resultaten, werkt
   documentatie bij en commit. Leest geen hele bestanden opnieuw die een
   subagent al heeft samengevat.
2. **Verkenner (licht, alleen lezen).** Zoekt het pad shared → server → data →
   web → tests en de relevante ADR's. Levert bestandsnamen met regelnummers en
   een korte conclusie, geen gekopieerde bestanden.
3. **Ontwerper (zwaar, alleen waar de tabel hieronder dat noemt).** Schrijft het
   ADR-concept en de randgevallen voor tests. De orkestrator legt keuzes die
   een productvraag zijn alsnog aan de opdrachtgever voor.
4. **Bouwer (standaard).** Implementeert één verticale slice met tests volgens
   de vaste werkwijze. Krijgt een op zichzelf staande opdracht met paden, slice
   en acceptatiecriteria.
5. **Controleur (licht).** Draait de controles uit
   `.agents/skills/verify-household-planner/SKILL.md` en meldt alleen wat faalt,
   met de relevante uitvoer.
6. **Reviewer (standaard; zwaar bij P06, P10a en P10b).** Beoordeelt de diff
   tegen de acceptatiecriteria en `AGENTS.md` vóór de commit.

**Spaarregels**

- Besteed niets uit wat één zoekopdracht of één bekend bestand is; doe dat zelf.
- Alleen lezende subagents mogen parallel lopen. Schrijvende subagents werken
  na elkaar, één slice tegelijk, zodat ze nooit dezelfde bestanden of dezelfde
  testdatabase, poort of fixture delen.
- Begin licht en schaal pas op na een mislukte poging: faalt een lichte of
  standaard subagent twee keer op dezelfde taak, geef die dan aan het volgende
  niveau of los haar zelf op.
- Hergebruik een lopende subagent voor vervolgvragen in plaats van een nieuwe
  te starten die alles opnieuw moet inlezen.
- Laat subagents conclusies teruggeven, geen volledige logs of bestanden.
- Bij kleine pakketten (S) doet de orkestrator het verkennen en bouwen zelf;
  alleen de controleur en de reviewer zijn dan subagents.

| Pakket | Verkenner | Ontwerper (zwaar) | Bouwer | Reviewer |
| --- | --- | --- | --- | --- |
| P00 | Licht: CI-tijden per job en stap meten | — | Standaard | Standaard |
| P03 | Licht | — | Standaard | Standaard |
| P05 | Zelf (klein pakket) | — | Zelf | Standaard |
| P06 | Licht | Ja: ADR datamodel en index | Standaard, per slice | Zwaar |
| P08 | Licht | — (ADR tabcoördinatie door orkestrator) | Standaard | Standaard |
| P09 | Zelf (klein pakket) | — | Zelf | Standaard |
| P10a | Licht | Ja: ADR grootboek en "uitgevoerd door" | Standaard, per slice | Zwaar |
| P10b | Licht | Ja: periodegrenzen en DST-randgevallen | Standaard | Zwaar |
| P10c | Zelf | — | Standaard | Standaard |
| P11 | Licht | — | Standaard | Standaard |
| P12 | Licht | — | Standaard | Standaard |

## Onbeheerde uitvoering (loop)

Deze sectie geldt alleen wanneer de opdrachtgever een onbeheerde run start,
bijvoorbeeld een nachtelijke `/loop` in Claude Code. Waar ze afwijkt van de
startprompt of de vaste werkwijze, gaat deze sectie voor.

**Opdracht (2 oktober 2026):** voer alle resterende pakketten uit in de
uitvoervolgorde, behalve P07. Push iedere pakketbranch en maak er een
draft-PR voor; merge nooit. De orkestrator draait op Sonnet; subagents volgen
*Agentverdeling en modelkeuze*.

**Bron van dit plan:** lees het van `origin/main`. Staat deze sectie daar nog
niet, lees het dan met
`git show docs/claude-wishlist-plan-assumptions:docs/WISHLIST-IMPLEMENTATION-PLAN.md`.

**Branches.** Een pakket waarvan de afhankelijkheden al op `main` staan,
begint vanaf een bijgewerkte `main`. P10a tot en met P12 bouwen op werk dat
nog niet gemergd is en worden daarom gestapeld; de basis is dan ook de base
van de draft-PR, zodat iedere PR alleen het eigen pakket toont.

| Pakket | Branch | Basis en PR-base |
| --- | --- | --- |
| P00 | `ci/claude-p00-ci-duration` | `main` |
| P05 | `feat/claude-p05-today-columns-home` | `main` |
| P09 | `feat/claude-p09-about-page` | `main` |
| P03 | `feat/claude-p03-ai-draft-plans` | `main` |
| P06 | `feat/claude-p06-extra-and-adhoc-runs` | `main` |
| P08 | `feat/claude-p08-browser-notifications` | `main` |
| P10a | `feat/claude-p10a-points-ledger` | `feat/claude-p06-extra-and-adhoc-runs` |
| P10b | `feat/claude-p10b-period-bonuses` | `feat/claude-p10a-points-ledger` |
| P10c | `feat/claude-p10c-currency-and-redemption` | `feat/claude-p10b-period-bonuses` |
| P11 | `feat/claude-p11-badges` | `feat/claude-p10c-currency-and-redemption` |
| P12 | `feat/claude-p12-reward-meter` | `feat/claude-p11-badges` |

**Iedere ronde**

1. Bepaal de voortgang uit GitHub met
   `gh pr list --state all --json headRefName,title,url,body`. Het eerste
   pakket in de uitvoervolgorde zonder PR is aan de beurt. Een PR-beschrijving
   bevat de markering `loop-status: done` of `loop-status: blocked`. Sla een
   pakket over als een afhankelijkheid `blocked` is; noteer dat voor het
   eindverslag.
2. Controleer het gebruik met de gebruikstool van de host (in de desktopapp
   `get_usage`, anders `npx -y ccusage@latest blocks --active --json`). Staat
   het 5-uurs- of weeklimiet op 95% of hoger, begin dan geen pakket: plan een
   wake-up over `min(3600, seconden tot reset)` en controleer bij het ontwaken
   opnieuw.
3. Werk de werktree schoon bij, maak de branch vanaf de basis uit de tabel en
   voer het pakket uit volgens de startprompt, de vaste werkwijze en de
   agentverdeling. Draai hooguit drie subagents tegelijk, en alleen lezende.
4. Push de branch en maak een draft-PR met een Conventional Commit-titel
   volgens de PR-regels in `AGENTS.md`. Zet in de beschrijving de markering,
   de uitgevoerde controles en een kopje **Beslissingen ter review** met de
   regels die anders in `docs/DECISIONS.md` zouden staan. Lees de beschrijving
   terug.
5. Plan de volgende ronde over 60 seconden. Is er geen pakket meer, schrijf
   dan het eindverslag (per pakket: PR-link, status, controles, open vragen en
   overgeslagen pakketten), stuur een melding als de host dat kan en stop de loop.

**Afwijkingen omdat niemand meekijkt**

- Een productvraag die dit plan niet beantwoordt, vul je niet zelf in. Commit
  het samenhangende deel, zet de vraag in `docs/BLOCKERS.md` op de branch,
  push, maak de draft-PR met `loop-status: blocked` en de vraag bovenaan, en
  ga verder met het volgende pakket dat er niet van afhangt.
- Technische ontwerpkeuzes en ADR's mag je maken; zet ze onder
  **Beslissingen ter review** in de PR.
- P12: kies een eenvoudige, toegankelijke indeling voor badges op de
  metertab en markeer die in de PR als *nog af te stemmen*.
- Blijft een controle falen na twee serieuze herstelpogingen, behandel het
  pakket dan als `blocked` en zet de relevante foutuitvoer in de PR.
- Werk de statustabel in dit plan niet bij in pakketbranches; de draft-PR is
  de status. De tabel wordt bijgewerkt bij het mergen.
- Merge, release, deploy of force-push nooit, en raak geen echte installatie aan.

## Vaste werkwijze voor elk werkpakket

1. Volg `AGENTS.md`: controleer `git status`, werk lokale `main` bij vanaf
   `origin/main`, maak een eigen branch en behoud wijzigingen van anderen.
2. Lees `docs/BUILD.md`, `docs/DECISIONS.md`, `docs/BLOCKERS.md`, de relevante
   requirements en ADR's. Zet de concrete verticale stappen van het pakket in
   `docs/BUILD.md` zolang het werk loopt.
3. Onderzoek het volledige pad van shared contract via server en data naar web.
   Maak eerst een reproducerende test bij een defect. Houd API-schrijfacties in
   `apps/server/src/data/` en audit iedere werkelijke statuswijziging.
4. Werk bij gedrag de relevante sectie van
   `docs/huishoudplanner-requirements.md` bij. Leg een nieuwe architectuurkeuze
   in `docs/adr/` vast; noteer de verwijzing tijdelijk in `docs/DECISIONS.md`.
   Voeg Nederlandse en Engelse UI-teksten toe.
5. Voer gerichte tests en de controles uit
   `.agents/skills/verify-household-planner/SKILL.md` uit. Test veranderde
   kritieke gebruikersreizen ook met Playwright.
6. Controleer vóór een PR de README, screenshots, requirements, ADR's en de
   agentcontext (`AGENTS.md`, `.github/instructions`, de spiegels in
   `.cursor/rules`, skills, plugins en agentic workflows). Werk relevante
   verouderde tekst in dezelfde wijziging bij. Houd gegenereerde workflows
   onder hun bestaande generator: wijzig zo nodig de Markdown-bron, voer
   `gh aw compile` uit en controleer de gegenereerde output. Bewerk die output
   niet met de hand.
7. Maak een Conventional Commit voor het afgeronde pakket. Maak of wijzig
   alleen een PR wanneer de gebruiker dat vraagt. Volg dan de PR- en
   release-noteregels uit `AGENTS.md`. Push, deploy en publiceer niet zonder
   expliciet verzoek. Werk de statustabel in dit plan bij en maak de drie
   tijdelijke werkdocumenten leeg na afronding.

## Volgorde en afhankelijkheden

De bevestigde prioriteit is eerst fouten en dagelijks gebruik, daarna
uitbreidingen en gamification. Uitvoervolgorde:
**P01 → P02 → P04 → P00 → P05 → P09 → P03 → P06 → P08 → P10a → P10b → P10c → P11 → P12**.

- P00 staat vooraan in het resterende werk, omdat snellere CI alle volgende
  pakketten versnelt.
- P09 heeft geen afhankelijkheden en is klein, dus het komt direct na P05.
- P07 is op verzoek gepauzeerd en hoort niet bij deze uitvoervolgorde.

Pakketten zonder onderlinge afhankelijkheid kunnen na controle onafhankelijk
worden uitgevoerd; testprocessen delen nooit een database, poort of fixture. De
nummers zijn stabiele verwijzingen, geen verplichting om alles in één PR samen
te voegen. Omvang: **S** is enkele uren, **M** is een dag of twee, **L** is
meerdere dagen met een datamodelwijziging.

| Werkpakket | Status | Omvang | Wacht op | Resultaat of eerstvolgende stap |
| --- | --- | --- | --- | --- |
| P00 — CI-doorlooptijd | Nog niet gestart | M | — | Eerstvolgende pakket bij hervatting. |
| P01 — Planner naar overzichten | Afgerond | — | — | [PR #51](https://github.com/somali-lab/keep-the-house-clean-planner/pull/51); de actieve planning bleek al te synchroniseren, met regressiedekking en duidelijke uitleg voor conceptplannen. |
| P02 — Activatievoorbeeld | Afgerond | — | — | [PR #52](https://github.com/somali-lab/keep-the-house-clean-planner/pull/52); inspecteerbare preview en hercontrole bij activatie (ADR-0008). |
| P03 — AI-conceptplan | Nog niet gestart | S–M | — | Na P09. |
| P04 — Zoeken, weekinformatie, filters | Afgerond | — | — | [PR #53](https://github.com/somali-lab/keep-the-house-clean-planner/pull/53); zoeken, cyclusweken, minuten, profielgebonden filterbehoud en gedateerde blokken in Mijn taken. |
| P05 — Dagweergave en navigatie | Nog niet gestart | S | — | Na P00. |
| P06 — Extra uitvoering en ad-hoc taak | Nog niet gestart | L | — | Na P03. |
| P07 — PDF-selectie | On hold | M | — | Alleen hervatten op nieuw expliciet verzoek. |
| P08 — Browsermeldingen | Nog niet gestart | M | — | Na P06. |
| P09 — About en projectinformatie | Nog niet gestart | S | — | Na P05; opnieuw controleren of screenshots en README actueel zijn. |
| P10a — Punten per uitvoering | Nog niet gestart | L | — | Na P06 en P08. |
| P10b — Week- en cyclusbonussen | Nog niet gestart | M | — | Na P10a. |
| P10c — Omrekening en inwisselen | Nog niet gestart | M | — | Na P10a. |
| P11 — Badges | Nog niet gestart | M | — | Na P10a. |
| P12 — Beloningsmeter | Nog niet gestart | M | — | Na P10b, P10c en P11; begin met afstemming van de badge-indeling. |

### P00 — Meet en herstel de CI-doorlooptijd

**Afhankelijkheid:** geen. **Oppervlak:** `.github/workflows/ci.yml`, Vitest-
en Playwright-configuratie en testharnassen.

- Huidige situatie: CI heeft de jobs `static-checks` (lint en typecheck),
  `test` als matrix over `shared`, `web` en `server`, en `container`. Alleen de
  `server`-job installeert Chromium (`npx playwright install --with-deps
  chromium`) voor de PDF-tests. Playwright-E2E draait **niet** in CI.
- Meet per job en stap de duur van ten minste een representatieve run. Probeer
  de [aangewezen run](https://github.com/somali-lab/keep-the-house-clean-planner/actions/runs/35503907105/job/106060355879?pr=20#logs)
  opnieuw te lezen; de logs waren tijdens het opstellen niet bereikbaar.
- Behoud de bestaande parallelle matrix. Optimaliseer eerst de aantoonbare
  bottleneck; de Chromium-installatie met systeemafhankelijkheden is de
  waarschijnlijkste kandidaat (bijvoorbeeld door de browser te cachen).
- Verhoog alleen daarna gericht de paralleliteit binnen een job. Controleer
  dat elke servertest eigen database, poort, klok en fixtures houdt.
- Voeg geen Playwright-E2E aan CI toe; dat valt buiten dit pakket.
- **Klaar wanneer:** de volledige gate dezelfde controles uitvoert, herhaalde
  runs zonder state-conflicten slagen, en vóór/na-doorlooptijden zijn vastgelegd
  in de PR-beschrijving of het commitbericht.

### P01 — Plannerwijzigingen zichtbaar in weekoverzicht en Mijn taken

**Status: afgerond in PR #51.**

**Afhankelijkheid:** geen. **Oppervlak:** `cyclePlans`-route en generatie,
planner-querymutaties, weekoverzicht en mobiele takenlijst.

- Reproduceer: voeg een taak en toewijzing toe aan het actieve plan, wacht tot
  opslaan klaar is, open dezelfde kalenderweek in weekoverzicht en Mijn taken.
- Controleer afzonderlijk: actief versus inactief plan, cyclusgeneratie,
  datumvenster en queryverversing na opslaan/activeren.
- Herstel de feitelijke oorzaak; toon in de planner duidelijk wanneer een
  conceptplan niet in dagelijkse overzichten verschijnt.
- **Klaar wanneer:** een nieuwe toekomstige uitvoer in beide overzichten staat
  met juiste datum en persoon, ook na herladen. Voeg een serverintegratietest,
  relevante componenttest en één gerichte E2E-reis toe.

### P02 — Voorbeeld van de gevolgen vóór planactivatie

**Status: afgerond in PR #52.**

**Afhankelijkheid:** P01. **Oppervlak:** gedeelde planningsregels,
`generation.ts`, activatieroute, Planbeheer.

- Bereken vóór activeren voor de huidige en volgende cyclus welke toekomstige,
  onaangeroerde, open en gegenereerde uitvoeringen worden vervangen en welke
  nieuwe uitvoeringen ontstaan. Presenteer aantallen én inspecteerbare taken
  met datum en persoon.
- Toon apart de uitvoeringen die blijven bestaan: afgerond, overgeslagen,
  zelf verplaatst en ad hoc. Voorkom dat een verouderde preview als bevestiging
  van een inmiddels gewijzigd plan dient.
- Vervang alleen onaangeroerde toekomstige open taken. Behoud afgeronde,
  overgeslagen, zelf verplaatste en ad-hoc uitvoeringen, zoals bevestigd.
- Leg uit dat overslaan nu niet doorschuift, niet als uitvoering telt voor de
  due-berekening en in de geschiedenis blijft.
- **Klaar wanneer:** de preview met de werkelijke activatie overeenkomt voor
  alle vijf genoemde statussen, inclusief een activatie midden in een cyclus.

### P03 — AI-plan als eenvoudig beheersbaar concept

**Afhankelijkheid:** P02. **Oppervlak:** AI-scherm, Planbeheer, AI-prompt.

- **Bestaat al:** `POST /ai/propose-plan` slaat een gevalideerd voorstel op als
  inactief plan (`active: false` in `apps/server/src/domain/ai/proposals.ts`).
  Bouw die opslag niet opnieuw. Het resterende werk zit vooral in de UI en in de
  prompt.
- Toon het concept na aanmaken in een gewone planweergave in Planbeheer met
  **activeren** en **verwijderen**; activeren gebruikt altijd de preview uit P02.
  Vervang de huidige verschilweergave in het AI-scherm, die de opdrachtgever niet
  gebruiksvriendelijk vindt, door een verwijzing naar dat concept.
- Toon werkverdeling en waarschuwingen begrijpelijk. Verberg geen harde
  validatiefouten. Het concept verandert het actieve plan nooit vanzelf.
- Geef de AI als zachte voorkeur mee om terugkerende activiteiten op dezelfde
  dagen en in een herkenbaar ritme te houden. Behoud beschikbaarheid,
  intervallen en harde daglimieten als belangrijkere regels.
- **Klaar wanneer:** maken, terugvinden, inspecteren, verwijderen en activeren
  van een concept afzonderlijk getest zijn; falende AI-validatie activeert
  of wijzigt niets.

### P04 — Zoeken, weekinformatie en filterbehoud

**Status: afgerond in PR #53.**

**Afhankelijkheid:** P01. **Oppervlak:** planner, weekoverzicht, Mijn taken,
filtermodellen.

- Zoek in planner en weekoverzicht zonder hoofdlettergevoeligheid op een deel
  van de taaknaam. De zoekterm filtert de zichtbaarheid, niet de opgeslagen
  planning. Test lege invoer, accenten en een naam die meerdere keren voorkomt.
- Toon in de planner totaalminuten per persoon en totaal voor elk van de vier
  cyclusweken. Gebruik bestaande planningsvalidatie als rekenbron.
- Voeg in het weekoverzicht een bewaarde schakelaar toe voor het
  cyclusweeknummer op de taak- of dagkaart.
- Groepeer Mijn taken in schuivende blokken vanaf vandaag met begin- en
  einddatum; toon voor de taken het juiste cyclusweeknummer, ook wanneer een
  blok twee cyclusweken raakt. Houd taken binnen iedere groep op datum
  gesorteerd.
- Bewaar alle filterkeuzes in de app na Ctrl+F5 via URL-parameters of
  browseropslag, passend bij de bestaande pagina. Bied een zichtbare reset.
  Test herladen en profielwissel, zodat een filter van persoon A niet
  ongemerkt aan B hangt.
- **Klaar wanneer:** alle waarden en filters in mobiel en desktop correct
  blijven na navigatie en herladen, met toetsenbord bedienbaar zijn en niet
  alleen via kleur betekenis geven.

### P05 — Dagweergave en navigatie

**Afhankelijkheid:** P04 voor gedeelde filterkeuzes.

- Zet bij **Vandaag → iedereen** de persoonsoverzichten in twee kolommen
  zodra het scherm breed genoeg is voor leesbare kolommen.
- Vervang het tandwiel rechtsboven (nu **Instellingen en beheer openen**) door
  een Home-actie die altijd naar het weekoverzicht gaat. Beheer blijft
  bereikbaar via de linkernavigatie; controleer dat alle links daar blijven
  werken, plus rolbeperkingen en de browser-terugknop. Pas de bestaande test in
  `apps/web/src/App.test.tsx` aan, die nu van het tandwiel uitgaat.
- **Klaar wanneer:** mobiele en desktop-navigatie, toetsenbordfocus en de
  Vandaag-indeling component- en E2E-dekking hebben.

### P06 — Extra uitvoering en ad-hoc taak

**Afhankelijkheid:** P01; ontwerpbeslissing vóór datamigratie.

- Ondersteun een extra uitvoering van een bestaande taak, ook meerdere keren
  op dezelfde dag, én een losse eenmalige taak zonder record in de centrale
  takenlijst. Maak het verschil tussen beide acties zichtbaar in de UI.
- Datamodel nu: elke `occurrence` heeft een verplichte `taskId`, en de unieke
  index `(cycleId, taskId, plannedDate)` in `apps/server/src/data/db.ts`
  weigert een tweede uitvoering van dezelfde taak op dezelfde dag. De
  idempotente bulk-insert van de generatie leunt op die index. Leg in een ADR
  vast hoe meerdere werkelijke uitvoeringen naast één geplande uitvoering
  bestaan en hoe een taak zonder centraal record past (bijvoorbeeld een
  optionele `taskId` of een verborgen eenmalig taakrecord), zonder generatie,
  snapshots, due-berekening of historie te beschadigen. Neem dit besluit
  vóórdat je migratiecode schrijft.
- Werk gedeeld contract, data, audit, due-berekening, statistiek en UI als één
  verticale wijziging bij. Voorkom dubbele registratie door herhaalde klikken.
- **Klaar wanneer:** beide soorten uitvoering een afzonderlijk auditspoor
  hebben, zichtbaar zijn in historie/statistiek en een planwissel overleven.
  Test meermaals uitvoeren op dezelfde dag, ongedaan maken en herladen.

### P07 — PDF per persoon of selectie

**Status: on hold. Niet uitvoeren zonder nieuw expliciet verzoek.**

**Afhankelijkheid:** P01. **Oppervlak:** exportschema, PDF-sheets en dialog.

- Voeg aan planning-PDF's **iedereen**, één persoon en meerdere personen toe.
  Geef bij een persoonsselectie een aparte keuze om niet-toegewezen taken
  mee te nemen; toon de gekozen instelling in de UI.
- Bied **plansjabloon** en **werkelijk ingeplande taken** als aparte
  exportkeuzes. Bij gegenereerde uitvoeringen moet een handmatig verplaatste
  taak op de actuele datum staan; bij een conceptsjabloon moet duidelijk zijn
  dat het om geplande, nog niet gegenereerde taken gaat. Houd onderscheidende,
  voorspelbare bestandsnamen aan.
- **Klaar wanneer:** server- en PDF-tests inhoud en lay-out controleren voor
  één, meerdere en alle personen, inclusief lege dagen en niet-toegewezen werk.

### P08 — Instelbare browsermeldingen op Windows

**Afhankelijkheid:** P01. **Oppervlak:** webinstellingen, meldingslogica en i18n.

- Er bestaat nog geen code die de browser-Notification API gebruikt. Bouw
  meldingen voor een geopende planner, ook wanneer die tab niet actief is. Er
  is geen eis voor een gesloten browser of OS-achtergrondservice.
- Stel tijdstippen per persoon in. Laat meldingen aan/uit zetten, toestemming
  aanvragen en een testmelding tonen. Toon duidelijk wanneer toestemming
  ontbreekt.
- Neem open taken voor vandaag en achterstallige taken van de ingestelde
  persoon op; stuur per tijdstip en dag maximaal één samenvatting, ook bij
  meerdere open tabbladen. Dat vraagt coördinatie tussen tabbladen, bijvoorbeeld
  met de Web Locks API of `BroadcastChannel` plus een geclaimde sleutel per
  persoon, datum en tijdstip in browseropslag; leg het gekozen mechanisme vast in
  een ADR. Herbereken bij tabherstel, tijdzone- of profielwissel. Houd bestaande
  ntfy/Home Assistant-meldingen als aparte instelling herkenbaar.
- **Klaar wanneer:** tijdstippen, toestemming, lege dag, meerdere tabbladen en
  herladen met een vaste klok zijn getest.

### P09 — About-pagina en actuele projectinformatie

**Afhankelijkheid:** geen.

- Toon versie van de draaiende build, datum en tijd van de laatste release,
  licentie of link naar `LICENSE`, en link naar `CHANGELOG.md`.
- Huidige situatie: `apps/web/src/versionModel.ts` kent alleen een
  versienummer, met een tijdstempel voor lokale builds; er is geen
  releasedatum. Lever de datum als buildmetadata vanuit de releaseworkflow,
  zonder afhankelijk te zijn van een live GitHub-verzoek. Volg
  `.github/instructions/release.instructions.md` en ADR-0007, en bewerk
  `version.txt`, `.release-please-manifest.json` en `CHANGELOG.md` niet met de
  hand. Label de datum als **laatste release**; verzin voor een lokale, nog niet
  uitgebrachte build geen nieuwe releasedatum.
- **Klaar wanneer:** lokale en officiële buildmetadata, werkende links en
  mobiel/desktopweergave zijn getest. Leg de betekenis vast in README en
  zo nodig ADR-0007.

### P10 — Punten en beloningsregels

Te groot voor één reviewbare wijziging en daarom gesplitst in drie pakketten.
Alle drie gebruiken hetzelfde grootboek.

#### P10a — Punten per uitvoering en grootboek

**Afhankelijkheid:** P06, omdat extra werk ook meetelt.

- Geef iedere taak een instelbare puntenwaarde.
- Registreer punten in een idempotent, controleerbaar grootboek per werkelijke
  uitvoering. Ken punten toe aan degene die de taak werkelijk deed.
  `completedBy` legt nu het aftikkende profiel vast, wat bij **namens iemand
  afvinken** iemand anders kan zijn. Leg in een ADR vast hoe "uitgevoerd door"
  wordt opgeslagen en welke waarde bestaande historie krijgt. Maak de keuze bij
  namens iemand afvinken ondubbelzinnig in de UI en corrigeer punten bij
  ongedaan maken of beheerwijziging.
- Bereken bij invoering ook punten over bestaande uitvoeringshistorie. Maak de
  berekening idempotent, zodat herstarten of opnieuw berekenen geen dubbele
  punten oplevert.
- **Klaar wanneer:** dubbel afvinken of historische herberekening geen dubbele
  punten oplevert en correcties de balans herstellen, getest met vaste klok en
  geïsoleerde database.

#### P10b — Week- en cyclusbonussen

**Afhankelijkheid:** P10a.

- Bereken per persoon en kalenderweek en per persoon en cyclus een instelbare
  bonus voor **alles gedaan** en een aanvullende bonus voor **alles op tijd**.
  Geplande en ad-hoc taken tellen mee; overgeslagen taken zijn niet gedaan. Voor
  de weekbonus is een taak op tijd als die vóór het einde van die kalenderweek
  klaar is; voor de cyclusbonus vóór het einde van de cyclus.
- Sluit de respectieve bonus pas na afloop van de week of cyclus definitief af
  en ken hem nooit dubbel toe, ook niet bij herberekening.
- **Klaar wanneer:** week- en cyclusuitkomsten reproduceerbaar zijn met vaste
  klok en geïsoleerde database, inclusief de grenzen van week, cyclus en DST.

#### P10c — Omrekening naar valuta en inwisselen

**Afhankelijkheid:** P10a.

- Voeg bij instellingen een instelbare omrekenfactor van punten naar valuta toe
  en toon het saldo in beide eenheden.
- Gebruikers kunnen zelf direct een inwisseling of uitbetaling registreren;
  audit die boeking.
- **Klaar wanneer:** een gebruiker een inwisseling kan boeken en omrekening en
  inwisseling het saldo correct en controleerbaar wijzigen.

### P11 — Beheerbare badges

**Afhankelijkheid:** P10a.

- Laat een beheerder badges maken met naam, geüploade afbeelding en een regel op
  geselecteerde taken, aantal uitvoeringen of uitgevoerde minuten. Lever
  voorbeeldbadges voor alles op tijd, schoonmaakminuten en herhaalde taken;
  namen en drempels blijven aanpasbaar.
- Evalueer regels uit dezelfde gecontroleerde uitvoeringsgegevens als P10a.
  Audit regelwijzigingen en voorkom dubbele toekenning bij een herberekening.
- **Klaar wanneer:** badgebeheer, drempelgrenzen, correcties en toegankelijk
  tonen van behaalde badges zijn getest.

### P12 — Beloningsmeter met kip en eieren

**Afhankelijkheid:** P10b, P10c en P11, omdat badges ook op dit tabblad
verschijnen.

- Stem bij de start met de opdrachtgever af hoe badges op het tabblad staan,
  en leg de uitkomst vast voordat je bouwt.
- Maak een tabblad met voortgang naar het instelbare week- of cyclusdoel:
  verdiende punten, omrekening, eieren in de mand en een lopende kip, en de
  behaalde badges.
- Speel de afrondingsanimatie één keer bij een voltooide meter. Respecteer
  verminderde-beweging-instellingen en geef dezelfde voortgang in tekst.
- **Klaar wanneer:** voortgang, reset per periode, meerdere profielen,
  schermbreedtes en verminderde beweging zijn getest.

## Documentatie- en PR-afsluiting

Na elk pakket geldt de vaste werkwijze bovenaan. Na de zichtbare UI-pakketten
worden de drie bestaande README-screenshots opnieuw gegenereerd met
`npm run screenshots`; voeg alleen beelden toe die een echte, leesbare
gebruikerssituatie tonen. Werk de README-functiebeschrijving en bediening
tegelijk bij. De releasebestanden `version.txt`,
`.release-please-manifest.json` en `CHANGELOG.md` blijven in beheer van
Release Please.

De pre-PR-documentatie- en agentcontextcontrole staat blijvend in `AGENTS.md`.
De bestaande context-maintainer-workflow kan alleen de bestanden binnen zijn
beperkte bewerkbare scope aanpassen; bevindingen daarbuiten meldt hij in zijn
PR-beschrijving. Wijzigingen aan die workflow lopen via de Markdown-bron en
`gh aw compile`.

## Uitvoering en nieuwe vragen

De bovenstaande keuzes zijn bevestigd. Leg ze bij implementatie permanent
vast in de requirements. Als een nieuw productdetail nodig blijkt dat hier
niet is besloten, voeg het toe aan de open vragen, vraag de opdrachtgever
gericht om een antwoord en voer het afhankelijke deel pas daarna uit. De
.NET-migratie blijft uitgesloten totdat er een nieuw, expliciet verzoek voor komt.

## Bijlage: oorspronkelijke braindump

Letterlijk overgenomen, inclusief tikfouten, als herleidbare bron van dit plan.

```text
Clean

- add windows notification like in mindfull checkin  configureable on which time moments
- als ik taken toevoeg en toewijs in de planner zie ik het niet terug in de week overview bijvoorbeeld of in de taken lijst van mobile

- ai plan makenL het beoordelen van het aangemaakt plan met verschil tov van huidige actieve plan moet gebruikes vriendelijke zoals het nu is werkt het niet. Mag ook gewoon plan aanmaken en dan kan de gebruiker het zelf beoordelen en evt verwijderen via plen beheer.
- kunnen de tests in de pipeline sneller door bijv. parallel te laten lopen zonder dat ze onderling gaan conflicteren

-- Bij PDF bij plannen ook voor maar 1 persoon of een selectie van aangemaakt persoen  kunnen aanmaken
-- bij mijn taken wil ik ook duideklijk de datum per week en ook cyclys week nummer zichtbaar hebben ipv alles maar achterelkaar, mag wel met extra kolommen
-- ik wil een about pagina met versie en datum/tijd laatste update met license of link naar linces en link naar changelog
-- wat geberurd als een ander pkan actief wordt wat gebeurt er dan met de al ingeplande taken voor de komende periode en wat gebeurt er met de oude taken zowel gereed, als overgelsagen als zelf verplaatst?
-- CTRL-F5 moet gemaakt filter keuzes behouden
-- op plek van tandwiel rechts boven moet home terug komen om naar de andere weerhave te gaan, weergave links moeten blijven staatn en blijven werken
-- readme moeten worden geupdate en betere screenshots
-- neem in de agents mee dat je voordat je een PR aanmaakt controleert of alle documentatie up to date en of er nog verbeteringennodig zijn aan agentic doucments zoals, agents.md, skills, plugins, agents, instructions, etc
-- Bij vandaag als je kiest voor iedereen dan de andere niet onder elkaar maar een 2 koloms van maken naast elkaar
-- Wat houd overslaan van een taak in ?
-- ik wil b ij planner ook totaal minuten per week zijn en ook van de andere weken in de cyclus
-- kan deze test sneller via parallel of zoals -> https://github.com/somali-lab/keep-the-house-clean-planner/actions/runs/35503907105/job/106060355879?pr=20#logs
-- ik wil kunnen filteren op taak naam in de planner en week overzicht ook opgedeelte taak naam
-- ik wil bij week overzicht een toggle hebben om om week nummer van de cyclus te tonen in de card
-- mogelijkheid om een extra uitvoering van een taak toe te voegen dat je die hebt uitgevoerd ook al stond deze niet op korte temrijn geplandd
- mogelijkheid o een adhoc taak uit te voeren (???) of toch eerst in taken lijst aanmaken een centrale lijst
--------------------
--------------------

- gamification
	- elke taak een waade en inputen
	- in instellingen een omrekening van aantal punten naar currency
	- wanneer alle taken gedaan een bonus in punten
	- wanneer alles op tijd gedaan nog een extra bonus
	- Per week en per cyclus
	- Eventueel ook badges, zoals
		- alles op tijd gedaan
		- x minuten schoonmaak gedaan
		- toilet juffrouw batch als je 10x hebt gedaan ,dat zsoort dingen
		- dweil kampieoen al sje zoveel minuten
		- etc
		- etc
	- badhes moetne aangemaakt kunnen worden met avatar en en rule gebaseerd op aangemaakte taken en minuten of aantal maal gedaan
	- tabblad met beloningsmeter, kip dit eiren in een basket, als alle eieren er in zitten dan een geweldig animatie  een meter met een rennende kip

- refactor backend naar .net 10 oplossing volgens hexagon architectuur, laat ze voorlopig naast elkaar bestaan. in de back end ook observabulity via otel dat kan aansluiten op bijvoorbeeld mijn ELK stack in mijn homelab/netweork. Clen Architextuur is heel belangrijk. inclusief SOLID design primcipples. moet voorbereid zijn op aansluiten van Oauth2 IdP providers, zoals de grote bekend maak ook een KeyCloack of andere zelf hosting oplsosing

- strutuur hints voor AI
  - activiteiten zoveel mogelijk zelfde dagen en ritme
```
