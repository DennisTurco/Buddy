namespace Buddy.Core;

/// <summary>
/// User-editable settings, bound from the "Buddy" section of appsettings.json and of the per-user
/// override file written by the settings window.
/// </summary>
public sealed class BuddySettings
{
    public const string SectionName = "Buddy";

    /// <summary>Vault folder of the active project (read-only for the buddy).</summary>
    public string ProjectPath { get; set; } = "";

    /// <summary>Notes always added to the local model context, relative to <see cref="ProjectPath"/>.</summary>
    public List<string> AlwaysIncludedNotes { get; set; } = [];

    /// <summary>Regex that recognises task IDs inside a question (e.g. NUSR-1234, or T-001 for tasks without a tracker card).</summary>
    public string TaskIdPattern { get; set; } = @"\b[A-Z]{1,10}-\d+\b";

    /// <summary>
    /// Upper bound for the vault text passed to the local model. Keep it well below the model's context window
    /// (Ollama defaults to 4096 tokens and silently drops the overflow); prompt size also drives latency.
    /// </summary>
    public int MaxContextChars { get; set; } = 6_000;

    public OllamaSettings Ollama { get; set; } = new();

    public ClaudeSettings Claude { get; set; } = new();

    public AppearanceSettings Appearance { get; set; } = new();

    public PetSettings Pet { get; set; } = new();

    public PhraseSettings Phrases { get; set; } = new();

    public RepoSettings Repo { get; set; } = new();

    public AssistantSettings Assistant { get; set; } = new();

    public MentorSettings Mentor { get; set; } = new();

    public VoiceSettings Voice { get; set; } = new();

    /// <summary>Saved project setups; switching copies one into the fields above (see <see cref="ProjectPreset.ApplyTo"/>).</summary>
    public List<ProjectPreset> Presets { get; set; } = [];

    /// <summary>Name of the preset currently applied, for the menu check mark; empty when none.</summary>
    public string ActivePreset { get; set; } = "";

    /// <summary>False until the first-run wizard (name, interests, personality, journal) has been completed.</summary>
    public bool FirstRunCompleted { get; set; }
}

/// <summary>Local speech-to-text with Whisper.</summary>
public sealed class VoiceSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Whisper ggml model file; empty uses &lt;app data&gt;/Buddy/models/ggml-small.bin.</summary>
    public string ModelPath { get; set; } = "";

    public string Language { get; set; } = "it";
}

/// <summary>
/// The buddy as a mentor: daily check-in/out, breaks and focus, weekly retrospective, knowledge pills,
/// friendly chats and "how would you do it?" before delegating. What you answer is written by Claude into
/// <see cref="JournalPath"/> (diary, profile, pills log); the buddy itself never writes there.
/// </summary>
public sealed class MentorSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>How the buddy calls the user.</summary>
    public string UserName { get; set; } = "";

    /// <summary>The user's interests (music, films, chess...): topics of the off-topic chats that keep company.</summary>
    public List<string> Interests { get; set; } = [];

    /// <summary>Key of a built-in personality (see <see cref="Mentor.Personalities"/>) or "custom".</summary>
    public string Personality { get; set; } = "mentore";

    /// <summary>Description of the personality when <see cref="Personality"/> is "custom".</summary>
    public string CustomPersonality { get; set; } = "";

    /// <summary>Folder of the journal, profile and pills log (e.g. a "Personale/Buddy" folder of the vault).</summary>
    public string JournalPath { get; set; } = "";

    /// <summary>Working days, e.g. "Monday,Tuesday,Wednesday,Thursday,Friday".</summary>
    public string WorkDays { get; set; } = "Monday,Tuesday,Wednesday,Thursday,Friday";

    /// <summary>Morning check-in time (HH:mm); empty disables it. Proactive messages only come between check-in and check-out.</summary>
    public string CheckInTime { get; set; } = "09:00";

    /// <summary>End-of-day check-out time (HH:mm); empty disables it.</summary>
    public string CheckOutTime { get; set; } = "17:30";

    /// <summary>Suggest a break after this many minutes of continuous activity at the PC; 0 disables it.</summary>
    public int BreakAfterMinutes { get; set; } = 90;

    /// <summary>Default length of /focus.</summary>
    public int FocusMinutes { get; set; } = 50;

    /// <summary>Weekly retrospective day and time; empty time disables it.</summary>
    public string RetroDay { get; set; } = "Friday";
    public string RetroTime { get; set; } = "16:00";

    /// <summary>Knowledge pills, method tips and "explain it to me" questions per working day.</summary>
    public int PillsPerDay { get; set; } = 1;

    /// <summary>
    /// Spaced reviews per working day: a quick question on a pill given days ago (intervals 2, 7, 21, 60 days;
    /// a wrong answer brings it back after 2 days). Nothing is asked when no pill is due.
    /// </summary>
    public int ReviewsPerDay { get; set; } = 1;

    /// <summary>
    /// No mentor messages while you are in a call (microphone used by another app), in full screen or presenting,
    /// or with Windows "do not disturb" on: they wait until you are free.
    /// </summary>
    public bool QuietWhenBusy { get; set; } = true;

    /// <summary>
    /// Pills, reviews and friendly chats wait until the chat has been quiet this long (minutes): right after working
    /// with Claude or the local model you are still on the task. 0 = no wait. Check-in, check-out, retro and breaks
    /// are not held.
    /// </summary>
    public int QuietAfterChatMinutes { get; set; } = 20;

    /// <summary>
    /// Friendly questions per working day: growth (books, what you are learning), off-topic on your interests just
    /// for company, and questions to get to know you better.
    /// </summary>
    public int ChatsPerDay { get; set; } = 2;

    /// <summary>Before sending a task action to Claude, ask how you would approach it and have Claude review your idea.</summary>
    public bool AskApproachBeforeDelegating { get; set; } = true;

    /// <summary>
    /// Commands that delegate real work, for which the approach is asked (comma separated, e.g. "/lavora").
    /// Others, like /contesto or /analisi, go straight to Claude.
    /// </summary>
    public string ApproachCommands { get; set; } = "/lavora";

    /// <summary>True when <paramref name="action"/> (e.g. "/lavora NUSR-1 autonomo") starts with one of <see cref="ApproachCommands"/>.</summary>
    public bool AsksApproachFor(string action)
    {
        var command = action.Trim().Split(' ', 2)[0];
        return Enabled && AskApproachBeforeDelegating
            && ApproachCommands.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(c => command.Equals(c.StartsWith('/') ? c : "/" + c, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Topics the pills draw from (empty uses <see cref="DefaultTopics"/>); your profile adds more.
    /// No default list here: the configuration binder would append to it instead of replacing it.
    /// </summary>
    public List<string> Topics { get; set; } = [];

    public static IReadOnlyList<string> DefaultTopics { get; } =
    [
        "reti e protocolli", "database e SQL", "C# e .NET", "architettura del software", "sicurezza applicativa",
        "testing", "git", "frontend e Angular", "stime e pianificazione", "comunicazione nel team", "code review",
        "produttività e focus",
    ];

    /// <summary>Claude model for mentor messages (cheap and fast is fine).</summary>
    public string Model { get; set; } = "haiku";
}

/// <summary>
/// Everything that changes from one project to another: vault folder, repository, Claude's folders, task id
/// format, guideline docs, groups, commit/PR conventions and task buttons. Appearance and models stay global.
/// </summary>
public sealed class ProjectPreset
{
    public string Name { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public List<string> AlwaysIncludedNotes { get; set; } = [];
    public string TaskIdPattern { get; set; } = "";
    public string ClaudeWorkingDirectory { get; set; } = "";
    public List<string> ClaudeAdditionalDirectories { get; set; } = [];
    public RepoSettings Repo { get; set; } = new();
    public List<string> TaskActions { get; set; } = [];

    public static ProjectPreset From(BuddySettings settings, string name) => new()
    {
        Name = name,
        ProjectPath = settings.ProjectPath,
        AlwaysIncludedNotes = [.. settings.AlwaysIncludedNotes],
        TaskIdPattern = settings.TaskIdPattern,
        ClaudeWorkingDirectory = settings.Claude.WorkingDirectory,
        ClaudeAdditionalDirectories = [.. settings.Claude.AdditionalDirectories],
        Repo = new RepoSettings
        {
            Path = settings.Repo.Path,
            KnowledgePaths = [.. settings.Repo.KnowledgePaths],
            Groups = [.. settings.Repo.Groups],
            CommitInstructions = settings.Repo.CommitInstructions,
            PullRequestInstructions = settings.Repo.PullRequestInstructions,
            PullRequestTemplatePattern = settings.Repo.PullRequestTemplatePattern,
        },
        TaskActions = [.. settings.Assistant.TaskActions],
    };

    public void ApplyTo(BuddySettings settings)
    {
        settings.ProjectPath = ProjectPath;
        settings.AlwaysIncludedNotes = [.. AlwaysIncludedNotes];
        if (!string.IsNullOrWhiteSpace(TaskIdPattern))
        {
            settings.TaskIdPattern = TaskIdPattern;
        }

        settings.Claude.WorkingDirectory = ClaudeWorkingDirectory;
        settings.Claude.AdditionalDirectories = [.. ClaudeAdditionalDirectories];
        settings.Repo = new RepoSettings
        {
            Path = Repo.Path,
            KnowledgePaths = [.. Repo.KnowledgePaths],
            Groups = [.. Repo.Groups],
            CommitInstructions = Repo.CommitInstructions,
            PullRequestInstructions = Repo.PullRequestInstructions,
            PullRequestTemplatePattern = Repo.PullRequestTemplatePattern,
        };
        settings.Assistant.TaskActions = [.. TaskActions];
        settings.ActivePreset = Name;
    }

    /// <summary>Notes of the flow template included in the local model's context when they exist in the vault.</summary>
    public static readonly string[] FlowNotes = ["Stato attuale.md", "To-Do.md", "Da controllare.md"];

    /// <summary>
    /// A new project: its own vault and repository, the conventions (ID format, commit and PR instructions) taken from
    /// the current settings, and the flow notes the vault has as always-included notes.
    /// </summary>
    public static ProjectPreset New(string name, string vault, string repo, BuddySettings current) => new()
    {
        Name = name.Trim(),
        ProjectPath = vault,
        AlwaysIncludedNotes = FlowNotes.Where(n => File.Exists(System.IO.Path.Combine(vault, n))).ToList(),
        TaskIdPattern = current.TaskIdPattern,
        ClaudeWorkingDirectory = repo,
        Repo = new RepoSettings
        {
            Path = repo,
            CommitInstructions = current.Repo.CommitInstructions,
            PullRequestInstructions = current.Repo.PullRequestInstructions,
            PullRequestTemplatePattern = current.Repo.PullRequestTemplatePattern,
        },
    };
}

public static class ProjectSwitching
{
    /// <summary>
    /// Makes <paramref name="preset"/> the current project. The project being left is saved first into its own preset
    /// (created from the folder name when it had none), so switching back restores it as it was.
    /// </summary>
    public static void SwitchTo(this BuddySettings settings, ProjectPreset preset)
    {
        if (string.Equals(preset.Name, settings.ActivePreset, StringComparison.OrdinalIgnoreCase))
        {
            preset.ApplyTo(settings);
            return;
        }

        var leaving = settings.ActivePreset;
        if (string.IsNullOrWhiteSpace(leaving) && !string.IsNullOrWhiteSpace(settings.ProjectPath))
        {
            leaving = Path.GetFileName(settings.ProjectPath.TrimEnd('/', '\\'));
        }

        if (!string.IsNullOrWhiteSpace(leaving) && !string.Equals(leaving, preset.Name, StringComparison.OrdinalIgnoreCase))
        {
            var saved = ProjectPreset.From(settings, leaving);
            var index = settings.Presets.FindIndex(p => string.Equals(p.Name, leaving, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                settings.Presets[index] = saved;
            }
            else
            {
                settings.Presets.Add(saved);
            }
        }

        var existing = settings.Presets.FindIndex(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            settings.Presets[existing] = preset;
        }
        else
        {
            settings.Presets.Add(preset);
        }

        preset.ApplyTo(settings);
    }
}

/// <summary>The code repository the buddy watches (read-only: it never commits or changes files).</summary>
public sealed class RepoSettings
{
    /// <summary>Repository folder; empty uses <see cref="ClaudeSettings.WorkingDirectory"/>.</summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// Files or folders (relative to the repo) searched to answer questions about conventions, e.g.
    /// "docs/guidelines", "AGENTS.md". Only markdown files are read.
    /// </summary>
    public List<string> KnowledgePaths { get; set; } = [];

    /// <summary>
    /// How /modifiche groups changed files: "Name=prefix1;prefix2" (path prefixes relative to the repo, first match wins).
    /// Files matching no group are shown under "Altro".
    /// </summary>
    public List<string> Groups { get; set; } = [];

    /// <summary>Instructions sent to Claude with the diff for /commit; describe the team's convention here.</summary>
    public string CommitInstructions { get; set; } =
        "Scrivi un messaggio di commit per queste modifiche. Prima riga: [ID] descrizione breve in italiano. " +
        "Poi, se serve, un elenco puntato dei cambiamenti principali.";

    /// <summary>Instructions sent to Claude with the diff and the PR template for /pr.</summary>
    public string PullRequestInstructions { get; set; } =
        "Scrivi titolo e descrizione della pull request per queste modifiche, compilando il template. " +
        "Titolo: [ID] descrizione in italiano.";

    /// <summary>File-name pattern of the PR template, searched under .github.</summary>
    public string PullRequestTemplatePattern { get; set; } = "*pull_request_template*";
}

public sealed class AssistantSettings
{
    /// <summary>
    /// Buttons shown in the bubble when a task is known (from the question or the current branch); {id} is replaced
    /// with the task id. Each is sent to Claude, so skills like /lavora work.
    /// </summary>
    public List<string> TaskActions { get; set; } = [];

    /// <summary>
    /// Your request templates, "nome = testo" (e.g. "stack = Spiegami questo stack trace e la causa probabile: {testo}"):
    /// "/nome …" sends the text with {testo} replaced by what follows (or appended). Managed with /modelli.
    /// </summary>
    public List<string> Templates { get; set; } = [];

    /// <summary>Global shortcut that opens the bubble with the clipboard as attachment, e.g. "Ctrl+Alt+B". Empty disables it.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+B";

    /// <summary>A Windows notification is shown when a Claude answer took longer than this and the bubble is not in focus.</summary>
    public int NotifyAfterSeconds { get; set; } = 20;

    /// <summary>Largest attachment text (characters) sent with a question; longer text is truncated.</summary>
    public int MaxAttachmentChars { get; set; } = 20_000;
}

public sealed class OllamaSettings
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "llama3.2:3b";

    /// <summary>How long Ollama keeps the model in memory after a reply (Ollama duration syntax).</summary>
    public string KeepAlive { get; set; } = "30m";

    /// <summary>Personality and rules of the local model; empty uses the built-in prompt.</summary>
    public string SystemPrompt { get; set; } = "";
}

public sealed class ClaudeSettings
{
    /// <summary>Claude Code CLI executable: claude.cmd (npm) or claude.exe on Windows, claude elsewhere.</summary>
    public string Executable { get; set; } = OperatingSystem.IsWindows() ? "claude.cmd" : "claude";

    /// <summary>Folder Claude Code runs in, so its CLAUDE.md, skills and settings apply.</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>Extra folders Claude may access (passed as --add-dir). The project vault folder is added automatically.</summary>
    public List<string> AdditionalDirectories { get; set; } = [];

    /// <summary>Optional model alias or id; empty uses the CLI default.</summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// Picks the model per message: Haiku for short questions, Opus for the flow's task skills (/lavora, /analisi…),
    /// <see cref="Model"/> for the rest. The session is the same (resumed), only the model changes.
    /// </summary>
    public bool AutoModel { get; set; }

    /// <summary>Daily Claude spend (dollars) after which the buddy says so and offers a lighter model; 0 = no limit.</summary>
    public decimal DailyBudgetUsd { get; set; } = 5;

    /// <summary>Skills that get the strongest model when <see cref="AutoModel"/> is on; empty = /lavora, /analisi, /analisi-spike.</summary>
    public List<string> HeavyCommands { get; set; } = [];

    internal static readonly string[] DefaultHeavyCommands = ["/lavora", "/analisi", "/analisi-spike"];

    /// <summary>Files that must never be committed (e.g. local config), relative to the repo; /commit warns about them.</summary>
    public List<string> DoNotCommit { get; set; } = [];

    /// <summary>
    /// Tools Claude may use without asking (passed as --allowedTools), e.g. "Read", "Grep", "Bash(git status:*)".
    /// Everything else still goes through the permission prompt in the bubble.
    /// </summary>
    public List<string> AutoAllowTools { get; set; } = [];

    /// <summary>
    /// Claude Code's permission mode (--permission-mode): "default" asks for every tool not in
    /// <see cref="AutoAllowTools"/>; "acceptEdits" also lets it edit files without asking (commands still ask);
    /// "auto" lets Claude Code's classifier decide, asking only for risky actions; "bypassPermissions" never asks.
    /// </summary>
    public string PermissionMode { get; set; } = "default";
}

public sealed class AppearanceSettings
{
    /// <summary>Name shown in the bubble header.</summary>
    public string Name { get; set; } = "Buddy";

    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 13;

    /// <summary>Zoom of the chat's messages (Ctrl+ / Ctrl- / Ctrl+0 in the bubble), 1 = as designed.</summary>
    public double ChatZoom { get; set; } = 1;

    public ThemeColors Theme { get; set; } = ThemeColors.Light();
}

/// <summary>Bubble colours as #RRGGBB strings.</summary>
public sealed class ThemeColors
{
    public string Background { get; set; } = "";
    public string Border { get; set; } = "";
    public string Text { get; set; } = "";
    public string MutedText { get; set; } = "";
    public string UserBubble { get; set; } = "";
    public string LocalBubble { get; set; } = "";
    public string LocalAccent { get; set; } = "";
    public string ClaudeBubble { get; set; } = "";
    public string ClaudeAccent { get; set; } = "";
    public string Input { get; set; } = "";
    public string MentorBubble { get; set; } = "";
    public string MentorAccent { get; set; } = "";

    public static ThemeColors Light() => new()
    {
        Background = "#FFFDF7",
        Border = "#241F36",
        Text = "#241F36",
        MutedText = "#6B6680",
        UserBubble = "#E9E6F2",
        LocalBubble = "#E3F6EC",
        LocalAccent = "#7FD1AE",
        ClaudeBubble = "#EFE8FF",
        ClaudeAccent = "#B99BF5",
        Input = "#FFFFFF",
        MentorBubble = "#FFF4DC",
        MentorAccent = "#F2B544",
    };

    public static ThemeColors Dark() => new()
    {
        Background = "#1F1C2C",
        Border = "#8A84A3",
        Text = "#ECE9F5",
        MutedText = "#A29DB8",
        UserBubble = "#3A3550",
        LocalBubble = "#1F3B31",
        LocalAccent = "#7FD1AE",
        ClaudeBubble = "#352B52",
        ClaudeAccent = "#B99BF5",
        Input = "#2A2639",
        MentorBubble = "#3D3320",
        MentorAccent = "#F2B544",
    };
}

public sealed class PetSettings
{
    /// <summary>Size of the character on screen, in pixels.</summary>
    public double DisplaySize { get; set; } = 96;

    /// <summary>Milliseconds per animation frame.</summary>
    public int FrameMilliseconds { get; set; } = 220;

    public bool AlwaysOnTop { get; set; } = true;

    public bool StartWithWindows { get; set; }

    /// <summary>Reopen where it was left instead of the bottom-right corner.</summary>
    public bool RememberPosition { get; set; } = true;

    /// <summary>Now and then the pet strolls left or right along the screen (only when idle and the chat is closed).</summary>
    public bool Walk { get; set; } = true;

    /// <summary>Walking speed in pixels per second.</summary>
    public double WalkSpeed { get; set; } = 40;

    /// <summary>Average pause between two walks, in seconds.</summary>
    public int WalkPauseSeconds { get; set; } = 20;

    /// <summary>Body colours of the built-in character, per chat mode.</summary>
    public string LocalColor { get; set; } = "#7FD1AE";
    public string ClaudeColor { get; set; } = "#B99BF5";

    /// <summary>
    /// Optional folder with custom pixel art: one PNG per state (idle, think, work, alert, error), each a horizontal
    /// strip of square frames; "claude-&lt;state&gt;.png" overrides it in Claude mode. Missing files use the built-in art.
    /// </summary>
    public string SpriteFolder { get; set; } = "";
}

public sealed class PhraseSettings
{
    /// <summary>Shown once when the app starts; empty for none.</summary>
    public string Greeting { get; set; } = "Ciao! Chiedimi qualcosa sul progetto, o scrivi /c per passare a Claude.";
    public string SwitchedToClaude { get; set; } = "Ora parli con Claude.";
    public string SwitchedToLocal { get; set; } = "Ora parli con il modello locale.";
    public string NewConversation { get; set; } = "Nuova conversazione.";
    public string ClaudeBusy { get; set; } = "Claude sta ancora rispondendo: aspetta o scrivi /stop.";
}
