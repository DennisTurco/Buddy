---
title: Scheda progetto
type: scheda
tags:
  - flusso
versione_template: "1.15"
tracker_link: ""
tracker_id: ""
pr_link: ""
updated: 2026-10-16
---

Tutto ciò che è **specifico di questo progetto**. Il resto del template è generico e non va toccato: per adattare il flusso a un progetto nuovo basta compilare questa nota. Claude la legge all'avvio di quasi ogni skill, quindi **ogni riga costa token**: scrivere solo quello che le istruzioni del repo (`CLAUDE.md`, `AGENTS.md`…) non dicono già, e per il resto rimandare a loro. Le sezioni che non servono si cancellano.

## Template base

- **Percorso del template base:** `…` (la copia ufficiale, da tenere aggiornata)
- **Cartella del progetto nel vault:** `…` (es. `<Vault>/Progetti/<Nome>`)
- **Colore nel grafo:** (diverso da quello degli altri progetti; gruppo `path:"Progetti/<Nome>"` nel grafo di Obsidian)
- **Versione applicata a questo progetto:** vedi la proprietà `versione_template` qui sopra

## Progetto

- **Nome:** 
- **In una riga:** 
- **Stack, sotto-progetti, comandi:** solo se il repo non li descrive già; altrimenti "in `AGENTS.md`" (o il file di istruzioni del repo)

## Piattaforme

- **Repo e PR:** (GitHub, GitLab, Azure Repos…) 
- **CI/CD:** 
- **Tracker dei task:** (Jira, issue GitHub…) — link base: 
- **Link cliccabili** (proprietà in cima alla nota, facoltative): `tracker_link` con `{id}` al posto della chiave (es. `https://azienda.atlassian.net/browse/{id}`), `tracker_id` = regex degli ID del tracker (es. `^ABC-\d+$`, così i `T-…` restano interni), `pr_link` con `{n}` al posto del numero (es. `https://github.com/org/repo/pull/{n}`). Li usa il Cruscotto
- **Documentazione esterna:** (wiki, procedure) 

## Lavoro in team

- **Flusso privato?** (sì: nel repo non entra niente) 
- **Commit, push, PR li fa:** 
- **Comandi vietati a Claude** (oltre a commit, push, merge, rebase e PR): 
- **Convenzioni del team:** (dove trovarle) 

## ID dei task

- **Formato:** (es. chiave Jira `ABC-123`) 
- **Task senza card:** `T-001`, `T-002`…; contatore unico (`prossimo_id` in `To-Do`), valido anche per le voci di `Da valutare`

## Tipi di task ricorrenti

Il nome in grassetto è il valore del campo `[tipo:: …]` dei task: serve alle statistiche del Cruscotto e a `/chiudi` per proporre nuove skill.

- **<Tipo>** — come si riconosce · procedura o skill

## Feature flag

<!-- Solo se il progetto li usa: serve alla skill rimuovi-ff -->
- **Nome dei flag:** 
- **Dove si leggono** (tabella, file di configurazione…): 
- **Nel codice:** dove sono definiti e come vengono letti

## Manutenzione

<!-- Facoltativo: soglie diverse da quelle predefinite della skill manutenzione -->
- **Soglie personalizzate:** (es. `To-Do` 3.000 parole)

## Credenziali

- **Dove stanno:** (es. KeePass) 
- **Regola:** nel vault solo il riferimento `🔑 KeePass: Gruppo/Voce`, mai il valore.
- **Database KeePass di Claude** (facoltativo): percorso del file `.kdbx` · percorso del key file · cosa contiene (solo credenziali di sviluppo e test)

## Note per Claude

<!-- Regole valide solo per questo progetto -->
- Configurazione dei test web (ambienti, login, cartelle): nella nota `Test web`.
