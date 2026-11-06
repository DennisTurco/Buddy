---
title: To-Do
type: flusso
tags:
  - flusso
prossimo_id: T-001
updated: 2026-10-16
---

Task da fare. **Non leggere tutto: cercare per ID.** Il formato dell'ID è nella [[Intelligenza Artificiale/Template flusso/Scheda progetto]] (es. chiave Jira); `T-001`… per i task senza card. La proprietà `prossimo_id` dice il prossimo ID libero ed è un **contatore unico** per tutti i `T-…`, anche per le voci di `Da valutare` (l'ID non cambia quando passano da una nota all'altra); la aggiorna `/smista`. I task ⏸ sono decisi ma non per ora: `/lavora` li salta.

> [!example] Formato
> ```
> - [ ] T-001 · 2026-10-14 · Titolo breve del task [priorita:: P2] [stima:: 4h] [tag:: feature] [tipo:: Startup]
>   - Contesto: [[Nota collegata]]
>   - Dove: `src/Ordini/OrdiniService.cs` (metodo `Salva`), `ordini.component.ts`
>   - Fatto quando: risultato concreto e verificabile
>   - Assegnato da: Persona A · Indicazioni da: Persona B
>   - Chi chiedere: Persona C (come testare l'import)
>   - Accordi: 2026-10-15 con Persona A — il caso X resta fuori, card separata
> - [ ] T-002 · ⏸ Titolo breve — In attesa di: rilascio 2.0 [priorita:: P3] [stima:: 2h]
> - [ ] T-005 · ⏸ Titolo breve — In attesa di: chiusura da Persona B (verificare che sia chiuso) [priorita:: P2] [stima:: 0.5h]
> - [ ] T-003 · ⏸ Titolo breve — In attesa di: #145 [priorita:: P2] [stima:: 1h]
> - [ ] T-004 · ⏸ Titolo breve [fino:: 2026-11-01] [priorita:: P3] [stima:: 3h]
> ```
> Un task tornato da rifare dopo la revisione prende 🔁1, 🔁2, … e una nota correttiva.
> **Persone e accordi** (righe facoltative, solo se servono): `Assegnato da:` chi ha assegnato il task; `Indicazioni da:` solo se qualcun altro dà indicazioni, e allora alla consegna vanno avvisati entrambi; `Chi chiedere:` una persona precisa per i dubbi (es. come testare), da contattare il giorno stesso; `Accordi:` una riga per ogni accordo preso a voce, con data e persona, che alla consegna finisce nel commento per la card. **Il task resta mio finché non è chiuso:** se il fix lo fa qualcun altro, diventa ⏸ `In attesa di: chiusura da Persona` e si chiude solo dopo aver verificato che è stato chiuso davvero.
> **Priorità:** `P1` alta (da fare per primo, blocca altro o porta più valore) · `P2` media · `P3` bassa. **Stima:** ore di lavoro, es. `2h`, `1.5h`. Se non le indico, le stima Claude quando crea il task.
> **Tag:** la natura del lavoro, sempre presente, uno solo: `ticket` (richiesta arrivata da fuori: card del tracker, segnalazione di un utente), `feature` (funzionalità nuova), `bug` (qualcosa non funziona), `analisi` (va capito o studiato, senza modifiche al codice), `miglioria` (rende meglio qualcosa che già funziona), `refactoring`, `documentazione`, `supporto` (aiuto a una persona, dati, configurazione). Non è il *Tipo*: il tag dice che genere di lavoro è, il tipo a quale procedura della Scheda appartiene. La bacheca di Buddy li mostra come etichette e filtra per tag.
> **Tipo:** uno dei tipi ricorrenti della Scheda, solo se il task lo è. **Dove:** file e punti di ingresso; li scrive `/smista` se si ricavano dall'appunto, altrimenti `/lavora` dopo aver esplorato. Evita di riesplorare il codice a ogni ripresa.

La tabella per priorità con il totale delle ore è nel Cruscotto.

## Task

