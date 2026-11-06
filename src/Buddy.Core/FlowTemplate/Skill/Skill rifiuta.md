---
name: rifiuta
description: Rifiuta un task o un'idea - la sposta in To-Do Rifiutati con il motivo e un'eventuale condizione per riconsiderarla, così non verrà più riproposta. Usare con /rifiuta ID motivo.
model: haiku
---

# Rifiuto di un task o di un'idea

Questa nota **è** il file `SKILL.md`: per installarla, copiarla in `~/.claude/skills/rifiuta/SKILL.md` (progetto in team, flusso privato) o in `.claude/skills/rifiuta/SKILL.md` del repo (progetto personale). È generica: ID, tracker, comandi e regole del progetto stanno nella `Scheda progetto`.

1. Trova la voce **per ID** in `Da controllare`, `To-Do` o `Da valutare`.
2. Il **motivo è obbligatorio**: se manca, chiedilo. Chiedi anche se c'è una condizione per riconsiderarla ("Riconsiderare se: …"); è facoltativa.
3. Scrivi **una riga** in `To-Do Rifiutati`, nel formato della nota.
4. Togli la voce dalla nota di origine.
5. Se il task era in `Da controllare`, le modifiche al codice sono ancora nella cartella di lavoro: ricordalo e chiedi se annullarle. **Non annullarle senza conferma.**
6. Aggiorna `Stato attuale`.
