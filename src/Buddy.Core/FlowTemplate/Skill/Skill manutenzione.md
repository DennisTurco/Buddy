---
name: manutenzione
description: Controlla le dimensioni delle note del progetto nel vault e dei file di istruzioni per Claude, e propone come alleggerire quelle cresciute troppo (archivio, divisione per categoria, pulizia) senza perdere informazioni. Usare con /manutenzione, a ogni rilascio, o quando un'altra skill segnala un file troppo grande.
model: sonnet
---

# Manutenzione dei file cresciuti troppo

Questa nota **è** il file `SKILL.md`: per installarla, copiarla in `~/.claude/skills/manutenzione/SKILL.md` (progetto in team, flusso privato) o in `.claude/skills/manutenzione/SKILL.md` del repo (progetto personale). È generica: le soglie si possono cambiare nella sezione **Manutenzione** della `Scheda progetto`.

**Perché serve:** più una nota cresce, più costa leggerla e più è facile rovinarla modificandola. L'obiettivo è che le note lette spesso restino piccole.

## Regole

- **Non si perde niente**: si sposta in archivio o si divide, non si cancella. L'unica eccezione sono gli screenshot dei test web, e solo con conferma.
- **Prima il rapporto, poi le modifiche**: mostra cosa hai trovato e cosa proponi, e modifica solo ciò che l'utente approva.
- **Misura senza leggere**: per le dimensioni usa comandi come `wc -l` e `wc -w`. Apri una nota solo se va davvero sistemata.
- Le note da modificare devono essere chiuse in Obsidian: ricordalo prima di iniziare.

## 1. Misura

Controlla queste note nella cartella del progetto e questi file. Se la `Scheda progetto` indica soglie diverse, usa quelle.

| Nota o file | Soglia predefinita | Cosa proporre se la supera |
|---|---|---|
| `To-Do` | 2.000 parole | Dividere per categoria (`To-Do - Bug`, `To-Do - Migliorie`…) con un riepilogo in `To-Do` che le collega; spostare in `Da valutare` i task fermi da più di 3 mesi |
| `To-Do Fatti` | 150 righe | Spostare in `Archivio/To-Do Fatti AAAA-MM.md` le voci dei mesi chiusi (o in `Novità x.y` se c'è stato un rilascio) |
| `Da controllare` | 30 voci | Solo segnalarlo: la revisione la gestisce l'utente |
| `Da valutare` | 50 voci | Proporre `/valuta`, partendo dalle scadute |
| `To-Do Rifiutati` | 200 righe | Accorciare le voci più lunghe di una riga; spostare in `Archivio/To-Do Rifiutati AAAA.md` quelle senza "Riconsiderare se" più vecchie di un anno |
| `Decisioni` | 100 righe | Spostare in `Archivio/Decisioni AAAA.md` quelle superate da decisioni più recenti |
| `Glossario` | 150 voci | Dividere per argomento, con un indice in `Glossario` |
| `Mappa codice` | 200 righe | Togliere i percorsi che non esistono più, unire le sezioni simili, dividere per area con un indice |
| `Stato attuale` | 4 righe | Riportarlo a 4 righe, senza conteggi |
| `00 Indice` | 100 righe | Raggruppare le voci per sezione e togliere i link a note che non esistono più |
| `Scheda progetto` | 150 righe | Spostare le parti lunghe (procedure, elenchi) in note collegate |
| File di istruzioni (`CLAUDE.local.md` o `CLAUDE.md`) | 60 righe | Spostare le procedure lunghe in skill o nella `Scheda progetto` |
| Screenshot in `Test web/` | più vecchi di 30 giorni, di task già in `To-Do Fatti` | Proporre di cancellarli (solo con conferma) |

## 2. Controlli veloci

Mentre misuri, segnala anche (senza correggere da solo):
- task presenti in due note contemporaneamente (stesso ID);
- voci di `Da valutare` scadute e task ⏸ con `fino` passato;
- testo che somiglia a una credenziale scritta in chiaro (cerca con `grep`, non leggere le note intere);
- link a note che non esistono più in `00 Indice`.

## 3. Rapporto

Mostra una tabella breve: nota, dimensione, soglia, proposta. Poi chiedi quali proposte applicare.

## 4. Applicazione

1. Crea la cartella `Archivio/` nella cartella del progetto, se serve. Le note in archivio non vengono contate dal `Cruscotto`, che guarda solo la cartella del progetto.
2. Sposta le voci mantenendo **ID e formato**: chi cerca un ID deve trovarlo anche in archivio.
3. Aggiorna i link in `00 Indice` e nelle note interessate.
4. Rimisura e mostra il prima e il dopo.
5. Aggiorna `Stato attuale`.
