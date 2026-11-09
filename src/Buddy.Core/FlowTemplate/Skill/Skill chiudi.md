---
name: chiudi
description: Chiude un task approvato dall'utente - lo sposta da Da controllare (o direttamente da To-Do) a To-Do Fatti con il link alla PR, aggiorna la documentazione collegata e controlla se conviene proporre una nuova skill. Usare con /chiudi ID con link o numero della PR (il costo lo prende dalla voce).
model: sonnet
---

# Chiusura di un task approvato

Questa nota **è** il file `SKILL.md`: per installarla, copiarla in `~/.claude/skills/chiudi/SKILL.md` (progetto in team, flusso privato) o in `.claude/skills/chiudi/SKILL.md` del repo (progetto personale). È generica: ID, tracker, comandi e regole del progetto stanno nella `Scheda progetto`.

1. Trova la voce **per ID** in `Da controllare`. Se non c'è, cercala in `To-Do` (anche ⏸ o ▶): è un task chiuso senza passare dalla revisione, per esempio fatto a mano o risolto da altri; chiedi conferma in una riga solo se il suo "Fatto quando" non risulta soddisfatto. Se il test web è fallito, chiedi conferma prima di chiudere. Se c'è il documento dei test manuali, conta le caselle ancora vuote (con `grep -c`, senza leggerlo tutto): se ce ne sono, in particolare righe **Da rifare**, elencale e chiedi conferma prima di chiudere.
2. Se non è stato indicato, chiedi il link o il numero della PR; «senza PR» vuol dire che non c'è e nella riga va `—`. Il **costo** prendilo dalla riga `Costo:` della voce (solo la percentuale o i `$`); se manca, chiedilo, altrimenti `—`.
3. Scrivi una riga in `To-Do Fatti`, nel formato della nota: ID, data, titolo, PR, costo, 🔁, link ai test manuali se ci sono, `[tag:: …]` e `[tipo:: …]` se ci sono. Il documento dei test resta in `Test manuali/`: serve per le regressioni future.
4. Togli la voce dalla nota di origine (`Da controllare` o `To-Do`).
5. **Documentazione**: se la modifica cambia un comportamento descritto nelle note collegate al task, aggiornale e mostrami le modifiche. Nessun'altra nota.
6. **Mappa codice**: per ogni file della riga `File:` della voce, aggiungi o aggiorna la riga nella sezione dell'area (o del tipo) della `Mappa codice`: percorso, cosa contiene in una riga, ID. Cerca la sezione per titolo, senza leggere tutta la nota; se un percorso non esiste più, togli la riga.
7. **Changelog**: se il repo ne ha uno, prepara il testo della voce da copiare. Se il flusso è privato non modificare file del repo.
8. **Nuove skill**: cerca in `To-Do Fatti` i task con lo stesso `[tipo:: …]` (o, se manca, con titolo simile). Se sono almeno 3 e non esiste una skill per quel tipo, aggiungi in `Da valutare` la voce "Creare la skill <nome>" con gli ID di esempio, se non c'è già.
9. **Dimensioni**: misura `To-Do`, `To-Do Fatti` e `Mappa codice` con `wc -w` / `wc -l` (senza leggerli). Se superano le soglie della skill `manutenzione` (o della `Scheda progetto`), suggerisci di lanciare `/manutenzione`.
10. Aggiorna `Stato attuale`.
