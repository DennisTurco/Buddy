---
name: verifica-web
description: Prova nel browser una modifica appena fatta, seguendo i passi "Come testare" del task in Da controllare, e scrive l'esito con gli screenshot. Riusa gli script Playwright già salvati quando esistono. Usare quando l'utente accetta il test web proposto a fine lavoro, o con /verifica-web ID.
---

# Verifica della modifica nel browser

Questa nota **è** il file `SKILL.md`: per installarla, copiarla in `~/.claude/skills/verifica-web/SKILL.md` (o `.claude/skills/verifica-web/SKILL.md` in un progetto personale). È generica: URL, ambienti permessi, login, strumento, cartelle degli screenshot e degli script stanno nella sezione **Configurazione** della nota `Test web`, che elenca anche gli script già salvati. Non serve leggere la `Scheda progetto` per i test.

## Due modi di testare

| Modo | Cosa succede | Costo in token |
|---|---|---|
| **Script salvato** | Si lancia uno script Playwright già scritto (`npx playwright test …`). Claude legge solo l'esito | 🟢 **Basso**: il browser lo guida lo script, non Claude |
| **Esplorazione** | Claude guida il browser passo per passo con Playwright MCP, leggendo ogni pagina | 🟡 **Medio** per pochi passi su pagine semplici · 🔴 **Alto** per molti passi, form lunghi o molte pagine |

## 1. Proposta a fine lavoro

Quando la modifica si vede nel browser, **prima di fare qualsiasi cosa** chiedi all'utente se vuole il test, indicando il costo stimato:

1. Cerca nella nota `Test web` uno script che copra già la pagina o il flusso modificato.
2. Proponi in una riga, per esempio:
   - `Vuoi il test web? 🟢 Costo basso: esiste già lo script "login-contratti" — lo rilancio.`
   - `Vuoi il test web? 🟡 Costo medio: prima volta su questa pagina, 3 passi semplici.`
   - `Vuoi il test web? 🔴 Costo alto: prima volta, 8 passi con un form lungo. Se vuoi, alla fine salvo lo script così le prossime volte costa poco.`
3. Se esiste uno script ma la modifica cambia la pagina, dillo: lo script va aggiornato prima di rilanciarlo.

La stima è indicativa: dipende da quante pagine vengono lette e da quanto sono grandi.

## 2. Regole

- Usa **solo gli ambienti permessi** nella nota `Test web`. **Mai produzione.** Se l'URL non è tra quelli permessi, fermati e chiedi.
- Sugli ambienti condivisi non salvare, modificare o cancellare dati se il passo non lo richiede esplicitamente. Se un passo modifica dati, **chiedi prima**.
- **Login:** usa l'accesso salvato indicato nella nota `Test web`. Se le credenziali sono nel database KeePass di Claude (percorso nella sezione *Credenziali* della `Scheda progetto`), usale **solo dentro lo script o il comando** che fa il login, senza mai stamparle, scriverle in un file o mostrarle in uno screenshot. Altrimenti chiedi all'utente di fare il login.
- Non correggere il codice durante il test. Se qualcosa non va, riportalo e chiedi come procedere.
- Script e risultati dei test restano **fuori dal repo**, nella cartella indicata nella nota `Test web`.

## 3. Esecuzione

**Con uno script salvato**
1. Lancia lo script dalla cartella degli script, mostrando solo il riepilogo dell'esito.
2. Se fallisce, apri solo la parte del report che riguarda il passo fallito.

**Con l'esplorazione**
1. Leggi la voce del task in `Da controllare`: "Cambiato" e "Come testare". Se c'è il documento dei test manuali, scegli da lì i casi da provare nel browser (sezione per sezione) invece di inventarli.
2. Verifica che l'applicazione sia avviata nell'ambiente giusto. Se non lo è, chiedi se avviarla.
3. Esegui i passi uno alla volta. Fai uno screenshot dopo ogni passo importante, nella cartella indicata nella nota `Test web`.
4. Controlla anche gli errori nella console del browser.
5. Alla fine proponi di **salvare uno script** per questo flusso, così le prossime verifiche costano poco. Se l'utente accetta, scrivilo nella cartella degli script e aggiungi una riga alla nota `Test web`.

## 4. Esito

Scrivi l'esito nella voce del task in `Da controllare`:
```
- Test web: ✅ superato · script "login-contratti" · 2026-10-14
  - Screenshot: ![[ID-passo1.png]]
```
oppure
```
- Test web: ❌ fallito al passo 2 — cosa si vede invece di cosa ci si aspettava
```
Aggiorna anche la nota `Test web`: data dell'ultima esecuzione ed esito dello script usato.

Se il task ha il documento dei test manuali (`Test manuali/Test manuali <ID>.md`): se il test è superato spunta **solo** i casi che lo script o l'esplorazione hanno davvero verificato e scrivi nell'introduzione quali sezioni sono coperte e da quale script; se è fallito non spuntare nulla e aggiungi sotto il caso una riga `❌ test web AAAA-MM-GG: cosa si vede`. I casi che il browser non può verificare (database, mail, servizi) restano all'utente.
