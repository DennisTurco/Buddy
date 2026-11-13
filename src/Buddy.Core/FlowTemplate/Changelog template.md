---
title: Changelog template
type: changelog
tags:
  - flusso
  - template
versione_template: "1.25"
updated: 2026-11-11
---

Storico delle modifiche al **flusso base**. Serve a tenere allineati i progetti: ognuno sa quale versione ha applicato (proprietà `versione_template` nella sua `Scheda progetto`) e può recuperare solo le modifiche arrivate dopo.

> [!info] Regola
> Qui vanno solo le modifiche **generiche**, valide per qualsiasi progetto. Quelle specifiche di un progetto vanno nella sua `Scheda progetto`.

> [!example] Formato
> ```
> ## 1.1 · AAAA-MM-GG
> - Cosa è cambiato — note toccate: [[Nota]] · origine: progetto X
> ```

## 1.25 · 2026-11-11

- **Test manuali solo su richiesta**: `/lavora` e `/guidami` non creano più il documento `Test manuali/Test manuali <ID>.md` in automatico. Si chiede con `/lavora ID test`, durante il lavoro, con `/test-manuali ID` o con il pulsante 🧪 della board; un documento che esiste già continua a essere aggiornato a ogni modifica — note toccate: [[Skill lavora]], [[Skill guidami]], [[Skill test-manuali]], [[Da controllare]], [[Nuovo test manuale]], [[LEGGIMI - Template flusso]]

## 1.24 · 2026-11-05

- **Tag dei task** `[tag:: …]`: la natura del lavoro (ticket, feature, bug, analisi, miglioria, refactoring, documentazione, supporto), uno per voce, distinto da `[tipo:: …]`. Lo assegna `/smista` (anche in `Da valutare`), `/valuta` lo conserva all'approvazione, `/lavora` lo aggiunge se manca e lo corregge se il lavoro lo smentisce, `/chiudi` lo riporta in `To-Do Fatti`; il modello *Nuovo task* lo chiede con un menu. La bacheca di Buddy lo mostra come etichetta e filtra per tag — note toccate: [[Intelligenza Artificiale/Template flusso/To-Do]], [[Intelligenza Artificiale/Template flusso/Da valutare]], [[Intelligenza Artificiale/Template flusso/To-Do Fatti]], [[Intelligenza Artificiale/Template flusso/Modelli/Nuovo task]], [[Intelligenza Artificiale/Template flusso/Skill/Skill smista]], [[Intelligenza Artificiale/Template flusso/Skill/Skill valuta]], [[Intelligenza Artificiale/Template flusso/Skill/Skill lavora]], [[Intelligenza Artificiale/Template flusso/Skill/Skill chiudi]] · origine: progetto Buddy

## 1.23 · 2026-11-05

- **Nuova skill `/test-manuali ID`**: scrive o aggiorna da sola il documento `Test manuali/Test manuali <ID>.md` (stesse regole del passo *Test manuali* di `/lavora`), per i task fatti prima della regola, fatti a mano o con il documento da rifare; aggiorna la riga `Test manuali:` in `Da controllare` — note toccate: nuova nota [[Intelligenza Artificiale/Template flusso/Skill/Skill test-manuali]], [[Intelligenza Artificiale/Template flusso/LEGGIMI - Template flusso]] · origine: progetto Buddy

## 1.22 · 2026-11-05

- **Persone e accordi nei task**: nuove righe facoltative in `To-Do`, cioè `Assegnato da:`, `Indicazioni da:`, `Chi chiedere:` e `Accordi:` (accordi presi a voce, con data e persona). Le compila `/smista` dall'appunto; il modello *Nuovo task* chiede `Assegnato da:`. Il task resta dell'utente finché non è chiuso: se il fix lo fa un altro, diventa ⏸ `In attesa di: chiusura da …` — note toccate: [[Intelligenza Artificiale/Template flusso/To-Do]], [[Intelligenza Artificiale/Template flusso/Modelli/Nuovo task]], [[Intelligenza Artificiale/Template flusso/Skill/Skill smista]] · origine: progetto User
- **Controlli in `/lavora`**: per un refactoring o una modifica a qualcosa di condiviso cerca tutti gli utilizzi e ne fa casi di test; il lavoro fuori perimetro non lo fa, ma prepara la domanda da mandare; i miglioramenti fuori dal task vanno in `Da valutare` con `#proposta-team`; per un caso che non si sa testare prepara la domanda per `Chi chiedere:`; sopra la soglia di dimensione (Scheda, default 15 file o 400 righe) propone di dividere la PR. In `Da controllare` arrivano le righe `Da avvisare:`, `Fuori perimetro:`, `Dimensione:`, e il commento per la card riporta gli accordi — note toccate: [[Intelligenza Artificiale/Template flusso/Skill/Skill lavora]], [[Intelligenza Artificiale/Template flusso/Da controllare]], [[Intelligenza Artificiale/Template flusso/Da valutare]] · origine: progetto User
- **Regole per i testi** mandati ad altri (commento per la card, PR, domande, messaggi): niente chiusure secche, errore ammesso per primo senza cause di processo nello stesso testo, disaccordo detto una volta. Sono una sezione di `/lavora` e una regola del file di istruzioni — note toccate: [[Intelligenza Artificiale/Template flusso/Skill/Skill lavora]], [[Intelligenza Artificiale/Template flusso/Configurazione/Configurazione Claude]] · origine: progetto User

## 1.21 · 2026-11-02

- **Test manuali per ogni task**: `/lavora` e `/guidami` scrivono `Test manuali/Test manuali <ID>.md` (nuovo modello *Nuovo test manuale*) alla prima modifica, con i casi ricavati dal "Fatto quando" e dal codice cambiato, e lo aggiornano a ogni modifica successiva senza togliere le spunte (righe `Da rifare dopo il …`); le caselle le spunta l'utente. `Da controllare` ha la riga `Test manuali:`, `To-Do Fatti` il link 🧪; `/verifica-web` spunta solo i casi verificati davvero; `/chiudi` chiede conferma se restano casi non spuntati — note toccate: [[Intelligenza Artificiale/Template flusso/Skill/Skill lavora]], [[Intelligenza Artificiale/Template flusso/Skill/Skill guidami]], [[Intelligenza Artificiale/Template flusso/Skill/Skill verifica-web]], [[Intelligenza Artificiale/Template flusso/Skill/Skill chiudi]], [[Intelligenza Artificiale/Template flusso/Modelli/Nuovo test manuale]], [[Intelligenza Artificiale/Template flusso/Da controllare]], [[Intelligenza Artificiale/Template flusso/To-Do Fatti]], [[Intelligenza Artificiale/Template flusso/LEGGIMI - Template flusso]] · origine: progetto User

## 1.20 · 2026-10-20

- `/lavora`: alla consegna, se il task ha una card sul tracker, aggiunge anche il **commento per la card**, pronto da incollare e pensato per il PO (cosa è stato fatto e cosa è stato testato, e dove; 3-5 righe, senza tecnicismi); nuova riga `Commento tracker:` nel formato di `Da controllare` — note toccate: [[Intelligenza Artificiale/Template flusso/Skill/Skill lavora]], [[Intelligenza Artificiale/Template flusso/Da controllare]] · origine: progetto User

## 1.19 · 2026-10-20

- Cruscotto: nuova colonna *Note* in tutte le tabelle dei task, con il link alla nota del vault che contiene il task e alle note collegate dal task o dalle sue sotto-righe che stanno in una sottocartella del progetto (es. `Analisi/`); le note del flusso (Decisioni, Test web…) sono escluse — note toccate: [[Intelligenza Artificiale/Template flusso/Cruscotto]] · origine: progetto User

## 1.18 · 2026-10-19

- Campo `[consiglio:: 🟢|🟡|🔴]` sulle idee di `Da valutare` (consigliata · dipende · sconsigliata o non ora), scritto da `/smista` insieme alla riflessione e mostrato da `/valuta`; nuova colonna *Consiglio* nella tabella *🤔 Da valutare* del Cruscotto — note toccate: [[Intelligenza Artificiale/Template flusso/Da valutare]], [[Intelligenza Artificiale/Template flusso/Cruscotto]], [[Intelligenza Artificiale/Template flusso/Skill/Skill smista]], [[Intelligenza Artificiale/Template flusso/Skill/Skill valuta]] · origine: progetto User

## 1.17 · 2026-10-19

- Ogni idea in `Da valutare` ha una sotto-riga `Riflessione di Claude:` (una o due frasi: valore, costo o rischio principale, eventuale alternativa) che `/smista` scrive sempre senza esplorare il codice; `/valuta` la mostra quando propone la voce — note toccate: [[Intelligenza Artificiale/Template flusso/Da valutare]], [[Intelligenza Artificiale/Template flusso/Skill/Skill smista]], [[Intelligenza Artificiale/Template flusso/Skill/Skill valuta]] · origine: progetto User

## 1.16 · 2026-10-19

- Cruscotto: nuova sezione *🤔 Da valutare* con l'elenco completo delle idee (ID, idea, aggiunta, rivedere), ordinate per data di revisione; prima si vedevano solo il conteggio e le scadute — note toccate: [[Intelligenza Artificiale/Template flusso/Cruscotto]] · origine: progetto User

## 1.15 · 2026-10-16

- Corretta la spiegazione del campo **costo** in `To-Do Fatti`: a metà frase era ripetuto l'inizio della nota (intestazione e primo paragrafo) — note toccate: [[Intelligenza Artificiale/Template flusso/To-Do Fatti]] · origine: progetto DailyPill

## 1.14 · 2026-10-16

- Analisi di implementazione: nuova cartella `Analisi/` nel progetto, modello comune [[Intelligenza Artificiale/Template flusso/Modelli/Nuova analisi]] (in breve, contesto, requisiti, situazione attuale, soluzione, alternative, impatti, rischi, punti aperti, piano di lavoro, verifica, storico), nuova skill `/analisi` (sola lettura sul codice; collega l'analisi al task e all'indice; non crea task da sola), sezione *Analisi* in `00 Indice` — note toccate: [[Intelligenza Artificiale/Template flusso/Skill/Skill analisi]], [[Intelligenza Artificiale/Template flusso/Modelli/Nuova analisi]], [[Intelligenza Artificiale/Template flusso/00 Indice]], [[Intelligenza Artificiale/Template flusso/LEGGIMI - Template flusso]], [[Intelligenza Artificiale/Template flusso/Configurazione/Configurazione Claude]] · origine: progetto User

## 1.13 · 2026-10-16

Obiettivo: meno token e meno esplorazione a parità di risultato.

- `Scheda progetto` più corta: non ripete le istruzioni del repo (`CLAUDE.md`, `AGENTS.md`), le rimanda a loro; i tipi di task diventano un elenco (niente tabella con spazi) e danno il valore del campo `[tipo:: …]` — note toccate: [[Intelligenza Artificiale/Template flusso/Scheda progetto]] · origine: progetto User
- Configurazione dei test web spostata dalla Scheda alla nota `Test web` (sezione *Configurazione*), letta solo da `verifica-web` — note toccate: [[Intelligenza Artificiale/Template flusso/Test web]], [[Intelligenza Artificiale/Template flusso/Skill/Skill verifica-web]], [[Intelligenza Artificiale/Template flusso/Scheda progetto]] · origine: progetto User
- Tolta la sezione *Vista* da `To-Do` (Claude ne pagava il codice a ogni lettura): la tabella per priorità con il totale delle ore è nel Cruscotto — note toccate: [[Intelligenza Artificiale/Template flusso/To-Do]], [[Intelligenza Artificiale/Template flusso/Cruscotto]] · origine: progetto User
- Nuova nota [[Intelligenza Artificiale/Template flusso/Mappa codice]] e campo `Dove:` nei task: `/smista` lo compila se si ricava dall'appunto, `/lavora` parte da lì, delega le ricerche ampie a un sotto-agente di esplorazione e lo aggiorna; `/chiudi` aggiorna la Mappa con i file del task; `/guidami` e `/contesto` la usano; `/manutenzione` la controlla (200 righe) — note toccate: [[Intelligenza Artificiale/Template flusso/To-Do]], [[Intelligenza Artificiale/Template flusso/Skill/Skill smista]], [[Intelligenza Artificiale/Template flusso/Skill/Skill lavora]], [[Intelligenza Artificiale/Template flusso/Skill/Skill chiudi]], [[Intelligenza Artificiale/Template flusso/Skill/Skill guidami]], [[Intelligenza Artificiale/Template flusso/Skill/Skill contesto]], [[Intelligenza Artificiale/Template flusso/Skill/Skill manutenzione]] · origine: progetto User
- Costo misurato: `/lavora` legge l'uso del piano all'inizio e alla consegna e scrive `Costo:` in `Da controllare`; `/chiudi` lo copia in `To-Do Fatti` senza chiederlo; campo `[tipo:: …]` portato fino a `To-Do Fatti`; nuova sezione del Cruscotto *📈 Costo e 🔁 per tipo* — note toccate: [[Intelligenza Artificiale/Template flusso/Da controllare]], [[Intelligenza Artificiale/Template flusso/To-Do Fatti]], [[Intelligenza Artificiale/Template flusso/Cruscotto]], [[Intelligenza Artificiale/Template flusso/Skill/Skill lavora]], [[Intelligenza Artificiale/Template flusso/Skill/Skill chiudi]], [[Intelligenza Artificiale/Template flusso/Skill/Skill valuta]] · origine: progetto User
- Modello per skill (`model:` nel frontmatter, vale per il resto del turno): `rifiuta` su haiku; `smista`, `valuta`, `chiudi`, `manutenzione` su sonnet; `lavora`, `guidami`, `contesto`, `verifica-web` restano sul modello della sessione — note toccate: le skill citate · origine: progetto User
- Regola sulla memoria automatica di Claude (fatti del progetto nel vault, memoria solo per l'ambiente locale) — note toccate: [[Intelligenza Artificiale/Template flusso/Configurazione/Configurazione Claude]] · origine: progetto User

## 1.12 · 2026-10-16

- Tolto il riferimento a `/release` (skill mai esistita) da `To-Do Fatti`: l'archivio lo fa `/manutenzione` — note toccate: [[Intelligenza Artificiale/Template flusso/To-Do Fatti]] · origine: progetto User
- `Stato attuale` senza conteggi (massimo 4 righe): i numeri li calcola il Cruscotto, qui restano solo cosa è in corso e gli avvisi con gli ID — note toccate: [[Intelligenza Artificiale/Template flusso/Stato attuale]], [[Intelligenza Artificiale/Template flusso/00 Indice]], [[Intelligenza Artificiale/Template flusso/Skill/Skill manutenzione]], [[Intelligenza Artificiale/Template flusso/LEGGIMI - Template flusso]] · origine: progetto User
- `prossimo_id` dichiarato **contatore unico** dei `T-…`, valido anche per `Da valutare` — note toccate: [[Intelligenza Artificiale/Template flusso/To-Do]], [[Intelligenza Artificiale/Template flusso/Scheda progetto]] · origine: progetto User
- Cruscotto riscritto in un unico blocco con tabelle: nuove sezioni *📋 Da fare* (per priorità, con stima), *❓ Senza "Fatto quando"*, *✅ Fatti negli ultimi 14 giorni* (PR, costo, 🔁); ID e PR cliccabili tramite le nuove proprietà `tracker_link`, `tracker_id`, `pr_link` della Scheda, usate anche nella *Vista* di `To-Do` — note toccate: [[Intelligenza Artificiale/Template flusso/Cruscotto]], [[Intelligenza Artificiale/Template flusso/To-Do]], [[Intelligenza Artificiale/Template flusso/Scheda progetto]] · origine: progetto User
- Nuova cartella `Modelli/` con i modelli Templater per inserire le voci nel formato giusto (task, task ⏸, idea, decisione, termine, appunto); Templater punta a questa cartella — note toccate: `Modelli/`, [[Intelligenza Artificiale/Template flusso/LEGGIMI - Template flusso]] · origine: progetto User

## 1.11 · 2026-10-15

- `/guidami` più didattico: nuova modalità **didattico** (predefinita) con concetti spiegati prima del codice, domande guida, indizi graduali (domanda → indizio → soluzione), aiuto a leggere gli errori, domande di verifica e un riepilogo finale di cosa si è imparato; all'inizio chiede cosa l'utente conosce già — note toccate: [[Intelligenza Artificiale/Template flusso/Skill/Skill guidami]] · origine: progetto DailyPill

## 1.10 · 2026-10-15

- Vista tabellare dei task: ogni task in `To-Do` ha `[priorita:: P1|P2|P3]` e `[stima:: Nh]` (stimati da Claude se non indicati); nuova sezione *Vista* con una tabella Dataview (ID, titolo, priorità, stima, stato, data) ordinata per priorità e con il totale delle ore; `/smista` e `/valuta` compilano i due campi — note toccate: [[Intelligenza Artificiale/Template flusso/To-Do]], [[Intelligenza Artificiale/Template flusso/Skill/Skill smista]], [[Intelligenza Artificiale/Template flusso/Skill/Skill valuta]], [[Intelligenza Artificiale/Template flusso/LEGGIMI - Template flusso]] · origine: progetto DailyPill

## 1.9 · 2026-10-15

- Corretto l'esempio di formato in [[Intelligenza Artificiale/Template flusso/Da controllare]]: la riga `- Test web: …` usciva dal riquadro perché le mancava `> ` — note toccate: [[Intelligenza Artificiale/Template flusso/Da controllare]] · origine: progetto DailyPill

## 1.8 · 2026-10-15

- Nuova skill [[Intelligenza Artificiale/Template flusso/Skill/Skill contesto]]: `/contesto [ID o argomento]` legge in sola lettura `Scheda progetto` e `Stato attuale` (e, con un argomento, solo le voci collegate di To-Do, Decisioni e Glossario) per lavorare con il contesto del progetto anche fuori dalle skill del flusso — note toccate: [[Intelligenza Artificiale/Template flusso/Skill/Skill contesto]], [[Intelligenza Artificiale/Template flusso/LEGGIMI - Template flusso]], [[Intelligenza Artificiale/Template flusso/Configurazione/Configurazione Claude]] · origine: progetto User

## 1.7 · 2026-10-15

- Un colore per progetto nel grafo di Obsidian: gruppo `path:"Progetti/<Nome>"` prima di quello di `Intelligenza Artificiale`, campo *Colore nel grafo* nella [[Intelligenza Artificiale/Template flusso/Scheda progetto]], nuovo passo in "Come usarlo per un nuovo progetto" — note toccate: [[Intelligenza Artificiale/Template flusso/LEGGIMI - Template flusso]], [[Intelligenza Artificiale/Template flusso/Scheda progetto]] · origine: progetto User

## 1.6 · 2026-10-14

- Nuova skill [[Intelligenza Artificiale/Template flusso/Skill/Skill manutenzione]]: misura le note (senza leggerle) e propone archivio, divisione o pulizia di quelle cresciute troppo; `/chiudi` la suggerisce quando `To-Do` o `To-Do Fatti` superano la soglia; sezione facoltativa **Manutenzione** nella [[Intelligenza Artificiale/Template flusso/Scheda progetto]] per soglie diverse — note toccate: [[Intelligenza Artificiale/Template flusso/Skill/Skill manutenzione]], [[Intelligenza Artificiale/Template flusso/Skill/Skill chiudi]], [[Intelligenza Artificiale/Template flusso/Scheda progetto]], [[Intelligenza Artificiale/Template flusso/Configurazione/Configurazione Claude]]

## 1.5 · 2026-10-14

- Tre modi di affrontare un task: `/lavora ID` (Claude lavora, chiede l'ok sul piano se serve), `/lavora ID autonomo` (delega completa) e la nuova skill [[Intelligenza Artificiale/Template flusso/Skill/Skill guidami]] (lo svolgo io, Claude guida e rivede) — note toccate: [[Intelligenza Artificiale/Template flusso/Skill/Skill lavora]], [[Intelligenza Artificiale/Template flusso/Skill/Skill guidami]], [[Intelligenza Artificiale/Template flusso/Configurazione/Configurazione Claude]]

## 1.4 · 2026-10-14

- Bozze delle skill del flusso: [[Intelligenza Artificiale/Template flusso/Skill/Skill smista]], [[Intelligenza Artificiale/Template flusso/Skill/Skill lavora]], [[Intelligenza Artificiale/Template flusso/Skill/Skill chiudi]], [[Intelligenza Artificiale/Template flusso/Skill/Skill rifiuta]], [[Intelligenza Artificiale/Template flusso/Skill/Skill valuta]]
- Esempi di configurazione in [[Intelligenza Artificiale/Template flusso/Configurazione/Configurazione Claude]] (istruzioni, permessi, esclusioni da git, Playwright) e regole pronte in [[Intelligenza Artificiale/Template flusso/Configurazione/Regole hookify]], provate su 21 casi
- Vault unico: una cartella per progetto in `Progetti/`; in `additionalDirectories` va solo la cartella del progetto
- Playwright MCP: salvataggi in `--output-dir` fuori dal repo; `--allowed-origins` non è una barriera di sicurezza, si aggiunge `--blocked-origins` per la produzione
- Campo *Cartella del progetto nel vault* nella [[Intelligenza Artificiale/Template flusso/Scheda progetto]]

## 1.3 · 2026-10-14

- `verifica-web` indica il **costo stimato** (🟢 basso / 🟡 medio / 🔴 alto) quando propone il test, riusa gli **script Playwright salvati** e propone di salvarne uno dopo ogni esplorazione — note toccate: [[Intelligenza Artificiale/Template flusso/Skill/Skill verifica-web]], nuova nota [[Intelligenza Artificiale/Template flusso/Test web]], [[Intelligenza Artificiale/Template flusso/Scheda progetto]] (campo *Cartella degli script di test*)

## 1.2 · 2026-10-14

- Playwright MCP come strumento consigliato per `verifica-web`, configurato solo in locale (niente nel repo) con accesso salvato e ambienti limitati — note toccate: [[Intelligenza Artificiale/Template flusso/Scheda progetto]] (campo *File di accesso salvato*)

## 1.1 · 2026-10-14

- Nuova skill `verifica-web` e sezione **Test web** nella [[Intelligenza Artificiale/Template flusso/Scheda progetto]]: a fine lavoro Claude chiede se provare la modifica nel browser — note toccate: [[Intelligenza Artificiale/Template flusso/Skill/Skill verifica-web]], [[Intelligenza Artificiale/Template flusso/Scheda progetto]], [[Intelligenza Artificiale/Template flusso/Da controllare]]
- Database KeePass dedicato a Claude (facoltativo), nella sezione **Credenziali** della [[Intelligenza Artificiale/Template flusso/Scheda progetto]]

## 1.0 · 2026-10-14

- Prima versione: note del flusso, Cruscotto, Scheda progetto, skill `rimuovi-ff`.
