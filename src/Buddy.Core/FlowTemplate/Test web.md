---
title: Test web
type: flusso
tags:
  - flusso
  - test
updated: 2026-10-16
---

Configurazione e registro dei test web. Serve **solo** alla skill `verifica-web` (le altre skill non la leggono): se un flusso ha già uno script il test costa poco (🟢), perché il browser lo guida lo script e non Claude.

## Configurazione

- **Ambienti permessi:** (es. locale `http://localhost:…`, test `https://…`) — **mai produzione**
- **Ambienti condivisi** (dove non modificare dati senza chiedere): 
- **Login:** (già fatto nel browser / utente di test nel database KeePass di Claude / lo faccio io)
- **Strumento:** (Playwright MCP consigliato / Claude in Chrome)
- **File di accesso salvato** (Playwright, fuori da repo e vault): 
- **Cartella degli screenshot:** (nel vault, fuori dal repo)
- **Cartella degli script di test:** (progetto Playwright **fuori dal repo e fuori dal vault**, es. `~/claude-test/<progetto>/`)

> [!example] Formato degli script
> ```
> - **login-contratti** · `contratti/login-contratti.spec.ts` — Cosa copre: login e apertura della pagina Contratti · Ultima esecuzione: 2026-10-14 ✅
> ```

## Script

