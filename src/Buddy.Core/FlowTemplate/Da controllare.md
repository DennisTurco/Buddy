---
title: Da controllare
type: flusso
tags:
  - flusso
updated: 2026-10-20
---

Task implementati **in attesa della mia revisione**. Ogni voce contiene tutto quello che serve per controllare e chiudere. A fine lavoro Claude chiede se provare la modifica nel browser (skill `verifica-web`) e, se sì, aggiunge l'esito.

> [!example] Formato
> ```
> - [ ] T-001 · Titolo breve del task [tipo:: Startup]
>   - Cambiato: …
>   - File: …
>   - Come testare: 1. apri X  2. fai Y  3. deve succedere Z
>   - Test manuali: [[Test manuali T-001]] · 24 casi, 3 da rifare  (sempre, scritto e aggiornato da /lavora o /guidami)
>   - Commit: `feat: …` (pronto da copiare)
>   - PR: titolo e descrizione pronti da copiare
>   - Commento tracker: cosa è stato fatto e cosa è stato testato, per il PO, senza tecnicismi, più gli accordi presi a voce (solo se il task ha una card)
>   - Da avvisare: Persona A e Persona B (solo se assegnazione e indicazioni vengono da persone diverse)
>   - Fuori perimetro: "Già che ci sono sistemerei anche X, ok?" (domanda pronta da mandare; solo se c'è)
>   - Dimensione: 22 file · 640 righe — proposta di divisione: 1. … 2. …  (solo se supera la soglia)
>   - Costo: 3% del limite 5h · contesto 85k token  (misurato da /lavora; — se non disponibile)
>   - Test web: ✅ superato · screenshot ![[T-001-passo1.png]]  (solo se fatto)
> ```

## Voci

