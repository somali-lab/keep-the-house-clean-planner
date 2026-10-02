# Uitvoerbaar implementatieplan — wensenlijst oktober 2026

## Doel en status

Dit document verdeelt de wensenlijst in kleine, zelfstandig te beoordelen wijzigingen.
**Status: P01, P02 en P04 afgerond; na P04 gepauzeerd.**

**Bron:** dit plan is afgeleid van een ongestructureerde braindump van de
opdrachtgever (letterlijk opgenomen in de [bijlage](#bijlage-oorspronkelijke-braindump)).
Er heeft **geen vraag-en-antwoordronde** plaatsgevonden. Alles wat verder gaat
dan de letterlijke wens is daarom een **aanname** (`Axx`) of een **open vraag**
(`Qxx`), niet een bevestigde keuze. P01, P02 en P04 zijn op basis van aannames
uitgevoerd; die staan in de lijst met de status *achteraf bevestigen*.

Een agent voert **één werkpakket tegelijk** uit en controleert de genoemde
acceptatiecriteria. Dit is een blijvende roadmap; `docs/BUILD.md` blijft de
tijdelijke checklist voor het pakket dat daadwerkelijk loopt.

De migratie naar .NET 10, hexagonale architectuur, OpenTelemetry en OAuth2 staat
in de braindump, maar valt **buiten dit plan** (zie Q10).

### Startprompt voor een uitvoerende agent

> Voer werkpakket `Pxx` uit uit `docs/WISHLIST-IMPLEMENTATION-PLAN.md`.
> Controleer of het pakket actief is, of de afhankelijkheden klaar zijn en of
> alle open vragen (`Qxx`) in de kolom **Wacht op** beantwoord zijn. Is dat niet
> zo, stel die vragen dan eerst en begin niet. Behandel aannames (`Axx`) niet als
> bevestigd: noem bij aanvang welke aannames het pakket gebruikt en vraag of ze
> kloppen. Voer een gepauzeerd pakket alleen uit na een nieuw expliciet verzoek.
> Volg daarna `AGENTS.md` en de vaste werkwijze in dit plan. Schrijf de concrete
> stappen tijdelijk in `docs/BUILD.md`, implementeer de kleinste volledige
> wijziging, werk tests en permanente documentatie bij, verifieer de
> acceptatiecriteria en commit het resultaat. Rapporteer de uitgevoerde controles
> en eventuele open punten. Maak alleen een PR als ik daar afzonderlijk om vraag.

## Herkomst: van wens naar werkpakket

Iedere regel uit de braindump, in de volgorde van de bijlage. **Wens** is iets dat
gebouwd moet worden, **vraag** is een vraag van de opdrachtgever die een antwoord
of uitleg nodig heeft.

| Braindump (samengevat) | Soort | Waar |
| --- | --- | --- |
| Windowsmelding zoals bij een mindful check-in, met instelbare tijdstippen | Wens | P08, Q03 |
| Taken toegevoegd in de planner verschijnen niet in weekoverzicht of mobiele takenlijst | Wens (defect) | P01 — afgerond |
| AI-plan beoordelen is niet gebruiksvriendelijk; liever gewoon een plan aanmaken en dat in Planbeheer beoordelen of verwijderen | Wens | P03 |
| Pipelinetests sneller, bijvoorbeeld parallel, zonder onderlinge conflicten | Wens | P00, Q01 |
| PDF van plannen voor één persoon of een selectie | Wens | P07 — on hold |
| Mijn taken: datum per week en cyclusweeknummer zichtbaar, niet alles achter elkaar; extra kolommen mag | Wens | P04 — afgerond |
| About-pagina met versie, datum/tijd laatste update, licentie(link) en changelog-link | Wens | P09, Q04 |
| Wat gebeurt er met ingeplande, afgeronde, overgeslagen en zelf verplaatste taken als een ander plan actief wordt? | Vraag | Beantwoord door bestaand gedrag (requirements sinds #37); P02 maakt het zichtbaar — afgerond |
| Ctrl+F5 moet filterkeuzes behouden | Wens | P04 — afgerond |
| Tandwiel rechtsboven vervangen door Home naar de andere weergave; linkernavigatie blijft werken | Wens | P05 |
| README bijwerken met betere screenshots | Wens | Doorlopend, zie *Documentatie- en PR-afsluiting* |
| Vóór een PR documentatie en agentcontext controleren | Wens | Afgerond in #50 (`AGENTS.md`) |
| Vandaag → iedereen in twee kolommen naast elkaar | Wens | P05 |
| Wat houdt overslaan van een taak in? | Vraag | Beantwoord in requirements; uitleg in de UI via P02 — afgerond |
| Planner: totaal minuten per week, ook voor de andere cyclusweken | Wens | P04 — afgerond |
| Specifieke CI-run sneller | Wens | P00 |
| Filteren op (een deel van) de taaknaam in planner en weekoverzicht | Wens | P04 — afgerond |
| Weekoverzicht: schakelaar voor cyclusweeknummer op de kaart | Wens | P04 — afgerond |
| Extra uitvoering van een taak registreren, ook als die niet op korte termijn gepland stond | Wens | P06 |
| Ad-hoc taak uitvoeren, of toch eerst in de centrale takenlijst aanmaken? | Vraag | P06, Q02 |
| Gamification: punten per taak, omrekening naar valuta, bonus voor alles gedaan en extra bonus voor alles op tijd, per week en per cyclus | Wens | P10a–P10c, Q05–Q07 |
| Badges met avatar en regel op taken, minuten of aantal keer gedaan | Wens ("eventueel") | P11, Q08 |
| Tabblad met beloningsmeter: kip, eieren in een mand, animatie als de mand vol is, rennende kip | Wens | P12, Q08 |
| Backend naar .NET 10, hexagonaal, OpenTelemetry, voorbereid op OAuth2/Keycloak | Wens | Buiten dit plan, Q10 |
| AI-hint: activiteiten zoveel mogelijk op dezelfde dagen en in een ritme | Wens | P03 |

## Aannames (niet bevestigd)

Een aanname is een invulling die de braindump niet letterlijk geeft. De uitvoerende
agent legt een aanname pas in `docs/huishoudplanner-requirements.md` vast nadat de
opdrachtgever haar heeft bevestigd. Een aanname die al in code zit, staat ook al in
de requirements; als de opdrachtgever haar afwijst, wordt dat een nieuw werkpakket.

| ID | Aanname | Pakket | Status |
| --- | --- | --- | --- |
| A01 | Prioriteit: eerst fouten en dagelijks gebruik, daarna uitbreidingen en gamification. Afgeleid uit de scheidingslijn in de braindump. | Volgorde | Gebruikt; achteraf bevestigen |
| A02 | Vóór planactivatie toont de app een inspecteerbaar voorbeeld van de gevolgen. De braindump stelde alleen de vraag wat er gebeurt. | P02 | In code; achteraf bevestigen |
| A03 | Mijn taken groepeert in schuivende blokken vanaf vandaag, niet in kalenderweken. | P04 | In code; achteraf bevestigen |
| A04 | Filterbehoud geldt voor alle filters in de app en per profiel, zodat een filter van persoon A niet aan B blijft hangen. | P04 | In code; achteraf bevestigen |
| A05 | Planner toont totaalminuten per persoon én totaal, voor alle vier cyclusweken. | P04 | In code; achteraf bevestigen |
| A06 | Home gaat altijd naar het weekoverzicht; dat sluit aan op de huidige knop **Terug naar overzicht** in beheer. | P05 | Nog niet gebruikt |
| A07 | Twee kolommen bij Vandaag → iedereen alleen waar het scherm breed genoeg is; mobiel blijft één kolom. | P05 | Nog niet gebruikt |
| A08 | Een AI-concept wordt geactiveerd via dezelfde preview als P02. | P03 | Nog niet gebruikt |
| A09 | Een extra uitvoering kan ook meerdere keren op dezelfde dag. | P06 | Nog niet gebruikt |
| A10 | "Windowsmelding" betekent een browsermelding (Notification API) zolang de planner in een tabblad open is; geen achtergrondservice en geen melding bij een gesloten browser. | P08 | Nog niet gebruikt |
| A11 | Bestaande ntfy/Home Assistant-meldingen blijven een aparte, herkenbare instelling. | P08 | Nog niet gebruikt |
| A12 | Voor PDF's zijn plansjabloon en werkelijk ingeplande taken aparte bronnen, en niet-toegewezen taken zijn apart aan of uit te zetten. | P07 | On hold |
| A13 | Badges worden beheerd door een beheerder; de avatar is een geüploade afbeelding. | P11 | Nog niet gebruikt |

## Open vragen

Een werkpakket begint pas als de vragen in zijn kolom **Wacht op** beantwoord
zijn. Het voorstel is een startpunt voor het gesprek, geen besluit. Leg het
antwoord vast in de requirements of een ADR en verwijder de vraag hier.

| ID | Vraag | Blokkeert | Voorstel |
| --- | --- | --- | --- |
| Q01 | CI draait nu geen Playwright-E2E (alleen lint/typecheck, Vitest per project en de containerbuild). Moet P00 alleen de bestaande jobs versnellen, of ook E2E aan CI toevoegen (wat CI langer maakt)? | P00 | Eerst de bestaande jobs versnellen; E2E in CI als apart besluit. |
| Q02 | Ad-hoc taak: mag die bestaan zonder record in de centrale takenlijst, of maak je hem eerst daar aan? Elke uitvoering heeft nu een verplichte `taskId`. Is meerdere keren per dag nodig (A09)? | P06 | Een "eenmalige" taak die automatisch een verborgen taakrecord krijgt, zodat statistiek en snapshots blijven werken. |
| Q03 | Welk gedrag van "mindful check-in" is bedoeld? Tijdstippen per persoon of per apparaat? Wat staat in de melding? Volstaat een melding zolang de planner open is (A10)? | P08 | Per persoon; open taken van vandaag plus achterstallige taken; alleen met geopende planner. |
| Q04 | Is "datum/tijd laatste update" de laatste release, of het bouwmoment van de draaiende versie? | P09 | Laatste release voor officiële builds; bij een lokale build geen verzonnen datum. |
| Q05 | Wie krijgt de punten als iemand namens een ander aftikt? `completedBy` legt nu het aftikkende profiel vast, niet degene die het werk deed. Komt er een apart veld "uitgevoerd door"? Wie krijgt punten voor de bestaande historie? | P10a | Nieuw veld "uitgevoerd door", standaard gelijk aan het aftikkende profiel; historie volgt `completedBy`. |
| Q06 | Wat betekent "op tijd": op de geplande dag, vóór het einde van de kalenderweek, of vóór het einde van de cyclus? Tellen overgeslagen en ad-hoc taken mee voor "alles gedaan"? | P10b | Op tijd = uiterlijk op de geplande dag; overgeslagen telt als niet gedaan; ad-hoc telt alleen als extra, niet als vereiste. |
| Q07 | Bonussen per persoon of voor het hele huishouden? Moeten gebruikers inwisselingen of uitbetalingen kunnen boeken (staat niet in de braindump)? Krijgt bestaande historie met terugwerkende kracht punten? | P10a, P10b, P10c | Per persoon; inwisselen als aparte keuze in P10c; terugwerkende kracht ja, idempotent. |
| Q08 | Wie mag badges aanmaken (A13)? Verschijnen badges op het tabblad van de beloningsmeter? Is het weekdoel of het cyclusdoel de maat voor een volle mand? | P11, P12 | Beheerder; badges op een eigen plek, niet op de metertab; de gebruiker kiest week of cyclus. |
| Q09 | Kloppen de aannames die al in code zitten (A01–A05)? | — | Bevestigen of per afwijzing een nieuw pakket maken. |
| Q10 | Bevestig dat P07 (PDF-selectie) on hold blijft en dat de .NET-migratie een apart traject is buiten dit plan. Deze status is niet uit de braindump te herleiden. | P07 | Beide bevestigen; .NET-migratie later als eigen plan met ADR. |

## Vaste werkwijze voor elk werkpakket

1. Volg `AGENTS.md`: controleer `git status`, werk lokale `main` bij vanaf
   `origin/main`, maak een eigen branch en behoud wijzigingen van anderen.
2. Controleer de kolom **Wacht op** en de aannames van het pakket. Stel open
   vragen eerst en begin pas na antwoord.
3. Lees `docs/BUILD.md`, `docs/DECISIONS.md`, `docs/BLOCKERS.md`, de relevante
   requirements en ADR's. Zet de concrete verticale stappen van het pakket in
   `docs/BUILD.md` zolang het werk loopt.
4. Onderzoek het volledige pad van shared contract via server en data naar web.
   Maak eerst een reproducerende test bij een defect. Houd API-schrijfacties in
   `apps/server/src/data/` en audit iedere werkelijke statuswijziging.
5. Werk bij gedrag de relevante sectie van
   `docs/huishoudplanner-requirements.md` bij met de bevestigde regel. Leg een
   nieuwe architectuurkeuze in `docs/adr/` vast; noteer de verwijzing tijdelijk in
   `docs/DECISIONS.md`. Voeg Nederlandse en Engelse UI-teksten toe.
6. Voer gerichte tests en de controles uit
   `.agents/skills/verify-household-planner/SKILL.md` uit. Test veranderde
   kritieke gebruikersreizen ook met Playwright.
7. Controleer vóór een PR de README, screenshots, requirements, ADR's en de
   agentcontext (`AGENTS.md`, `.github/instructions`, de spiegels in
   `.cursor/rules`, skills, plugins en agentic workflows). Werk relevante
   verouderde tekst in dezelfde wijziging bij. Houd gegenereerde workflows
   onder hun bestaande generator: wijzig zo nodig de Markdown-bron, voer
   `gh aw compile` uit en controleer de gegenereerde output. Bewerk die output
   niet met de hand.
8. Maak een Conventional Commit voor het afgeronde pakket. Maak of wijzig
   alleen een PR wanneer de gebruiker dat vraagt. Volg dan de PR- en
   release-noteregels uit `AGENTS.md`. Push, deploy en publiceer niet zonder
   expliciet verzoek. Werk de statustabel en de lijsten met aannames en vragen in
   dit plan bij, en maak de drie tijdelijke werkdocumenten leeg na afronding.

## Volgorde en afhankelijkheden

Volgens A01 komen eerst fouten en dagelijks gebruik, daarna uitbreidingen en
gamification. Uitvoervolgorde:
**P01 → P02 → P04 → P00 → P05 → P09 → P03 → P06 → P08 → P10a → P10b → P10c → P11 → P12**.

- P00 staat vooraan in het resterende werk, omdat snellere CI alle volgende
  pakketten versnelt. Het meetgedeelte kan beginnen vóór Q01 beantwoord is.
- P09 heeft geen afhankelijkheden en is klein, dus het komt direct na P05.
- P07 is gepauzeerd (Q10) en hoort niet bij deze uitvoervolgorde.

Pakketten zonder onderlinge afhankelijkheid kunnen na controle onafhankelijk
worden uitgevoerd; testprocessen delen nooit een database, poort of fixture. De
nummers zijn stabiele verwijzingen, geen verplichting om alles in één PR samen
te voegen. Omvang: **S** is enkele uren, **M** is een dag of twee, **L** is
meerdere dagen met een datamodelwijziging.

| Werkpakket | Status | Omvang | Wacht op | Resultaat of eerstvolgende stap |
| --- | --- | --- | --- | --- |
| P00 — CI-doorlooptijd | Nog niet gestart | M | Q01 (alleen voor E2E) | Eerstvolgende pakket bij hervatting. |
| P01 — Planner naar overzichten | Afgerond | — | — | [PR #51](https://github.com/somali-lab/keep-the-house-clean-planner/pull/51); de actieve planning bleek al te synchroniseren, met regressiedekking en duidelijke uitleg voor conceptplannen. |
| P02 — Activatievoorbeeld | Afgerond | — | Q09 | [PR #52](https://github.com/somali-lab/keep-the-house-clean-planner/pull/52); inspecteerbare preview en hercontrole bij activatie (ADR-0008). |
| P03 — AI-conceptplan | Nog niet gestart | S–M | — | Na P09. |
| P04 — Zoeken, weekinformatie, filters | Afgerond | — | Q09 | [PR #53](https://github.com/somali-lab/keep-the-house-clean-planner/pull/53); zoeken, cyclusweken, minuten, profielgebonden filterbehoud en gedateerde blokken in Mijn taken. |
| P05 — Dagweergave en navigatie | Nog niet gestart | S | — | Na P00. |
| P06 — Extra uitvoering en ad-hoc taak | Nog niet gestart | L | Q02 | Na P03. |
| P07 — PDF-selectie | On hold | M | Q10 | Alleen hervatten op nieuw expliciet verzoek. |
| P08 — Browsermeldingen | Nog niet gestart | M | Q03 | Na P06. |
| P09 — About en projectinformatie | Nog niet gestart | S | Q04 | Na P05; opnieuw controleren of screenshots en README actueel zijn. |
| P10a — Punten per uitvoering | Nog niet gestart | L | Q05, Q07 | Na P06 en P08. |
| P10b — Week- en cyclusbonussen | Nog niet gestart | M | Q06, Q07 | Na P10a. |
| P10c — Omrekening en inwisselen | Nog niet gestart | M | Q07 | Na P10a. |
| P11 — Badges | Nog niet gestart | M | Q08 | Na P10a. |
| P12 — Beloningsmeter | Nog niet gestart | M | Q08 | Na P10b en P10c; P11 alleen als badges op de metertab komen. |

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
- E2E in CI toevoegen is een apart besluit (Q01); als dat gebeurt, geldt
  dezelfde isolatie-eis voor iedere E2E-worker.
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

**Status: afgerond in PR #52. Gebruikt aanname A02.**

**Afhankelijkheid:** P01. **Oppervlak:** gedeelde planningsregels,
`generation.ts`, activatieroute, Planbeheer.

- De braindump vroeg wat er bij een planwissel gebeurt. Het antwoord stond al in
  de requirements: alleen onaangeroerde, gegenereerde, open toekomstige taken
  worden vervangen; afgeronde, overgeslagen, zelf verplaatste en ad-hoc
  uitvoeringen blijven bestaan.
- Bereken vóór activeren voor de huidige en volgende cyclus welke uitvoeringen
  worden vervangen en welke nieuwe ontstaan. Presenteer aantallen én
  inspecteerbare taken met datum en persoon.
- Toon apart de uitvoeringen die blijven bestaan. Voorkom dat een verouderde
  preview als bevestiging van een inmiddels gewijzigd plan dient.
- Leg uit dat overslaan niet doorschuift, niet als uitvoering telt voor de
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
  **activeren** en **verwijderen**; activeren gebruikt de preview uit P02 (A08).
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

**Status: afgerond in PR #53. Gebruikt aannames A03, A04 en A05.**

**Afhankelijkheid:** P01. **Oppervlak:** planner, weekoverzicht, Mijn taken,
filtermodellen.

- Zoek in planner en weekoverzicht zonder hoofdlettergevoeligheid op een deel
  van de taaknaam. De zoekterm filtert de zichtbaarheid, niet de opgeslagen
  planning.
- Toon in de planner totaalminuten per persoon en totaal voor elk van de vier
  cyclusweken.
- Voeg in het weekoverzicht een bewaarde schakelaar toe voor het
  cyclusweeknummer op de taak- of dagkaart.
- Groepeer Mijn taken in schuivende blokken vanaf vandaag met begin- en
  einddatum en het juiste cyclusweeknummer per taak.
- Bewaar alle filterkeuzes na Ctrl+F5, per profiel, met een zichtbare reset.
- **Klaar wanneer:** alle waarden en filters in mobiel en desktop correct
  blijven na navigatie en herladen, met toetsenbord bedienbaar zijn en niet
  alleen via kleur betekenis geven.

### P05 — Dagweergave en navigatie

**Afhankelijkheid:** P04 voor gedeelde filterkeuzes. **Aannames:** A06, A07.

- Zet bij **Vandaag → iedereen** de persoonsoverzichten in twee kolommen
  zodra het scherm breed genoeg is voor leesbare kolommen.
- Vervang het tandwiel rechtsboven (nu **Instellingen en beheer openen**) door
  een Home-actie die naar het weekoverzicht gaat. Beheer blijft bereikbaar via
  de linkernavigatie; controleer dat alle links daar blijven werken, plus
  rolbeperkingen en de browser-terugknop. Pas de bestaande test in
  `apps/web/src/App.test.tsx` aan, die nu van het tandwiel uitgaat.
- **Klaar wanneer:** mobiele en desktop-navigatie, toetsenbordfocus en de
  Vandaag-indeling component- en E2E-dekking hebben.

### P06 — Extra uitvoering en ad-hoc taak

**Afhankelijkheid:** P01. **Wacht op:** Q02. **Aanname:** A09.

- Ondersteun een extra uitvoering van een bestaande taak, ook als die niet op
  korte termijn gepland stond. Ondersteun daarnaast een ad-hoc taak in de vorm
  die Q02 bepaalt. Maak het verschil tussen beide acties zichtbaar in de UI.
- Datamodel nu: elke `occurrence` heeft een verplichte `taskId`, en de unieke
  index `(cycleId, taskId, plannedDate)` in `apps/server/src/data/db.ts`
  weigert een tweede uitvoering van dezelfde taak op dezelfde dag. De
  idempotente bulk-insert van de generatie leunt op die index. Leg in een ADR
  vast hoe meerdere werkelijke uitvoeringen naast één geplande uitvoering
  bestaan, en (afhankelijk van Q02) hoe een taak zonder centraal record past,
  zonder generatie, snapshots of historie te beschadigen. Neem dit besluit
  vóórdat je migratiecode schrijft.
- Werk gedeeld contract, data, audit, due-berekening, statistiek en UI als één
  verticale wijziging bij. Voorkom dubbele registratie door herhaalde klikken.
- **Klaar wanneer:** beide soorten uitvoering een afzonderlijk auditspoor
  hebben, zichtbaar zijn in historie/statistiek en een planwissel overleven.
  Test meermaals uitvoeren op dezelfde dag (als A09 bevestigd is), ongedaan
  maken en herladen.

### P07 — PDF per persoon of selectie

**Status: on hold (Q10). Niet uitvoeren zonder nieuw expliciet verzoek.**

**Afhankelijkheid:** P01. **Oppervlak:** exportschema, PDF-sheets en dialog.
**Aanname:** A12.

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

**Afhankelijkheid:** P01. **Wacht op:** Q03. **Aannames:** A10, A11.
**Oppervlak:** webinstellingen, meldingslogica en i18n.

- Er bestaat nog geen code die de browser-Notification API gebruikt. Bouw
  meldingen voor een geopende planner, ook wanneer die tab niet actief is.
- Laat tijdstippen instellen (per persoon of per apparaat volgens Q03).
  Laat meldingen aan- en uitzetten, toestemming aanvragen en een testmelding
  tonen. Toon duidelijk wanneer toestemming ontbreekt.
- Stuur per tijdstip en dag maximaal één samenvatting, ook als er meerdere
  tabbladen open zijn. Dat vraagt coördinatie tussen tabbladen, bijvoorbeeld met
  de Web Locks API of `BroadcastChannel` plus een geclaimde sleutel per persoon,
  datum en tijdstip in browseropslag. Leg het gekozen mechanisme vast in een ADR.
  Herbereken bij tabherstel, tijdzone- of profielwissel.
- Houd bestaande ntfy/Home Assistant-meldingen als aparte instelling herkenbaar.
- **Klaar wanneer:** tijdstippen, toestemming, lege dag, meerdere tabbladen en
  herladen met een vaste klok zijn getest.

### P09 — About-pagina en actuele projectinformatie

**Afhankelijkheid:** geen. **Wacht op:** Q04.

- Toon de versie van de draaiende build, de datum en tijd van de laatste update
  (volgens Q04), de licentie of een link naar `LICENSE`, en een link naar
  `CHANGELOG.md`.
- Huidige situatie: `apps/web/src/versionModel.ts` kent alleen een
  versienummer, met een tijdstempel voor lokale builds; er is geen
  releasedatum. Lever de datum als buildmetadata vanuit de releaseworkflow,
  zonder live GitHub-verzoek. Volg `.github/instructions/release.instructions.md`
  en ADR-0007, en bewerk `version.txt`, `.release-please-manifest.json` en
  `CHANGELOG.md` niet met de hand. Verzin voor een lokale, nog niet
  uitgebrachte build geen releasedatum.
- **Klaar wanneer:** lokale en officiële buildmetadata, werkende links en
  mobiel/desktopweergave zijn getest. Leg de betekenis vast in README en
  zo nodig ADR-0007.

### P10 — Punten en beloningsregels

De braindump vraagt om punten per taak, een omrekening van punten naar valuta,
een bonus voor alles gedaan en een extra bonus voor alles op tijd, per week en per
cyclus. Dat is te groot voor één reviewbare wijziging en is daarom gesplitst in
drie pakketten. Alle drie gebruiken hetzelfde grootboek.

#### P10a — Punten per uitvoering en grootboek

**Afhankelijkheid:** P06, omdat extra werk ook meetelt. **Wacht op:** Q05, Q07.

- Geef iedere taak een instelbare puntenwaarde.
- Registreer punten in een idempotent, controleerbaar grootboek per werkelijke
  uitvoering. Leg vast wie de taak werkelijk deed volgens Q05; `completedBy` is
  nu het aftikkende profiel. Corrigeer punten bij ongedaan maken of een
  beheerwijziging.
- Bereken, als Q07 dat bevestigt, bij invoering punten over de bestaande
  uitvoeringshistorie. Herstarten of opnieuw berekenen levert geen dubbele
  punten op.
- **Klaar wanneer:** dubbel afvinken of historische herberekening geen dubbele
  punten oplevert en correcties de balans herstellen, getest met vaste klok en
  geïsoleerde database.

#### P10b — Week- en cyclusbonussen

**Afhankelijkheid:** P10a. **Wacht op:** Q06, Q07.

- Bereken een bonus voor **alles gedaan** en een aanvullende bonus voor
  **alles op tijd**, per kalenderweek en per cyclus, met de betekenis van "op
  tijd" en de telling van overgeslagen en ad-hoc taken volgens Q06. Bonusbedragen
  zijn instelbaar.
- Sluit een bonus pas na afloop van de week of cyclus definitief af en ken hem
  nooit dubbel toe.
- **Klaar wanneer:** week- en cyclusuitkomsten reproduceerbaar zijn met vaste
  klok, inclusief de grenzen van week, cyclus en DST.

#### P10c — Omrekening naar valuta en inwisselen

**Afhankelijkheid:** P10a. **Wacht op:** Q07.

- Voeg bij instellingen een instelbare omrekenfactor van punten naar valuta toe
  en toon het saldo in beide eenheden.
- Alleen als Q07 dat bevestigt: laat gebruikers een inwisseling of uitbetaling
  registreren en audit die boeking.
- **Klaar wanneer:** omrekening en (indien bevestigd) inwisselen het saldo
  correct en controleerbaar wijzigen.

### P11 — Beheerbare badges

**Afhankelijkheid:** P10a. **Wacht op:** Q08. **Aanname:** A13.

- Laat badges maken met naam, geüploade afbeelding en een regel op geselecteerde
  taken, aantal uitvoeringen of uitgevoerde minuten. Lever voorbeeldbadges uit de
  braindump (alles op tijd, schoonmaakminuten, herhaalde taken zoals tien keer het
  toilet); namen en drempels blijven aanpasbaar.
- Evalueer regels uit dezelfde gecontroleerde uitvoeringsgegevens als P10a.
  Audit regelwijzigingen en voorkom dubbele toekenning bij een herberekening.
- **Klaar wanneer:** badgebeheer, drempelgrenzen, correcties en toegankelijk
  tonen van behaalde badges zijn getest.

### P12 — Beloningsmeter met kip en eieren

**Afhankelijkheid:** P10b en P10c; P11 alleen als badges op de metertab komen
(Q08). **Wacht op:** Q08.

- Maak een tabblad met voortgang naar het week- of cyclusdoel: verdiende punten,
  omrekening, eieren in de mand en een rennende kip.
- Speel de afrondingsanimatie één keer bij een volle mand. Respecteer
  verminderde-beweging-instellingen en geef dezelfde voortgang in tekst.
- **Klaar wanneer:** voortgang, reset per periode, meerdere profielen,
  schermbreedtes en verminderde beweging zijn getest.

## Documentatie- en PR-afsluiting

Na elk pakket geldt de vaste werkwijze bovenaan. De braindump vraagt om een
bijgewerkte README met betere screenshots. Genereer daarom na de zichtbare
UI-pakketten de README-screenshots opnieuw met `npm run screenshots`; voeg alleen
beelden toe die een echte, leesbare gebruikerssituatie tonen. Werk de
README-functiebeschrijving en bediening tegelijk bij. De releasebestanden
`version.txt`, `.release-please-manifest.json` en `CHANGELOG.md` blijven in beheer
van Release Please.

De pre-PR-documentatie- en agentcontextcontrole staat blijvend in `AGENTS.md`.
De bestaande context-maintainer-workflow kan alleen de bestanden binnen zijn
beperkte bewerkbare scope aanpassen; bevindingen daarbuiten meldt hij in zijn
PR-beschrijving. Wijzigingen aan die workflow lopen via de Markdown-bron en
`gh aw compile`.

## Uitvoering en nieuwe vragen

Behandel alleen de letterlijke braindump en beantwoorde vragen als vaststaand.
Leg een bevestigd antwoord permanent vast in de requirements of een ADR, werk de
lijsten met aannames en open vragen in dit plan bij, en voer het afhankelijke deel
pas daarna uit. Als tijdens de uitvoering een nieuw productdetail nodig blijkt,
voeg het toe als `Qxx`, vraag de opdrachtgever gericht om een antwoord en vul het
niet zelf in. De .NET-migratie blijft uitgesloten totdat er een nieuw, expliciet
verzoek voor komt.

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
