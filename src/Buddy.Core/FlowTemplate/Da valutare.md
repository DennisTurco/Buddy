---
title: Da valutare
type: flusso
tags:
  - flusso
updated: 2026-10-19
---

Idee su cui **non ho ancora deciso se farle**. Claude non ci lavora mai. Con `/valuta` me le propone una alla volta: approva → [[Intelligenza Artificiale/Template flusso/To-Do]] · rimanda → nuova data · rifiuta → [[Intelligenza Artificiale/Template flusso/To-Do Rifiutati]]. L'ID non cambia.

> [!example] Formato
> ```
> - [ ] T-004 · 2026-10-14 · Titolo breve — Dubbio: perché non è deciso [rivedere:: 2026-11-15] [consiglio:: 🟡] [tag:: miglioria]
>   - Riflessione di Claude: una o due frasi su valore, costo o rischio principale, eventuale alternativa
> ```
> **Tag:** come in `To-Do` (`feature`, `bug`, `analisi`, `miglioria`…); resta uguale quando l'idea viene approvata.
> **Consiglio** (lo dà Claude insieme alla riflessione): 🟢 consigliata · 🟡 dipende, da valutare bene · 🔴 sconsigliata o non ora. Il Cruscotto lo mostra nella tabella *🤔 Da valutare*.
> **Proposte al team** (miglioramenti di processo o di codice fuori dal task, comprese le cause di un errore): tag `#proposta-team`. Se approvate diventano una card o una voce della board delle idee del team, **mai** una modifica dentro la PR di una feature o un intervento al daily.

## Voci

