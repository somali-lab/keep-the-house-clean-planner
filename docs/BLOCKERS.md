# Blokkades

Taken die na 3 serieuze pogingen niet haalbaar bleken. Formaat: taak · foutmelding · wat geprobeerd is.

Geen openstaande blokkades.

## Opgelost

- 2026-09-13 · T5.2 · **Opgelost:** de smoke-test is handmatig gedraaid met "✔ Smoke-test geslaagd". Oorspronkelijke melding: `node scripts/smoke.mjs` kon in deze sessie niet worden uitgevoerd. Elk `docker`-commando wordt geweigerd met "This command requires approval", en in deze niet-interactieve sessie kan niemand die goedkeuring geven. · Geprobeerd: `docker version` via PowerShell en via Bash. Niet vaker herhaald: een geweigerde permissie is geen technische fout, en opnieuw proberen verandert niets. Klaar en groen: het smoke-script, de compose-parameters, de README, `npm run verify` en `npm run test:e2e`. **Nodig:** één keer `node scripts/smoke.mjs` draaien op een machine met Docker. Verwacht resultaat: "✔ Smoke-test geslaagd". Daarna kan T5.2 op `[x]`.

