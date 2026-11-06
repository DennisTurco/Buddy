---
name: smista
description: Smista le voci di To-do Appunti (e, se la Scheda progetto lo prevede, i task del tracker assegnati all'utente) nelle note del flusso - To-Do, Da valutare o To-Do in attesa (⏸). Usare con /smista o quando l'utente chiede di sistemare gli appunti.
model: sonnet
---

# Smistamento degli appunti

Questa nota **è** il file `SKILL.md`: per installarla, copiarla in `~/.claude/skills/smista/SKILL.md` (progetto in team, flusso privato) o in `.claude/skills/smista/SKILL.md` del repo (progetto personale). È generica: ID, tracker, comandi e regole del progetto stanno nella `Scheda progetto`.

## 1. Prima di iniziare

1. Leggi la `Scheda progetto` (formato degli ID, tracker) e `Stato attuale`.
2. Leggi `To-do Appunti`.
3. Se la Scheda prevede il tracker, recupera i task assegnati all'utente e tieni solo quelli che non sono già nel vault (cerca il loro ID).
4. **Non leggere `To-Do` per intero**: per i controlli cerca per ID o per parole chiave.

## 2. Per ogni appunto

1. **Rifiutati e Decisioni**: cerca parole chiave in `To-Do Rifiutati` e `Decisioni`. Se l'appunto ripropone un'idea rifiutata o contraddice una decisione, **non creare niente** e segnalalo nel riepilogo.
2. **Doppioni**: se esiste già un task uguale, aggiungi le informazioni nuove a quello invece di crearne un altro.
3. **Destinazione**:
   - dubbio ("forse", "da valutare", "chissà se") → `Da valutare`, con il dubbio, `[rivedere:: AAAA-MM-GG]` (la data indicata, altrimenti tra 14 giorni) e sempre una sotto-riga `Riflessione di Claude:` di una o due frasi (valore, costo o rischio principale, eventuale alternativa), scritta da quello che sai del progetto **senza esplorare il codice**, più il campo `[consiglio:: 🟢|🟡|🔴]` (legenda nella nota `Da valutare`);
   - deciso ma non per ora ("più avanti", "dopo X", "quando arriva Y") → `To-Do` con ⏸ e `In attesa di: …`, oppure `[fino:: AAAA-MM-GG]` se è una data;
   - tutto il resto → `To-Do`;
   - se non è chiaro, **chiedi** e lascia l'appunto dov'è finché non rispondo.
4. **ID**: la chiave del tracker, se c'è. Altrimenti `prossimo_id` dalle proprietà di `To-Do`, che poi aumenti di uno.
5. **Formato**: usa quello dell'esempio in cima alla nota di destinazione. Ogni task in `To-Do` ha un "Fatto quando" concreto e verificabile: se non si ricava dall'appunto, chiedi o scrivi "da definire". Ha anche `[priorita:: P1|P2|P3]` e `[stima:: Nh]`: usa quelli dell'appunto, altrimenti stimali tu (legenda nella nota `To-Do`) e dillo nel riepilogo. Ogni voce, in `To-Do` come in `Da valutare`, ha sempre un `[tag:: …]` con la natura del lavoro (legenda nella nota `To-Do`: ticket, feature, bug, analisi, miglioria, refactoring, documentazione, supporto): `ticket` se arriva dal tracker o da una richiesta di altri, `bug` se qualcosa non funziona, `analisi` se va solo capito; se l'appunto non lo dice scegli il più probabile e dillo nel riepilogo. Se l'appunto è di un tipo ricorrente della Scheda aggiungi anche `[tipo:: …]`; se nomina file, pagine o funzioni, mettili nel campo `Dove:` (aiutandoti con la `Mappa codice`), **senza esplorare il codice** per trovarli. Se l'appunto dice chi ha assegnato il task, chi dà indicazioni, a chi chiedere o un accordo preso a voce, scrivilo nelle righe `Assegnato da:`, `Indicazioni da:`, `Chi chiedere:` e `Accordi:` (legenda nella nota `To-Do`). Se un appunto è un miglioramento fuori da un task, va in `Da valutare` con il tag `#proposta-team`.
6. **Termini sconosciuti**: se un termine non è nel `Glossario` e non è chiaro, chiedi.
7. **Credenziali**: se un appunto contiene password, chiavi o token, non copiarli. Scrivi il riferimento indicato nella Scheda e segnalamelo.

## 3. Alla fine

1. Togli da `To-do Appunti` solo gli appunti smistati.
2. Aggiorna `prossimo_id` e `Stato attuale`.
3. Riepilogo breve: per ogni appunto la destinazione e l'ID, poi le segnalazioni (rifiutati, doppioni, domande aperte).
