---
title: Configurazione Claude
type: configurazione
tags:
  - flusso
  - template
updated: 2026-10-15
---

Esempi di riferimento per i file di configurazione di Claude Code. Al setup Claude li copia sostituendo i valori tra `< >`; così permessi e regole sono uguali in tutti i progetti. **Resta nella copia ufficiale del template**: non va copiata nella cartella del progetto.

## 1. File di istruzioni

| Tipo di progetto | File | Dove |
|---|---|---|
| In team, flusso privato | `CLAUDE.local.md` | root del repo, escluso da git con `.git/info/exclude` |
| Personale | `CLAUDE.md` | root del repo |

È scritto in inglese, come da convenzione per i file di Claude; le note del vault restano in italiano. Per un progetto personale si tolgono le righe segnate *(team)*.

```markdown
# Personal workflow

## Where things are
- Project sheet: <PROJECT_FOLDER>/Scheda progetto.md — read it first. Everything project-specific (task IDs, platforms, commands, forbidden commands, credentials, recurring task types) is there.
- My vault folder for this project: <PROJECT_FOLDER>/ — start from `Stato attuale.md` and `00 Indice.md`, open only what you need. Never read `To-Do.md` in full: search by ID.
- Official flow template: <TEMPLATE_PATH>

## Hard rules
- Never git commit, push, merge or rebase; never create or merge PRs; never run the commands forbidden in the project sheet. Prepare the commit message and the PR text instead.
- (team) Team platforms (repo, tracker, CI/CD) are read-only for you.
- (team) Never modify shared repo files: CLAUDE.md, .gitignore, the team's .claude/ folder, .mcp.json.
- (team) Never put vault content, my workflow or credentials into repo files, commits, PRs or tickets. Never mention the vault outside the vault.
- Project facts (tasks, decisions, code locations, terms) go in the vault, never in your auto-memory; use auto-memory only for local-environment gotchas the vault does not cover, and never duplicate the two.
- Credentials never go in the vault: write only the reference format from the project sheet (e.g. `🔑 KeePass: Group/Entry`). If you find one in clear text, tell me.
- (team) Code conventions are the team's; the workflow is mine.

## Working style
- Make the smallest change that fully solves the task.
- Don't guess domain terms: if a term is not in `Glossario.md` and not clear from the code, ask me, then propose a glossary entry.
- Don't re-propose anything in `To-Do Rifiutati.md`; don't contradict `Decisioni.md` without asking.
- When something is ambiguous, ask instead of guessing.
- Code, comments and commit messages in English (unless the team convention differs); vault notes in Italian.
- Any text I will send to others (card comments, PR texts, questions, chat messages, also my own drafts) follows the "Regole per i testi" section of the `lavora` skill: no curt closings, a mistake is admitted first with the fix (process causes go to `Da valutare` as `#proposta-team`, never in the same text), a disagreement is stated once with one argument.

## Flow
To-do Appunti → Da valutare → To-Do (⏸ waiting, ▶ in progress) → Da controllare → To-Do Fatti | To-Do Rifiutati
- Skills: /smista, /lavora, /chiudi, /rifiuta, /valuta, /guidami, /manutenzione, /contesto, /analisi, plus the task-type skills listed in the project sheet.
- Three ways to handle a task: `/lavora ID` (you work, ask for approval on the plan when the change is large), `/lavora ID autonomo` (full delegation: stop only for ambiguity, forbidden commands or shared data), `/guidami ID` (I write the code, you guide and review read-only).
- After a change visible in the browser, ask me whether to run /verifica-web and state the estimated cost (🟢 saved script, 🟡/🔴 exploration). Never on production.
- When closing a task, if at least 3 done tasks of the same type have no skill, add a "create skill" proposal to `Da valutare.md`.
- Flow changes: decide whether a change is project-specific (→ project sheet) or part of the base flow (→ apply it also to the official template in generic form, bump `versione_template`, add an entry to `Changelog template.md`). Ask if unclear. Never sync content notes (tasks, notes, decisions, glossary, sheet).
```

## 2. Permessi

| Tipo di progetto | File |
|---|---|
| In team, flusso privato | `.claude/settings.local.json` (personale; escluso da git) |
| Personale | `.claude/settings.json` |

Usare **percorsi assoluti**. Ai divieti si aggiungono i *comandi vietati* della `Scheda progetto`. Le righe `keepassxc-cli` servono solo se si usa il database KeePass di Claude.

```json
{
  "permissions": {
    "allow": [
      "Bash(keepassxc-cli show:*)",
      "Bash(keepassxc-cli ls:*)",
      "Bash(keepassxc-cli add:*)",
      "Bash(keepassxc-cli edit:*)"
    ],
    "deny": [
      "Bash(git commit:*)",
      "Bash(git push:*)",
      "Bash(git merge:*)",
      "Bash(git rebase:*)",
      "Bash(gh pr create:*)",
      "Bash(gh pr merge:*)",
      "Bash(keepassxc-cli export:*)"
    ],
    "additionalDirectories": [
      "<PERCORSO ASSOLUTO DELLA CARTELLA DEL PROGETTO NEL VAULT>",
      "<PERCORSO ASSOLUTO DELLA COPIA UFFICIALE DEL TEMPLATE>",
      "<PERCORSO ASSOLUTO DELLA CARTELLA DEGLI SCRIPT DI TEST>"
    ]
  }
}
```

In `additionalDirectories` va **la cartella del progetto**, non tutto il vault: così Claude non legge le note degli altri progetti.

## 3. Esclusioni da git (solo flusso privato)

Righe da aggiungere a `.git/info/exclude` del repo (funziona come `.gitignore` ma resta solo sul proprio computer):

```
CLAUDE.local.md
.claude/settings.local.json
.claude/hookify.*
.playwright-mcp/
```

L'ultima riga serve se Playwright salva per errore i suoi file nella cartella del repo.

## 4. Playwright per i test web (facoltativo)

Dalla cartella del repo, una volta per progetto (dettagli in [[Guida configurazione flusso AI#Playwright per i test web]]):

```
claude mcp add playwright --scope local -- npx @playwright/mcp@latest --isolated --storage-state <FILE DI ACCESSO SALVATO> --output-dir <CARTELLA FUORI DAL REPO> --allowed-origins "<URL LOCALE>;<URL DI TEST>" --blocked-origins "<URL DI PRODUZIONE>"
```

- `--output-dir`: Playwright salva le pagine lette in file; senza questa opzione li mette nella cartella del repo (`.playwright-mcp/`).
- `--allowed-origins` e `--blocked-origins` sono una **guida, non una barriera di sicurezza** (lo dice la documentazione di Playwright, e non valgono per i redirect). La vera protezione è che il file di accesso salvato è solo per gli ambienti di test.
