---
title: Regole hookify
type: configurazione
tags:
  - flusso
  - template
updated: 2026-10-14
---

Regole pronte per il plugin **hookify**: bloccano o segnalano comportamenti indesiderati anche se Claude dimentica le istruzioni. Sono una seconda protezione oltre ai permessi. **Resta nella copia ufficiale del template.**

**Installazione:** ogni blocco qui sotto è un file da salvare nella cartella `.claude/` del repo con il nome indicato. Hookify li legge subito, senza riavviare. Nel flusso privato i file sono già esclusi da git con la riga `.claude/hookify.*`. Per vederli: `/hookify:list`.

I pattern sono espressioni regolari Python e **non distinguono maiuscole e minuscole**. Per controllare il testo scritto si usa il campo `content`, che vale sia per i file nuovi sia per le modifiche.

## 1. Niente commit, push, merge, rebase e PR

`.claude/hookify.blocca-git.local.md`
```markdown
---
name: blocca-git
enabled: true
event: bash
pattern: \bgit\s+(commit|push|merge|rebase)\b|\bgh\s+pr\s+(create|merge)\b
action: block
---

🛑 **Commit, push, merge, rebase e PR li fa solo l'utente.**
Prepara il messaggio di commit e il testo della PR e mettili nella voce del task in `Da controllare`.
```

## 2. Comandi vietati del progetto

Adattare il pattern ai *comandi vietati* della `Scheda progetto`. Esempio per Azure DevOps e SQL:

`.claude/hookify.comandi-vietati.local.md`
```markdown
---
name: comandi-vietati
enabled: true
event: bash
pattern: \baz\s+(pipelines|repos|devops)\b|\bsqlcmd\b|Invoke-Sqlcmd
action: block
---

🛑 **Comando vietato dalla Scheda progetto.**
Pipeline e script sui database li esegue solo l'utente: prepara il comando o lo script e lascialo nella voce del task.
```

## 3. Mai esportare il database KeePass

`.claude/hookify.keepass-export.local.md`
```markdown
---
name: keepass-export
enabled: true
event: bash
pattern: keepassxc-cli\s+export
action: block
---

🛑 **Esportare il database KeePass è vietato**: metterebbe tutte le credenziali in chiaro.
Leggi solo la voce che serve con `keepassxc-cli show`.
```

## 4. Credenziali scritte in un file

`.claude/hookify.credenziali.local.md`
```markdown
---
name: credenziali
enabled: true
event: file
action: warn
conditions:
  - field: content
    operator: regex_match
    pattern: (password|passwd|pwd|secret|apikey|api_key|token|decryptkey)["']?\s*[:=]\s*(["'][^"'\s]{6,}["']|[^\s"';.()]{6,};)
---

🔐 **Sembra una credenziale scritta in chiaro.**
Nel vault va solo il riferimento (es. `🔑 KeePass: Gruppo/Voce`); nel codice va letta dalla configurazione, mai scritta. Se è un falso allarme, prosegui.
```

## 5. Attenzione a To-Do Rifiutati

`.claude/hookify.rifiutati.local.md`
```markdown
---
name: rifiutati
enabled: true
event: file
action: warn
conditions:
  - field: file_path
    operator: regex_match
    pattern: To-Do Rifiutati\.md$
  - field: old_text
    operator: regex_match
    pattern: \S
---

⚠️ **Stai modificando o togliendo righe da `To-Do Rifiutati`.**
È la memoria delle idee scartate: si aggiungono righe, non si tolgono. Procedi solo se l'utente l'ha chiesto.
```

## 6. File condivisi del repo (solo flusso privato)

`.claude/hookify.file-condivisi.local.md`
```markdown
---
name: file-condivisi
enabled: true
event: file
action: block
conditions:
  - field: file_path
    operator: regex_match
    pattern: (^|[\\/])(CLAUDE\.md|\.gitignore|\.mcp\.json|\.claude[\\/]settings\.json)$
---

🛑 **File condiviso con il team.**
Il flusso è privato: le regole personali vanno in `CLAUDE.local.md` e `.claude/settings.local.json`.
```
