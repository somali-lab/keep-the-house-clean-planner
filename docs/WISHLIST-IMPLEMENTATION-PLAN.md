# Uitvoerbaar implementatieplan — wensenlijst oktober 2026

## Doel en status

Dit document verdeelt de wensenlijst in kleine, zelfstandig te beoordelen wijzigingen.
**Status: concept; de productkeuzes in de beslislijst hieronder staan nog open.**
Een agent voert pas een pakket uit nadat alle bijbehorende keuzes met de
opdrachtgever zijn bevestigd. Daarna voert de agent **één werkpakket tegelijk**
uit en controleert de genoemde acceptatiecriteria. Dit is een blijvende roadmap;
`docs/BUILD.md` blijft de tijdelijke checklist voor het pakket dat daadwerkelijk loopt.

De migratie naar .NET 10, hexagonale architectuur, OpenTelemetry en OAuth2 valt op
uitdrukkelijk verzoek van de opdrachtgever **buiten dit plan**.

### Startprompt voor een uitvoerende agent

> Voer werkpakket `Pxx` uit uit `docs/WISHLIST-IMPLEMENTATION-PLAN.md`.
> Controleer eerst of de bijbehorende productkeuzes bevestigd zijn. Vraag de
> opdrachtgever om een antwoord als dat niet zo is; vul geen standaard in.
> Controleer daarna de afhankelijkheden en volg `AGENTS.md` en de vaste werkwijze
> in dit plan. Schrijf de concrete stappen tijdelijk in `docs/BUILD.md`, implementeer
> de kleinste volledige wijziging, werk tests en permanente documentatie bij,
> verifieer de acceptatiecriteria en commit het resultaat. Rapporteer de
> uitgevoerde controles en eventuele open punten. Maak alleen een PR als ik
> daar afzonderlijk om vraag.

## Bevestigd en nog te besluiten

Bevestigd door de opdrachtgever:

- Windowsmeldingen zijn alleen nodig terwijl de planner open is.
- Meldmomenten worden per persoon ingesteld. Een melding bevat open taken
  voor vandaag en achterstallige taken van die persoon.
- Vóór planactivatie moet een voorbeeld van de gevolgen zichtbaar zijn.
- AI-plannen worden als gewone conceptplannen in Planbeheer beoordeeld;
  activeren en verwijderen gebeuren daar.
- Gamificationpunten gaan naar degene die het werk werkelijk deed.
- Week- en cyclusbonussen tellen geplande en ad-hoc taken mee; overgeslagen
  taken gelden niet als gedaan. De grens voor “op tijd” is nog open.
- Inwisselingen of uitbetalingen worden geregistreerd; de autorisatie en
  boekingswijze staan nog open.
- Planning-PDF's krijgen twee aparte bronnen: een gekozen plansjabloon en
  werkelijk ingeplande taken.
- Mijn taken gebruikt schuivende perioden vanaf vandaag, met zichtbare datums
  en cyclusweeknummers.
- Alle filterkeuzes in de app blijven na Ctrl+F5 behouden.
- De Home-knop gaat altijd naar het weekoverzicht. Twee kolommen bij
  **Vandaag → iedereen** gelden alleen waar het scherm breed genoeg is.
- Een badge-avatar is een door de beheerder geüploade afbeelding.
- De .NET-migratie hoort niet in dit plan.

De volgende productkeuzes zijn **geen implementatiedetails**. Een uitvoerende
agent mag het genoemde pakket niet starten op basis van een eigen aanname:

- **D01 — Prioriteit (alle pakketten):** welke onderdelen moeten eerst? De
  volgorde verderop is slechts een voorstel totdat de opdrachtgever kiest.
- **D02 — Planwissel (P02):** welke toekomstige uitvoeringen worden na de
  preview daadwerkelijk vervangen of behouden?
- **D03 — Extra/ad-hoc werk (P06, P10):** gaat het alleen om een extra
  uitvoering van een bestaande taak, ook meerdere keren per dag, of ook om
  een losse taak zonder centraal taakrecord?
- **D05 — PDF-selectie (P07):** hoe worden niet-toegewezen taken behandeld bij
  een selectie van personen?
- **D06 — About (P09):** betekent “laatste update” build, installatie of
  release? Welke tijdzone moet daarbij staan?
- **D08 — Op tijd (P10):** welke dag of welk tijdstip is de grens voor
  geplande en ad-hoc taken? Wanneer wordt een periode definitief afgesloten?
- **D09 — Inwisseling (P10, P12):** wie mag punten inwisselen of een
  uitbetaling boeken, met welke goedkeuring en welk auditspoor?
- **D16 — Bonusontvanger (P10):** krijgt iedere persoon een eigen week- en
  cyclusbonus, is het een huishoudbonus, of zijn beide gewenst? Betekent
  “week” een kalenderweek of een andere periode?
- **D17 — Instelbaarheid (P10):** welke puntwaarden, bonusbedragen en
  valutaomrekening kan een beheerder wijzigen?
- **D18 — Historie (P10):** krijgen uitvoeringen van vóór de invoering van
  gamification achteraf punten of begint de telling vanaf de activering?

Na beantwoording worden de gekozen regels in
`docs/huishoudplanner-requirements.md` gezet en de bijbehorende pakketstappen
hier aangescherpt. Een open punt blijft zichtbaar; het wordt niet stilzwijgend
omgezet in een default.

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
   onder hun bestaande generator; bewerk die niet met de hand.
7. Maak een Conventional Commit voor het afgeronde pakket. Maak of wijzig
   alleen een PR wanneer de gebruiker dat vraagt. Volg dan de PR- en
   release-noteregels uit `AGENTS.md`. Push, deploy en publiceer niet zonder
   expliciet verzoek. Maak de drie tijdelijke werkdocumenten leeg na afronding.

## Volgorde en afhankelijkheden

De voorgestelde volgorde hieronder geeft prioriteit aan zichtbare fouten en
dagelijks gebruik; D01 kan deze volgorde wijzigen.
Pakketten met hetzelfde niveau kunnen na controle van hun afhankelijkheden
onafhankelijk worden uitgevoerd; testprocessen delen nooit een database,
poort of fixture. De nummers zijn stabiele verwijzingen, geen verplichting om
alles in één PR samen te voegen.

### P00 — Meet en herstel de CI-doorlooptijd

**Afhankelijkheid:** geen. **Oppervlak:** `.github/workflows/ci.yml`, Vitest-
en Playwright-configuratie en testharnassen.

- Meet per job en stap de duur van ten minste een representatieve run. Probeer
  de [aangewezen run](https://github.com/somali-lab/keep-the-house-clean-planner/actions/runs/35503907105/job/106060355879?pr=20#logs)
  opnieuw te lezen; de logs waren tijdens het opstellen niet bereikbaar.
- Behoud de bestaande parallelle CI-matrix voor `shared`, `web` en `server`.
  Optimaliseer eerst de aantoonbare bottleneck, waaronder de Chromium-installatie
  voor PDF-tests indien die substantieel is.
- Verhoog alleen daarna gericht de paralleliteit binnen een job. Controleer
  dat elke server- en E2E-test eigen database, poort, klok en fixtures houdt.
- **Klaar wanneer:** de volledige gate dezelfde controles uitvoert, herhaalde
  runs zonder state-conflicten slagen, en vóór/na-doorlooptijden zijn vastgelegd
  in de PR-beschrijving of het commitbericht.

### P01 — Plannerwijzigingen zichtbaar in weekoverzicht en Mijn taken

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

**Afhankelijkheid:** P01. **Oppervlak:** gedeelde planningsregels,
`generation.ts`, activatieroute, Planbeheer.

- Bereken vóór activeren voor de huidige en volgende cyclus welke toekomstige,
  onaangeroerde, open en gegenereerde uitvoeringen worden vervangen en welke
  nieuwe uitvoeringen ontstaan. Presenteer aantallen én inspecteerbare taken
  met datum en persoon.
- Toon apart de uitvoeringen die blijven bestaan: afgerond, overgeslagen,
  zelf verplaatst en ad hoc. Voorkom dat een verouderde preview als bevestiging
  van een inmiddels gewijzigd plan dient.
- Implementeer de met D02 bevestigde vervangingsregel. De huidige code bewaart
  afgeronde, overgeslagen, verplaatste en ad-hoc uitvoeringen; dit is een
  beschrijving van huidig gedrag, geen genomen productbesluit.
- Leg uit dat overslaan nu niet doorschuift, niet als uitvoering telt voor de
  due-berekening en in de geschiedenis blijft. Pas tekst en tests aan als
  D02 tot een andere regel leidt.
- **Klaar wanneer:** de preview met de werkelijke activatie overeenkomt voor
  alle vijf genoemde statussen, inclusief een activatie midden in een cyclus.

### P03 — AI-plan als eenvoudig beheersbaar concept

**Afhankelijkheid:** P02. **Oppervlak:** AI-route, planopslag, AI-scherm en
Planbeheer.

- Sla een gevalideerd AI-plan als inactief concept op en toon het in een
  gewone planweergave in Planbeheer met **activeren** en **verwijderen**;
  activeren gebruikt altijd de preview uit P02.
- Toon werkverdeling en waarschuwingen begrijpelijk. Verberg geen harde
  validatiefouten. Het concept verandert het actieve plan nooit vanzelf.
- Geef de AI als zachte voorkeur mee om terugkerende activiteiten op dezelfde
  dagen en in een herkenbaar ritme te houden. Behoud beschikbaarheid,
  intervallen en harde daglimieten als belangrijkere regels.
- **Klaar wanneer:** maken, terugvinden, inspecteren, verwijderen en activeren
  van een concept afzonderlijk getest zijn; falende AI-validatie activeert
  of wijzigt niets.

### P04 — Zoeken, weekinformatie en filterbehoud

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
- Vervang het tandwiel rechtsboven door een Home-actie die altijd naar het
  weekoverzicht gaat. Laat alle links in de linker navigatie staan en controleer
  rolbeperkingen en browser-terugknop.
- **Klaar wanneer:** mobiele en desktop-navigatie, toetsenbordfocus en de
  Vandaag-indeling component- en E2E-dekking hebben.

### P06 — Extra uitvoering en ad-hoc taak

**Afhankelijkheid:** P01; ontwerpbeslissing vóór datamigratie.

- Implementeer de door D03 gekozen vormen van extra of ad-hoc werk. Bepaal
  daarna of een nieuwe taak eerst in de centrale takenlijst moet komen.
- De huidige uniciteitsregel `(cycleId, taskId, plannedDate)` en ad-hoc API
  weigeren een tweede uitvoering van dezelfde taak op dezelfde dag. Leg in
  een ADR vast hoe meerdere werkelijke uitvoeringen naast één geplande
  uitvoering bestaan zonder generatie of historie te beschadigen.
- Werk gedeeld contract, data, audit, due-berekening, statistiek en UI als één
  verticale wijziging bij. Voorkom dubbele registratie door herhaalde klikken.
- **Klaar wanneer:** een extra uitvoering op dezelfde dag mogelijk is, een
  afzonderlijk auditspoor heeft, zichtbaar is in historie/statistiek en een
  planwissel overleeft. Test ook ongedaan maken en herladen.

### P07 — PDF per persoon of selectie

**Afhankelijkheid:** P01. **Oppervlak:** exportschema, PDF-sheets en dialog.

- Voeg aan planning-PDF's **iedereen**, één persoon en meerdere personen toe.
  Behandel niet-toegewezen taken volgens D05 en toon de keuze in de UI.
- Bied **plansjabloon** en **werkelijk ingeplande taken** als aparte
  exportkeuzes. Bij gegenereerde uitvoeringen moet een handmatig verplaatste
  taak op de actuele datum staan; bij een conceptsjabloon moet duidelijk zijn
  dat het om geplande, nog niet gegenereerde taken gaat. Houd onderscheidende,
  voorspelbare bestandsnamen aan.
- **Klaar wanneer:** server- en PDF-tests inhoud en lay-out controleren voor
  één, meerdere en alle personen, inclusief lege dagen en niet-toegewezen werk.

### P08 — Instelbare browsermeldingen op Windows

**Afhankelijkheid:** P01. **Oppervlak:** webinstellingen, meldingslogica en i18n.

- Bouw meldingen voor een geopende planner, ook wanneer die tab niet actief
  is. Er is geen eis voor een gesloten browser of OS-achtergrondservice.
- Stel tijdstippen per persoon in. Laat meldingen aan/uit
  zetten, toestemming aanvragen en een testmelding tonen. Toon duidelijk
  wanneer toestemming ontbreekt.
- Neem open taken voor vandaag en achterstallige taken van de ingestelde
  persoon op; stuur per tijdstip en dag
  maximaal één samenvatting, ook bij meerdere open tabbladen. Herbereken bij
  tabherstel, tijdzone- of profielwissel. Houd bestaande ntfy/Home Assistant-
  meldingen als aparte instelling herkenbaar.
- **Klaar wanneer:** tijdstippen, toestemming, lege dag, meerdere tabbladen en
  herladen met een vaste klok zijn getest.

### P09 — About-pagina en actuele projectinformatie

**Afhankelijkheid:** geen.

- Toon versie van de draaiende build, de met D06 gekozen datum/tijd,
  licentie of link naar `LICENSE`, en link naar `CHANGELOG.md`.
- Maak de benodigde datum/tijd ook voor officiële builds beschikbaar; de
  huidige lokale buildidentiteit alleen dekt een builddatum niet. Label de
  waarde precies volgens D06, zonder build, installatie en release te verwarren.
- **Klaar wanneer:** lokale en officiële buildmetadata, werkende links en
  mobiel/desktopweergave zijn getest. Leg de betekenis vast in README en
  zo nodig ADR-0007.

### P10 — Punten en beloningsregels

**Afhankelijkheid:** P06, omdat extra werk ook meetelt.

- Geef iedere taak een instelbare puntenwaarde; voeg bij instellingen een
  omrekenfactor van punten naar valuta toe. Registreer inwisselingen of
  uitbetalingen volgens de nog te bevestigen boekingsregel D09.
- Registreer punten in een idempotent, controleerbaar grootboek per werkelijke
  uitvoering. Ken punten toe aan degene die de taak werkelijk deed. Maak de
  keuze bij **namens iemand afvinken** ondubbelzinnig en corrigeer punten bij
  ongedaan maken of beheerwijziging.
- Bereken per met D16 bevestigde weekperiode en per cyclus een bonus voor
  **alles gedaan** en een
  aanvullende bonus voor **alles op tijd**. Geplande en ad-hoc taken tellen
  mee; overgeslagen taken zijn niet gedaan. Definieer met D08 de grens voor
  “op tijd” en het afsluiten van een periode.
- **Klaar wanneer:** dubbel afvinken geen dubbele punten oplevert, correcties
  de balans herstellen en week- en cyclusuitkomsten reproduceerbaar zijn met
  vaste klok en geïsoleerde database.

### P11 — Beheerbare badges

**Afhankelijkheid:** P10.

- Laat een beheerder badges maken met naam, geüploade afbeelding en een regel op
  geselecteerde taken, aantal uitvoeringen of uitgevoerde minuten. Lever
  voorbeeldbadges voor alles op tijd, schoonmaakminuten en herhaalde taken;
  namen en drempels blijven aanpasbaar.
- Evalueer regels uit dezelfde gecontroleerde uitvoeringsgegevens als P10.
  Audit regelwijzigingen en voorkom dubbele toekenning bij een herberekening.
- **Klaar wanneer:** badgebeheer, drempelgrenzen, correcties en toegankelijk
  tonen van behaalde badges zijn getest.

### P12 — Beloningsmeter met kip en eieren

**Afhankelijkheid:** P10; P11 alleen als badges op dit tabblad verschijnen.

- Maak een tabblad met voortgang naar het instelbare week- of cyclusdoel:
  verdiende punten, omrekening, eieren in de mand en een lopende kip.
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

Voor de eerste PR die dit plan uitvoert, voeg een blijvende pre-PR
documentatie- en agentcontextcontrole toe aan `AGENTS.md` en waar toepasselijk
de gespiegelde instructies. Controleer ook of de bestaande context-maintainer
workflow die regel kan behouden. Dit is een eigen, klein documentatiecommit;
het hoeft niet te wachten tot alle functies klaar zijn.

## Uitvoering pas na bevestiging

De open beslislijst is deel van de definitie van gereedheid. Neem bij elk
pakket de antwoorden en eventuele nieuwe vragen door met de opdrachtgever.
Leg bevestigde gedragsregels permanent vast in de requirements en werk daarna
de acceptatiecriteria van het pakket bij. De .NET-migratie blijft uitgesloten
totdat er een nieuw, expliciet verzoek voor komt.
