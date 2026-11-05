<p align="center"><img src="docs/icon.png" width="128" alt="Buddy" style="image-rendering: pixelated" /></p>

# Buddy

A small pixel-art desk companion for Windows, and a mentor. Click it to open a chat bubble:

- **local model** (Ollama) for quick answers grounded on the notes of one Obsidian project folder (read-only);
- **Claude** (Claude Code CLI) for real tasks, in the same thread, with permission prompts answered from the bubble;
- **mentor**: its main goal is to help you grow as a developer and as a person, not just to make work easier.

## Requirements

- Windows 10/11, .NET 10 SDK
- [Ollama](https://ollama.com) running locally, with the configured model pulled (`ollama pull llama3.2:3b`)
- Claude Code CLI logged in (`claude` then `/login` in a terminal)
- For dictation: a Whisper ggml model, e.g. `ggml-small.bin` from `huggingface.co/ggerganov/whisper.cpp`, in
  `%APPDATA%\Buddy\models\` (or set its path in the settings)

Windows only: the UI is WPF, and the tray icon, global shortcut, idle detection and microphone use Windows APIs.
`Buddy.Core` (vault, context, Ollama, Claude bridge, mentor) is plain .NET and cross-platform; a Linux version would
need a new UI layer (e.g. Avalonia) and replacements for those Windows-specific pieces.

## First run

A short wizard asks your name, the buddy's name and **personality** (calm mentor, motivating coach, ironic friend,
direct and challenging, old sage, or your own description), your **interests** outside work (music, films, chess…,
used for off-topic chats that keep you company) and the journal folder. It can be repeated from
Impostazioni → Mentore e voce.

## Mentor

| | |
|---|---|
| Check-in / check-out | Morning: priorities from the vault and one question to plan with intention. Evening: what went well, what blocked you, what you'd change, mood 1-5. |
| Before delegating | When you press a task button (e.g. `/lavora ID`), it first asks how *you* would approach it; Claude then reviews your approach before starting. |
| Pills | Knowledge pills (e.g. TCP vs UDP), method tips, "explain it to me" questions, everyday-approach ideas, chosen from your profile; never repeated (`Pillole.md`). |
| Spaced review | A quick recall question on a pill seen days ago (after 2, 7, 21, 60 days; a wrong or partial answer brings it back after 2). Claude grades the answer (✓ / ~ / ✗), explains what was missing and logs the result in `Pillole.md`. Nothing is asked when no pill is due. |
| Chats | Growth questions (books, what you are learning), off-topic questions on your interests just for company, and questions to get to know you better. |
| Profile | `Profilo.md` keeps goals, reading, learning, interests, strengths, things to improve and a **personal profile** (motivation, work and learning style, how you handle stress and feedback). Written from your answers as dated hypotheses with the evidence, never diagnoses — yours to read and correct. |
| Breaks and focus | A break suggestion after long continuous activity at the PC; `/focus [min]` silences the mentor and asks how it went. |
| Weekly retro | On the chosen day Claude writes `Retro/<year>-W<week>.md` from the diary, the vault and your commits, with one strength and one habit to work on. |
| Trends | The buddy counts, per week, active hours at the PC, focus blocks, check-ins, pills, reviews, chats and approaches (`/andamento` shows the last 4 weeks). The retro adds a row to `Andamento.md` (average mood, hours, focus, reviews, whether last week's habit was kept) and compares the week with the previous ones. |
| Do not disturb | The mentor stays quiet while another app uses the microphone (a call), an app is full screen or presenting, or Windows "do not disturb" is on; due messages wait until you are free. |

Your answers are written by Claude (one-shot `claude -p` with a cheap model) into the journal folder only: writing
elsewhere is denied by the CLI. Commands: `/checkin`, `/checkout`, `/pillola`, `/ripasso`, `/chiacchiera`, `/retro`,
`/focus`, `/andamento`, `/salta`, `/mentore`.

## Voice

🎤 in the bubble: click to talk, click again to stop; the text is transcribed **locally** with Whisper and put in the
input box to check before sending. Nothing leaves the PC.

## Configuration

Right click on the pet → **Impostazioni…** edits everything below and restarts the buddy to apply it:
appearance (name, font, bubble colours with light/dark presets), the character (colours, size, animation speed,
always on top, remembered position, start with Windows, custom pixel art), models and paths, the local model's
personality (system prompt), Claude tools allowed without asking, and the chat phrases.

**Custom pixel art**: a folder with one PNG per state (`idle`, `think`, `work`, `alert`, `error`), each a horizontal
strip of square frames; `claude-<state>.png` applies only in Claude mode; missing files fall back to the built-in
character. "Esporta modello" writes the built-in frames there as a starting point.

Defaults are in `src/Buddy.App/appsettings.json`. Personal values go in `%APPDATA%\Buddy\appsettings.json` (written by
the settings window), which overrides them. Window position is kept in `%APPDATA%\Buddy\state.json`. Minimal example:

```json
{
  "Buddy": {
    "ProjectPath": "C:\\path\\to\\vault\\project",
    "Claude": { "WorkingDirectory": "C:\\path\\to\\repo" }
  }
}
```

| Setting | Meaning |
|---|---|
| `ProjectPath` | Vault folder the local model reads (never written). Also passed to Claude with `--add-dir`. |
| `AlwaysIncludedNotes` | Notes always added to the local context (default none). |
| `TaskIdPattern` | Regex for task ids; blocks mentioning an id found in the question are added to the context. |
| `MaxContextChars` | Vault text budget for the local model. Bigger = slower first token. |
| `Ollama.Model` / `KeepAlive` | Model and how long it stays loaded. The app preloads it at startup. |
| `Claude.WorkingDirectory` | Folder Claude Code runs in (its CLAUDE.md, skills and settings apply). |
| `Claude.Model` | Optional model alias. |

## Usage

- Click the pet: open/close the bubble. Drag it to move. Right click: menu (chat, new conversation,
  open the project vault in Obsidian, settings, exit).
- Type and press Enter (Shift+Enter for a new line).
- ⛶ in the bubble title (or F11, or a double click on the title): full screen on the pet's monitor, with wider
  messages for long answers; again to go back next to the pet.

| Command | Effect |
|---|---|
| `/c [text]` | Switch to Claude (and send the text) |
| `/l [text]` | Switch back to the local model |
| `/stop` | Interrupt Claude's current answer |
| `/nuova` | Close the Claude conversation and start a new one |
| `/aiuto` | Command list |

Local answers end with **Fonti**: links to the vault notes (open in Obsidian) and guideline sections they were
built from. **Chiedi a Claude ↗** under a local answer sends the same question (and attachment) to Claude.

The pet is green when talking to the local model and purple with Claude. States: idle, thinking (local model),
working (Claude), alert (permission request), error.

## Developer assistant

| Feature | How |
|---|---|
| Current task | Read from the git branch name of the repo (e.g. `NUSR-2367-…`); shown in the pet tooltip and the bubble, and used as context when a question names no task. |
| Task buttons | `Assistente → Pulsanti del task` templates (`/lavora {id}`…) shown in the bubble, sent to Claude. |
| Instant commands | `/stato [ID]`, `/oggi`, `/cerca parole`, `/modifiche` — no model, answered from the vault and git in milliseconds. Links open the note in Obsidian. |
| Drafts | `/commit`, `/pr` — Claude gets the diff (and the PR template) and returns text to copy; it is told not to commit or run git. |
| Guidelines | Questions are also matched (keyword search weighted by rarity) against `Repo.KnowledgePaths` (e.g. `docs/guidelines`, `AGENTS.md`). |
| Attachments | Global shortcut (default Ctrl+Alt+B) opens the chat with the clipboard (text, image or files) attached; files, text or images can also be dropped on the pet. Images go only to Claude. |
| Notifications | When a Claude answer takes longer than `NotifyAfterSeconds` and the bubble is not focused, a Windows notification appears (tray icon). |
| Walking | When idle and the chat is closed, the pet strolls left/right along its screen row (`Personaggio` tab: on/off, speed, pause). Custom art: `walk.png`. |
| Project presets | Right click → Progetto: switch between saved project setups (vault, repo, Claude folders, docs, groups, conventions, task buttons) or save the current one. Also in `Impostazioni → Progetti`. |

## How it works

- `Buddy.Core` — vault index (`Vault/`), deterministic context builder (`Context/`), Ollama client (`Llm/`),
  Claude Code bridge (`Claude/`), chat thread and routing (`Chat/Conversation.cs`).
- Claude runs as `claude -p --input-format stream-json --output-format stream-json --permission-prompt-tool stdio`:
  one long-lived process per conversation; permission prompts arrive as `can_use_tool` control requests and are
  answered with a `control_response`. The session id is kept so the conversation resumes with `--resume`.
- `Buddy.App` — WPF: `Pet/` (sprite drawn from character grids in `PetSprites.cs`), `Chat/` (bubble).

## Logs

`%APPDATA%\Buddy\logs\buddy-<date>.log` (kept 14 days; tray icon → **Apri i log**): start-up, Claude turns and
errors, `claude -p` calls of the mentor, Ollama failures, when the mentor goes quiet. Unexpected UI errors are logged
and shown in the chat instead of closing the app.

## Development

```powershell
dotnet build Buddy.sln
dotnet test Buddy.sln            # ClaudeProcessTests needs node on PATH (fake CLI)
src\Buddy.App\bin\Debug\net10.0-windows\Buddy.App.exe --snapshot <folder>   # renders sprites and a sample chat to PNG
```
