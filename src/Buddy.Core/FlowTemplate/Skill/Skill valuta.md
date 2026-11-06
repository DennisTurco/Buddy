---
name: valuta
description: Rivede le idee in Da valutare una alla volta, partendo da quelle scadute, e applica la decisione dell'utente - approva (va in To-Do), rimanda (nuova data) o rifiuta (va in To-Do Rifiutati). Usare con /valuta.
model: sonnet
---

# Valutazione delle idee in sospeso

Questa nota **è** il file `SKILL.md`: per installarla, copiarla in `~/.claude/skills/valuta/SKILL.md` (progetto in team, flusso privato) o in `.claude/skills/valuta/SKILL.md` del repo (progetto personale). È generica: ID, tracker, comandi e regole del progetto stanno nella `Scheda progetto`.

1. Leggi `Da valutare`. Metti prima le voci scadute (`rivedere` uguale o precedente a oggi), poi le altre per data.
2. Per ogni voce mostra ID, titolo, dubbio, la `Riflessione di Claude` con il `consiglio` (se ci sono) e da quanto tempo aspetta. Se utile, aggiungi in una riga il tuo parere aggiornato (un pro e un contro).
3. Chiedi: **approva**, **rimanda**, **rifiuta** o **salta**.
   - **approva** → spostala in `To-Do` con lo stesso ID e un "Fatto quando" concreto (chiedilo se manca), `[priorita:: …]` e `[stima:: …]` (se non li indico, stimali tu), lo stesso `[tag:: …]` (aggiungilo se manca) e `[tipo:: …]` se è di un tipo ricorrente;
   - **rimanda** → nuova data in `[rivedere:: …]` (quella indicata, altrimenti tra 30 giorni);
   - **rifiuta** → riga in `To-Do Rifiutati` con il motivo, come nella skill `rifiuta`;
   - **salta** → passa alla successiva senza cambiare niente.
4. Fermati quando l'utente vuole o quando le voci sono finite.
5. Aggiorna `Stato attuale`.
