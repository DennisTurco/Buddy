using System.Text.RegularExpressions;
using Buddy.Core.Claude;

namespace Buddy.Core.Vault;

/// <summary>How a template file differs from what the project has.</summary>
public enum TemplateChange
{
    /// <summary>Not in the project yet (a note or a skill added to the template).</summary>
    Added,

    /// <summary>Buddy's copy of an older template, untouched since: replaced without asking.</summary>
    Updated,

    /// <summary>Different from what Buddy wrote: edited by hand. Replaced only on request, with a backup.</summary>
    Edited,

    /// <summary>Different, and Buddy has no record of writing it (a vault from before the updates): also on request.</summary>
    Differs,
}

/// <param name="Path">The file in the template ("Skill/Skill lavora.md"); for a skill, its installed SKILL.md.</param>
/// <param name="Target">Where it goes.</param>
/// <param name="Content">What it becomes.</param>
/// <param name="Skill">The skill's name when the target is an installed skill (~/.claude/skills).</param>
public sealed record TemplateFile(string Path, string Target, TemplateChange Change, string Content, string? Skill = null)
{
    public bool NeedsConsent => Change is TemplateChange.Edited or TemplateChange.Differs;
}

/// <summary>
/// What updating a project to the template built into Buddy (or the personal one) would do. Shared notes and installed
/// skills are replaced; the project's own notes hold its content, so only the missing ones are created and the rest is
/// left to Claude, guided by the changelog entries newer than the project's <c>versione_template</c>.
/// </summary>
public sealed record TemplateUpdate(
    string ProjectFolder,
    string SharedFolder,
    string? ProjectVersion,
    string? TemplateVersion,
    IReadOnlyList<TemplateFile> Files,
    string NewChangelog,
    IReadOnlyList<(string Path, string Content)> ProjectTemplates)
{
    /// <summary>The project applied an older template version than the one available.</summary>
    public bool VersionBehind => ParseVersion(ProjectVersion) is { } project && ParseVersion(TemplateVersion) is { } template && project < template;

    public bool HasUpdates => Files.Count > 0 || VersionBehind;

    /// <summary>Claude can bring the project's notes to the new version: there are changelog entries to apply.</summary>
    public bool CanAskClaude => VersionBehind && NewChangelog.Length > 0;

    internal static Version? ParseVersion(string? text) =>
        Version.TryParse(text?.Trim(), out var v) ? v : int.TryParse(text?.Trim(), out var major) ? new Version(major, 0) : null;
}

/// <summary>What <see cref="FlowTemplate.ApplyUpdate"/> did.</summary>
/// <param name="Backup">The folder with the previous versions of the files replaced on request (null: none).</param>
public sealed record TemplateUpdateResult(IReadOnlyList<TemplateFile> Written, IReadOnlyList<TemplateFile> Skipped, string? Backup);

public static partial class FlowTemplate
{
    public const string BackupFolder = ".buddy-backup";

    /// <summary>
    /// Compares the project with the template: missing project notes, shared notes (Skill, Modelli, Configurazione…)
    /// and the skills installed in ~/.claude/skills. Changes nothing.
    /// </summary>
    public static TemplateUpdate CheckUpdate(string projectFolder, string? customTemplate = null, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var files = Load(customTemplate);
        var layout = LayoutOf(projectFolder, customTemplate);
        var manifest = TemplateManifest.Open(layout.Shared);
        var skillsFolder = ClaudeSkills.UserSkillsFolder(home);
        var skillManifest = TemplateManifest.Open(skillsFolder);
        var changes = new List<TemplateFile>();

        foreach (var (path, content) in files)
        {
            var isProjectNote = layout.IsProjectNote(path);
            var target = layout.TargetOf(path);
            var text = layout.Rewrite(content, isProjectNote);
            string? previous = File.Exists(target) ? File.ReadAllText(target) : null;

            if (isProjectNote)
            {
                // The project's notes hold its content: only a missing one is created.
                if (previous is null)
                {
                    if (path == "Scheda progetto.md")
                    {
                        text = FillScheda(text, Path.GetFileName(layout.Project), layout.Shared, layout.Project);
                    }

                    changes.Add(new TemplateFile(path, target, TemplateChange.Added, text));
                }
            }
            else if (layout.CopyShared)
            {
                if (Compare(previous, text, manifest, path) is { } change)
                {
                    changes.Add(new TemplateFile(path, target, change, text));
                }
            }

            // The skill Claude runs is the installed copy: it follows the template too (the user's own skills,
            // installed in no way by Buddy, are compared like the rest and replaced only on request).
            if (ClaudeSkills.SkillName(path, content) is { } skill)
            {
                var installed = Path.Combine(skillsFolder, skill, "SKILL.md");
                if (File.Exists(installed))
                {
                    var current = File.ReadAllText(installed);
                    var change = Compare(current, content, skillManifest, skill);
                    // Same as the vault's (old) copy: both come from an older template.
                    if (change is TemplateChange.Differs && previous is not null && TemplateManifest.Same(current, previous))
                    {
                        change = TemplateChange.Updated;
                    }

                    if (change is not null)
                    {
                        changes.Add(new TemplateFile(path, installed, change.Value, content, skill));
                    }
                }
            }
        }

        var schedaPath = Path.Combine(layout.Project, "Scheda progetto.md");
        var projectVersion = File.Exists(schedaPath) ? TemplateVersionOf(File.ReadAllText(schedaPath)) : null;
        var changelog = files.FirstOrDefault(f => f.Path == "Changelog template.md").Content ?? "";
        var templateVersion = TemplateVersionOf(changelog);
        var projectTemplates = files.Where(f => layout.IsProjectNote(f.Path))
            .Select(f => (f.Path, layout.Rewrite(f.Content, inProject: true)))
            .ToList();

        return new TemplateUpdate(layout.Project, layout.Shared, projectVersion, templateVersion, changes,
            ChangelogAfter(changelog, projectVersion), projectTemplates);
    }

    /// <summary>
    /// Writes the update: added and updated files always, the ones edited by hand (or of unknown origin) only with
    /// <paramref name="replaceEdited"/>, after copying them in <see cref="BackupFolder"/> inside the shared folder.
    /// </summary>
    public static TemplateUpdateResult ApplyUpdate(TemplateUpdate update, bool replaceEdited, DateTime now, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var manifest = TemplateManifest.Open(update.SharedFolder);
        var skillManifest = TemplateManifest.Open(ClaudeSkills.UserSkillsFolder(home));
        var backup = Path.Combine(update.SharedFolder, BackupFolder, now.ToString("yyyy-MM-dd HHmmss"));
        var backedUp = false;
        var written = new List<TemplateFile>();
        var skipped = new List<TemplateFile>();

        foreach (var file in update.Files)
        {
            if (file.NeedsConsent && !replaceEdited)
            {
                skipped.Add(file);
                continue;
            }

            if (file.NeedsConsent && File.Exists(file.Target))
            {
                var copy = file.Skill is { } name
                    ? Path.Combine(backup, "skill", name, "SKILL.md")
                    : Path.Combine(backup, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file.Target, copy, overwrite: true);
                backedUp = true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file.Target)!);
            File.WriteAllText(file.Target, file.Content);
            written.Add(file);
            if (file.Skill is { } skill)
            {
                skillManifest.Record(skill, file.Content);
            }
            else if (!ProjectNotes.Contains(file.Path, StringComparer.Ordinal))
            {
                manifest.Record(file.Path, file.Content);
            }
        }

        manifest.Save();
        skillManifest.Save();
        return new TemplateUpdateResult(written, skipped, backedUp ? backup : null);
    }

    /// <summary>
    /// What Claude is asked (claude -p, working directory = the project's notes) to bring the project's own notes to the
    /// new template version without touching their content. <paramref name="templateNotes"/> holds the template's
    /// version of those notes (read-only).
    /// </summary>
    public static string UpdatePrompt(TemplateUpdate update, string templateNotes) => $"""
        Sei nella cartella delle note di un progetto (vault Obsidian, flusso di lavoro con Claude). Il template del
        flusso è passato dalla versione {update.ProjectVersion} alla {update.TemplateVersion}.

        Porta le note di questa cartella alla nuova versione applicando SOLO le voci del changelog qui sotto che
        toccano le note del progetto (formato delle voci, sezioni, proprietà, righe di esempio, query del Cruscotto,
        link dell'indice). La versione aggiornata di ogni nota del template è in: {templateNotes} (solo lettura):
        usala come riferimento per la struttura.

        Regole:
        - Mai cambiare il contenuto: task, appunti, idee, decisioni, glossario, persone, dati compilati della Scheda
          progetto. Se una modifica richiede di spostare o riformattare voci esistenti, fallo senza perdere niente.
        - Le note che mancavano, le skill e la parte condivisa del template (Skill, Modelli, Configurazione, LEGGIMI,
          Changelog) li ha già aggiornati Buddy: non toccarli.
        - Non cambiare `versione_template` nella Scheda progetto: lo fa Buddy.
        - Se una voce riguarda solo skill o note condivise, saltala.

        Scrivi in italiano. Alla fine rispondi con una sola riga che dice quali note hai cambiato (o che non serviva
        cambiare niente).

        Voci del changelog da applicare:
        {update.NewChangelog}
        """;

    /// <summary>Writes the project's new template version in its Scheda progetto (adds the property when missing).</summary>
    public static void SetProjectVersion(string projectFolder, string version)
    {
        var scheda = Path.Combine(projectFolder, "Scheda progetto.md");
        if (!File.Exists(scheda))
        {
            return;
        }

        var text = File.ReadAllText(scheda);
        var updated = WithTemplateVersion(text, version);
        if (updated != text)
        {
            File.WriteAllText(scheda, updated);
        }
    }

    /// <summary>The note with its <c>versione_template</c> property set (added to the frontmatter when missing).</summary>
    internal static string WithTemplateVersion(string text, string version)
    {
        var line = $"versione_template: \"{version}\"";
        if (VersionProperty().IsMatch(text))
        {
            return VersionProperty().Replace(text, line, 1);
        }

        if (Frontmatter().Match(text) is { Success: true } fm)
        {
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            return text.Insert(fm.Groups["body"].Index + fm.Groups["body"].Length, newline + line);
        }

        return text;
    }

    /// <summary>The <c>versione_template</c> property of a note's frontmatter.</summary>
    internal static string? TemplateVersionOf(string note) =>
        Frontmatter().Match(note) is { Success: true } fm && VersionProperty().Match(fm.Value) is { Success: true } m
            ? m.Groups["v"].Value.Trim()
            : null;

    /// <summary>The changelog's "## X.Y · date" sections newer than <paramref name="since"/>, oldest first.</summary>
    internal static string ChangelogAfter(string changelog, string? since)
    {
        if (TemplateUpdate.ParseVersion(since) is not { } from)
        {
            return "";
        }

        var sections = ChangelogSection().Matches(changelog)
            .Select(m => (Version: TemplateUpdate.ParseVersion(m.Groups["v"].Value), Text: m.Value.Trim()))
            .Where(s => s.Version is { } v && v > from)
            .OrderBy(s => s.Version)
            .Select(s => s.Text);
        return string.Join("\n\n", sections);
    }

    private static TemplateChange? Compare(string? current, string wanted, TemplateManifest manifest, string key) =>
        current is null ? TemplateChange.Added
        : TemplateManifest.Same(current, wanted) ? null
        : manifest.Matches(key, current) ? TemplateChange.Updated
        : manifest.Knows(key) ? TemplateChange.Edited
        : TemplateChange.Differs;

    [GeneratedRegex(@"\A---\r?\n(?<body>(?:.*\r?\n)*?.*?)\r?\n---", RegexOptions.None)]
    private static partial Regex Frontmatter();

    [GeneratedRegex(@"^versione_template:[ \t]*""?(?<v>[^""\r\n]*)""?[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex VersionProperty();

    [GeneratedRegex(@"^## (?<v>\d+(?:\.\d+)*) ·.*?(?=^## |\z)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex ChangelogSection();
}
