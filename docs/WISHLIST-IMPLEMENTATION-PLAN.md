# Uitvoerbaar implementatieplan — wensenlijst oktober 2026

## Doel en status

Dit document verdeelt de wensenlijst in kleine, zelfstandig te beoordelen wijzigingen.
**Status: P01, P02 en P04 afgerond; na P04 gepauzeerd op verzoek van de opdrachtgever.**
Een agent voert **één werkpakket tegelijk** uit en controleert de genoemde
acceptatiecriteria. Nieuwe, tijdens uitvoering ontdekte productvragen worden
aan de opdrachtgever gesteld en niet door de agent ingevuld. Dit is een blijvende roadmap;
`docs/BUILD.md` blijft de tijdelijke checklist voor het pakket dat daadwerkelijk loopt.

De migratie naar .NET 10, hexagonale architectuur, OpenTelemetry en OAuth2 valt op
uitdrukkelijk verzoek van de opdrachtgever **buiten dit plan**.

### Startprompt voor een uitvoerende agent

> Voer werkpakket `Pxx` uit uit `docs/WISHLIST-IMPLEMENTATION-PLAN.md`.
> Controleer of het pakket actief is en of de afhankelijkheden klaar zijn.
> Voer een gepauzeerd pakket alleen uit na een nieuw expliciet verzoek.
> Volg daarna `AGENTS.md` en de vaste werkwijze
> in dit plan. Schrijf de concrete stappen tijdelijk in `docs/BUILD.md`, implementeer
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
- De About-pagina toont de datum en tijd van de laatste release.
- De .NET-migratie hoort niet in dit plan.

De uitvoerende agent legt de relevante gekozen regel vast in
`docs/huishoudplanner-requirements.md` tegelijk met de eerste implementatie
ervan. Waar de huidige requirements een andere regel noemen, geldt deze
bevestigde keuze voor het nieuwe werk; markeer het conflict in de wijziging.

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
   expliciet verzoek. Maak de drie tijdelijke werkdocumenten leeg na afronding.

## Volgorde en afhankelijkheden

De bevestigde prioriteit is eerst fouten en dagelijks gebruik, daarna
uitbreidingen en gamification. Uitvoervolgorde:
**P01 → P02 → P04 → P05 → P00 → P03 → P06 → P08 → P09 → P10 → P11 → P12**.
P00 versnelt de resterende tests na de eerste herstel- en gebruikspakketten.
P07 is op verzoek gepauzeerd en hoort niet bij deze uitvoervolgorde.
Pakketten met hetzelfde niveau kunnen na controle van hun afhankelijkheden
onafhankelijk worden uitgevoerd; testprocessen delen nooit een database,
poort of fixture. De nummers zijn stabiele verwijzingen, geen verplichting om
alles in één PR samen te voegen.

| Werkpakket | Status | Resultaat of eerstvolgende stap |
| --- | --- | --- |
| P00 — CI-doorlooptijd | Nog niet gestart | Na P05, zodra de uitvoering wordt hervat. |
| P01 — Planner naar overzichten | Afgerond | [PR #51](https://github.com/somali-lab/keep-the-house-clean-planner/pull/51); de actieve planning bleek al te synchroniseren, met regressiedekking en duidelijke uitleg voor conceptplannen. |
| P02 — Activatievoorbeeld | Afgerond | [PR #52](https://github.com/somali-lab/keep-the-house-clean-planner/pull/52); inspecteerbare preview en hercontrole bij activatie. |
| P03 — AI-conceptplan | Nog niet gestart | Na P00, zodra de uitvoering wordt hervat. |
| P04 — Zoeken, weekinformatie, filters | Afgerond | Deze wijziging: zoeken, cyclusweken, minuten, profielgebonden filterbehoud en gedateerde blokken in Mijn taken. |
| P05 — Dagweergave en navigatie | Nog niet gestart | Eerstvolgende werkpakket bij hervatting. |
| P06 — Extra uitvoering en ad-hoc taak | Nog niet gestart | Na P03 volgens de uitvoervolgorde. |
| P07 — PDF-selectie | On hold | Alleen hervatten op nieuw expliciet verzoek. |
| P08 — Browsermeldingen | Nog niet gestart | Na P06 volgens de uitvoervolgorde. |
| P09 — About en projectinformatie | Nog niet gestart | Na P08; opnieuw controleren of screenshots en README actueel zijn. |
| P10 — Punten en beloningen | Nog niet gestart | Na P09. |
| P11 — Badges | Nog niet gestart | Na P10. |
| P12 — Beloningsmeter | Nog niet gestart | Na P10; controleer de afhankelijkheid van P11. |

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
- Vervang alleen onaangeroerde toekomstige open taken. Behoud afgeronde,
  overgeslagen, zelf verplaatste en ad-hoc uitvoeringen, zoals bevestigd.
- Leg uit dat overslaan nu niet doorschuift, niet als uitvoering telt voor de
  due-berekening en in de geschiedenis blijft.
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

- Ondersteun een extra uitvoering van een bestaande taak, ook meerdere keren
  op dezelfde dag, én een losse eenmalige taak zonder record in de centrale
  takenlijst. Maak het verschil tussen beide acties zichtbaar in de UI.
- De huidige uniciteitsregel `(cycleId, taskId, plannedDate)` en ad-hoc API
  weigeren een tweede uitvoering van dezelfde taak op dezelfde dag. Leg in
  een ADR vast hoe meerdere werkelijke uitvoeringen naast één geplande
  uitvoering bestaan zonder generatie of historie te beschadigen.
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

- Toon versie van de draaiende build, datum en tijd van de laatste release,
  licentie of link naar `LICENSE`, en link naar `CHANGELOG.md`.
- Maak de releasedatum ook voor officiële builds beschikbaar zonder afhankelijk
  te zijn van een live GitHub-verzoek. Label haar als **laatste release**;
  verzin voor een lokale, nog niet uitgebrachte build geen nieuwe releasedatum.
- **Klaar wanneer:** lokale en officiële buildmetadata, werkende links en
  mobiel/desktopweergave zijn getest. Leg de betekenis vast in README en
  zo nodig ADR-0007.

### P10 — Punten en beloningsregels

**Afhankelijkheid:** P06, omdat extra werk ook meetelt.

- Geef iedere taak een instelbare puntenwaarde; voeg bij instellingen een
  instelbare omrekenfactor van punten naar valuta en instelbare week- en
  cyclusbonussen toe. Gebruikers kunnen zelf direct een inwisseling of
  uitbetaling registreren; audit die boeking.
- Registreer punten in een idempotent, controleerbaar grootboek per werkelijke
  uitvoering. Ken punten toe aan degene die de taak werkelijk deed. Maak de
  keuze bij **namens iemand afvinken** ondubbelzinnig en corrigeer punten bij
  ongedaan maken of beheerwijziging.
- Bereken per persoon en kalenderweek en per persoon en cyclus een bonus voor
  **alles gedaan** en een aanvullende bonus voor **alles op tijd**. Geplande
  en ad-hoc taken tellen mee; overgeslagen taken zijn niet gedaan. Voor de
  weekbonus is een taak op tijd als die vóór het einde van die kalenderweek
  klaar is; voor de cyclusbonus vóór het einde van de cyclus. Sluit de
  respectieve bonus pas na afloop van de week of cyclus definitief af.
- Bereken bij invoering ook punten over bestaande uitvoeringshistorie.
  Maak de berekening idempotent, zodat herstarten of opnieuw berekenen geen
  dubbele punten of dubbele bonus oplevert.
- **Klaar wanneer:** dubbel afvinken of historische herberekening geen dubbele
  punten oplevert, correcties de balans herstellen, een gebruiker een
  inwisseling kan boeken en week- en cyclusuitkomsten reproduceerbaar zijn
  met vaste klok en geïsoleerde database.

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

De pre-PR-documentatie- en agentcontextcontrole staat blijvend in `AGENTS.md`.
De bestaande context-maintainer-workflow kan alleen de bestanden binnen zijn
beperkte bewerkbare scope aanpassen; bevindingen daarbuiten meldt hij in zijn
PR-beschrijving. Wijzigingen aan die workflow lopen via de Markdown-bron en
`gh aw compile`.

## Uitvoering en nieuwe vragen

De bovenstaande keuzes zijn bevestigd. Leg ze bij implementatie permanent
vast in de requirements. Als een nieuw productdetail nodig blijkt dat hier
niet is besloten, vraag de opdrachtgever gericht om een antwoord en voer het
afhankelijke deel pas daarna uit. De .NET-migratie blijft uitgesloten totdat
er een nieuw, expliciet verzoek voor komt.
