---
name: contesto
description: Carica il contesto del progetto dal vault prima di rispondere - legge Scheda progetto e Stato attuale e, se viene dato un ID o un argomento, cerca solo le note collegate (To-Do, Decisioni, Glossario). Non modifica nulla. Usare con /contesto, /contesto ID o /contesto argomento, anche insieme a una richiesta nello stesso messaggio.
---

# Contesto del progetto

Questa nota **è** il file `SKILL.md`: per installarla, copiarla in `~/.claude/skills/contesto/SKILL.md` (progetto in team, flusso privato) o in `.claude/skills/contesto/SKILL.md` del repo (progetto personale). È generica: i percorsi del vault e le regole del progetto stanno nella `Scheda progetto`.

Serve quando si lavora **fuori dalle skill del flusso** (domande, debug, piccole modifiche) e si vuole che Claude parta dalle note del progetto, come fa `/lavora`. **Sola lettura**: non sposta task e non aggiorna note.

## 1. Lettura di base (sempre)

1. Leggi la `Scheda progetto` e `Stato attuale`.
2. Confronta la `versione_template` della Scheda con quella del `Changelog template`: se è più vecchia, segnalalo in una riga (senza applicare nulla).

## 2. Lettura mirata (solo se c'è un argomento)

Con `/contesto ID` o `/contesto argomento`:
- cerca l'ID o le parole chiave in `To-Do`, `Da controllare`, `Da valutare` e `To-Do Fatti` **con una ricerca**, senza leggere le note intere;
- cerca l'argomento in `Decisioni`, `Glossario` e nei titoli di sezione della `Mappa codice`;
- controlla `To-Do Rifiutati`, per non riproporre qualcosa già scartato.

Senza argomento non leggere altro: usa `00 Indice` solo per sapere cosa esiste.

## 3. Riepilogo

Massimo 6 righe:
- cosa è in corso (`Stato attuale`);
- le regole della Scheda che contano per la richiesta (comandi vietati, piattaforme, convenzioni);
- decisioni, termini o task trovati al passo 2, con il nome della nota.

Se nello stesso messaggio c'è una richiesta, dopo il riepilogo procedi con quella, rispettando quello che hai letto.
