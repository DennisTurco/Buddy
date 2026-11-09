---
title: LEGGIMI - Template flusso
type: guida
versione_template: "1.22"
tags:
  - flusso
  - template
updated: 2026-11-05
---

**Copia ufficiale** del template del flusso descritto in [[Ottimizzare L'AI]]. Ogni nota spiega in una riga a cosa serve e contiene un esempio di voce: Claude copia il formato invece di inventarlo. Guida passo passo: [[Guida configurazione flusso AI]].

## Struttura del vault

Un unico vault, una cartella per progetto:

```
<Vault>/
├─ Intelligenza Artificiale/
│  ├─ Template flusso/      ← questa cartella: la copia ufficiale
│  └─ Profili progetto/     ← schede progetto già compilate
└─ Progetti/
   ├─ User/                 ← note del flusso + Scheda progetto
   └─ EnergyDeal/
```

## Come usarlo per un nuovo progetto

1. Creare la cartella `Progetti/<Nome progetto>/`.
2. Copiarci **solo le note del progetto** (colonna *Nel progetto* qui sotto). `Skill/`, `Configurazione/`, `Modelli/`, questo LEGGIMI e il changelog restano qui.
3. Compilare la [[Intelligenza Artificiale/Template flusso/Scheda progetto]]: è l'**unica nota da personalizzare**.
4. Aggiungere nel grafo di Obsidian (*Groups* → *New group*) un gruppo `path:"Progetti/<Nome progetto>"` con il colore scelto nella Scheda, **prima** del gruppo `path:"Intelligenza Artificiale"`: Obsidian usa il primo gruppo che corrisponde.
5. Lanciare il [[Prompt di avvio]].

## Le note

| Nota                   | Nel progetto | Chi la usa  | A cosa serve                                                                                                                                                                                            |
| ---------------------- | ------------ | ----------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [[Intelligenza Artificiale/Template flusso/Scheda progetto]]    | ✅            | io / Claude | Personalizzazione: ID, piattaforme, comandi, credenziali, tipi di task, test web                                                                                                                        |
| [[Intelligenza Artificiale/Template flusso/00 Indice]]          | ✅            | Claude      | Punto di partenza: link a tutte le note                                                                                                                                                                 |
| [[Intelligenza Artificiale/Template flusso/Stato attuale]]      | ✅            | Claude      | 4 righe scritte da Claude per orientarsi a inizio sessione (niente conteggi)                                                                                                                             |
| [[Intelligenza Artificiale/Template flusso/Cruscotto]]          | ✅            | **io**      | Conteggi e liste calcolati da Dataview. Claude non lo apre                                                                                                                                              |
| [[Intelligenza Artificiale/Template flusso/To-do Appunti]]      | ✅            | io          | Inbox libera                                                                                                                                                                                            |
| [[Intelligenza Artificiale/Template flusso/Da valutare]]        | ✅            | Claude      | Idee su cui non ho deciso **se** farle                                                                                                                                                                  |
| [[Intelligenza Artificiale/Template flusso/To-Do]]              | ✅            | Claude      | Task da fare, ⏸ in attesa e ▶ in corso                                                                                                                                                                             |
| [[Intelligenza Artificiale/Template flusso/Da controllare]]     | ✅            | Claude      | Task implementati da rivedere                                                                                                                                                                           |
| [[Intelligenza Artificiale/Template flusso/To-Do Fatti]]        | ✅            | Claude      | Task approvati                                                                                                                                                                                          |
| [[Intelligenza Artificiale/Template flusso/To-Do Rifiutati]]    | ✅            | Claude      | Idee scartate, con motivo                                                                                                                                                                               |
| [[Intelligenza Artificiale/Template flusso/Decisioni]]          | ✅            | Claude / io | Scelte fatte e perché                                                                                                                                                                                   |
| [[Intelligenza Artificiale/Template flusso/Glossario]]          | ✅            | io / Claude | Termini del progetto, aggiunti quando servono                                                                                                                                                           |
| [[Intelligenza Artificiale/Template flusso/Test web]]           | ✅            | Claude      | Configurazione dei test web (ambienti, login, cartelle) e script già pronti: se ce n'è uno, il test costa poco. La legge solo `verifica-web` |
| [[Intelligenza Artificiale/Template flusso/Mappa codice]]       | ✅            | Claude      | Dove si tocca il codice, per area o tipo di task: la legge `/lavora` prima di esplorare, la aggiorna `/chiudi` |
| `Analisi/`             | ✅            | Claude / io | Analisi di implementazione delle funzionalità, una nota per analisi sul modello [[Intelligenza Artificiale/Template flusso/Modelli/Nuova analisi]]. La crea `/analisi` alla prima analisi |
| `Test manuali/`        | ✅            | **io** / Claude | Un documento per task (`Test manuali <ID>`) con la checklist dei test manuali, sul modello [[Intelligenza Artificiale/Template flusso/Modelli/Nuovo test manuale]]: lo scrivono `/lavora` e `/guidami` alla prima modifica e lo aggiornano a ogni modifica successiva (righe **Da rifare**); le caselle le spunto io. La crea `/lavora` al primo task |
| [[Intelligenza Artificiale/Template flusso/Changelog template]] | ❌            | Claude      | Versione del flusso base e storico delle modifiche                                                                                                                                                      |
| `Skill/`               | ❌            | Claude      | Skill da installare: [[Intelligenza Artificiale/Template flusso/Skill/Skill smista]], [[Intelligenza Artificiale/Template flusso/Skill/Skill lavora]], [[Intelligenza Artificiale/Template flusso/Skill/Skill chiudi]], [[Intelligenza Artificiale/Template flusso/Skill/Skill rifiuta]], [[Intelligenza Artificiale/Template flusso/Skill/Skill valuta]], [[Intelligenza Artificiale/Template flusso/Skill/Skill guidami]], [[Intelligenza Artificiale/Template flusso/Skill/Skill manutenzione]], [[Intelligenza Artificiale/Template flusso/Skill/Skill contesto]], [[Intelligenza Artificiale/Template flusso/Skill/Skill verifica-web]], [[Intelligenza Artificiale/Template flusso/Skill/Skill rimuovi-ff]], [[Intelligenza Artificiale/Template flusso/Skill/Skill analisi]], [[Intelligenza Artificiale/Template flusso/Skill/Skill test-manuali]] |
| `Configurazione/`      | ❌            | Claude      | Esempi di file di Claude ([[Intelligenza Artificiale/Template flusso/Configurazione/Configurazione Claude]]) e regole pronte ([[Intelligenza Artificiale/Template flusso/Configurazione/Regole hookify]])                                                                                                               |
| `Modelli/`             | ❌            | io          | Modelli Templater per inserire una voce nel formato giusto (task, task ⏸, idea, decisione, termine, appunto, analisi, test manuale): comando *Templater: Insert template* dentro la nota di destinazione (per l'analisi, una nota nuova e vuota in `Analisi/`) |

## Tenere il template aggiornato

Questa è **l'unica copia ufficiale**: il suo percorso è scritto in ogni `Scheda progetto`.

1. Quando chiedo un cambiamento al flusso, Claude decide se è **specifico del progetto** o **del flusso base**. Se non è chiaro, chiede.
2. **Specifico** → solo nel progetto, di solito nella `Scheda progetto`.
3. **Del flusso base** → nel progetto **e** qui, in forma generica (niente nomi, ID o dettagli del progetto). Poi aumenta `versione_template`, aggiunge una voce al [[Intelligenza Artificiale/Template flusso/Changelog template]] e aggiorna la versione nella `Scheda progetto`.
4. Negli altri progetti, a inizio sessione Claude confronta la loro `versione_template` con quella ufficiale e propone le modifiche arrivate dopo, una alla volta.

Si sincronizzano solo struttura, formati, regole, skill e configurazione. **Mai** il contenuto: task, appunti, decisioni, glossario, scheda.

## Note tecniche

- **Stato attuale e Cruscotto sono separati** perché Dataview calcola i risultati solo dentro Obsidian: Claude leggerebbe il codice della query, non i numeri. Il [[Intelligenza Artificiale/Template flusso/Cruscotto]] è per me, [[Intelligenza Artificiale/Template flusso/Stato attuale]] è per Claude.
- **Le date sono campi** che Dataview riconosce: `[fino:: 2026-11-01]` per i task ⏸, `[rivedere:: 2026-11-15]` per le voci di `Da valutare`.
- **Priorità, stima e tipo** dei task in `To-Do` sono campi (`[priorita:: P1]`, `[stima:: 4h]`, `[tipo:: …]`): il Cruscotto li mostra in tabella (con il totale delle ore) e fa le medie di costo e 🔁 per tipo. La tabella sta nel Cruscotto e non in `To-Do` perché Claude legge `To-Do` e non deve pagare in token il codice della query.
- **Le query del Cruscotto guardano solo la propria cartella**, quindi ogni progetto ha il suo Cruscotto anche nello stesso vault.
- **ID e PR cliccabili**: le proprietà `tracker_link`, `tracker_id` e `pr_link` della `Scheda progetto` rendono gli ID link al tracker (i `T-…` portano alla nota) nel Cruscotto e nella *Vista* di `To-Do`.
- **Modelli Templater** (`Modelli/`, cartella impostata in Templater per tutto il vault): i modelli di task e idee chiedono l'ID; se lo lascio vuoto usano `prossimo_id` di `To-Do` e lo aumentano dopo l'inserimento. Funzionano in qualsiasi progetto perché leggono la cartella della nota corrente.
- **Risparmio di token**: la `Scheda progetto` non ripete le istruzioni del repo; la configurazione dei test web sta in `Test web`, letta solo da `verifica-web`; le skill meccaniche girano su un modello più economico (`model:` nel frontmatter: `rifiuta` su haiku, `smista`, `valuta`, `chiudi`, `manutenzione` su sonnet; vale per il resto del turno); `/lavora` delega le ricerche ampie a un sotto-agente di esplorazione e parte da `Dove:` e `Mappa codice`.
- **Costo misurato**: `/lavora` legge l'uso del piano (% del limite di 5 ore, token di contesto) all'inizio e alla consegna, se lo strumento c'è (app desktop); `/chiudi` lo riporta in `To-Do Fatti`.
- **Memoria automatica di Claude**: i fatti del progetto stanno nel vault; la memoria automatica solo per problemi dell'ambiente locale che il vault non copre (regola nel file di istruzioni, vedi [[Intelligenza Artificiale/Template flusso/Configurazione/Configurazione Claude]]).
