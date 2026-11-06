using System.Text.RegularExpressions;

namespace Buddy.Core.Vault;

/// <summary>
/// Creates a project's vault from the flow template (To-Do, Da controllare, Scheda progetto…), for a user who has
/// no Obsidian vault yet. The template is the one built into Buddy, or a personal copy (<see cref="BuddySettings.FlowTemplatePath"/>).
/// Layout, as the template's LEGGIMI describes it:
/// <code>
/// &lt;vault&gt;/Intelligenza Artificiale/Template flusso/   shared: skills, models, configuration
/// &lt;vault&gt;/Progetti/&lt;Nome&gt;/                           the project's notes
/// </code>
/// Existing notes are never overwritten, so it is safe on a folder that already has some of them.
/// </summary>
public static partial class FlowTemplate
{
    /// <summary>Where the template's links point, relative to the vault root.</summary>
    public const string TemplateFolder = "Intelligenza Artificiale/Template flusso";

    /// <summary>The notes that belong to each project; the rest of the template stays shared.</summary>
    public static readonly string[] ProjectNotes =
    [
        "Scheda progetto.md", "00 Indice.md", "Stato attuale.md", "Cruscotto.md", "To-do Appunti.md", "Da valutare.md",
        "To-Do.md", "Da controllare.md", "To-Do Fatti.md", "To-Do Rifiutati.md", "Decisioni.md", "Glossario.md",
        "Test web.md", "Mappa codice.md",
    ];

    /// <summary>The vault Buddy proposes when the user has none: one folder for every project.</summary>
    public static string DefaultVaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) is { Length: > 0 } documents
            ? documents
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Buddy Vault");

    /// <summary>The proposed folder for a new project's notes.</summary>
    public static string DefaultProjectFolder(string name) => Path.Combine(DefaultVaultRoot, "Progetti", SafeName(name));

    /// <summary>True when the folder already has the notes of the flow (nothing to create).</summary>
    public static bool HasFlow(string projectFolder) =>
        ProjectPreset.FlowNotes.Any(n => File.Exists(Path.Combine(projectFolder, n)));

    /// <summary>
    /// The vault root of a project folder: the nearest folder with Obsidian's ".obsidian", otherwise the folder that
    /// holds "Progetti", otherwise the project folder's parent.
    /// </summary>
    public static string VaultRootOf(string projectFolder)
    {
        var full = Path.GetFullPath(projectFolder);
        for (var dir = new DirectoryInfo(full); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".obsidian")))
            {
                return dir.FullName;
            }
        }

        var parent = Directory.GetParent(full);
        return parent is { Name: "Progetti", Parent: { } root } ? root.FullName : parent?.FullName ?? full;
    }

    /// <summary>The template's files (path relative to the template folder, with '/', and content).</summary>
    public static IReadOnlyList<(string Path, string Content)> Load(string? customFolder = null)
    {
        if (!string.IsNullOrWhiteSpace(customFolder) && Directory.Exists(customFolder))
        {
            return Directory.EnumerateFiles(customFolder, "*.md", SearchOption.AllDirectories)
                .Select(f => (Path.GetRelativePath(customFolder, f).Replace('\\', '/'), File.ReadAllText(f)))
                .ToList();
        }

        var assembly = typeof(FlowTemplate).Assembly;
        const string prefix = "FlowTemplate/";
        return assembly.GetManifestResourceNames()
            .Select(n => (Name: n, Path: n.Replace('\\', '/')))
            .Where(r => r.Path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(r =>
            {
                using var stream = assembly.GetManifestResourceStream(r.Name)!;
                using var reader = new StreamReader(stream);
                return (r.Path[prefix.Length..], reader.ReadToEnd());
            })
            .ToList();
    }

    /// <summary>
    /// Creates the project's notes in <paramref name="projectFolder"/> and, when the vault does not have it yet, the
    /// shared part of the template. Links to the project's notes point to the project; the Scheda progetto gets the
    /// name and the paths. Returns the files created.
    /// </summary>
    public static IReadOnlyList<string> CreateProject(string projectFolder, string name, string? customTemplate = null)
    {
        var files = Load(customTemplate);
        if (files.Count == 0)
        {
            throw new InvalidOperationException("Template del flusso vuoto o non trovato.");
        }

        var projectFull = Path.GetFullPath(projectFolder);
        var root = VaultRootOf(projectFull);
        var newVault = !Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any();
        var created = new List<string>();

        Directory.CreateDirectory(projectFull);
        if (newVault)
        {
            // Obsidian opens it as a vault, and VaultRootOf finds it again.
            Directory.CreateDirectory(Path.Combine(root, ".obsidian"));
        }

        // The shared part: the personal template when it is inside this vault, otherwise a copy in the vault.
        var customFull = !string.IsNullOrWhiteSpace(customTemplate) && Directory.Exists(customTemplate) ? Path.GetFullPath(customTemplate) : null;
        var sharedFull = customFull is not null && IsInside(customFull, root)
            ? customFull
            : Path.Combine(root, TemplateFolder.Replace('/', Path.DirectorySeparatorChar));
        var copyShared = sharedFull != customFull;
        var sharedLink = VaultLink(root, sharedFull);
        var projectLink = VaultLink(root, projectFull);
        var projectNotes = ProjectNotes.Select(n => n[..^3]).ToHashSet(StringComparer.Ordinal);

        string Rewrite(string content, bool inProject) => TemplateLink().Replace(content, m =>
        {
            var note = m.Groups["note"].Value;
            var target = inProject && projectNotes.Contains(note) ? projectLink : sharedLink;
            return target + "/" + note;
        });

        foreach (var (path, content) in files)
        {
            var isProjectNote = ProjectNotes.Contains(path, StringComparer.Ordinal);
            if (!isProjectNote && !copyShared)
            {
                continue; // the personal template already is the shared copy
            }

            var target = isProjectNote
                ? Path.Combine(projectFull, path)
                : Path.Combine(sharedFull, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target))
            {
                continue;
            }

            var text = Rewrite(content, isProjectNote);
            if (path == "Scheda progetto.md")
            {
                text = FillScheda(text, name, sharedFull, projectFull);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, text);
            created.Add(target);
        }

        return created;
    }

    /// <summary>
    /// What Claude is asked (claude -p, working directory = the project's notes, repository readable) to fill in the
    /// Scheda progetto and Stato attuale from the code.
    /// </summary>
    public static string FillPrompt(string name, string repoFolder, string recentCommits) => $"""
        Sei nella cartella delle note del progetto «{name}» (vault Obsidian, flusso di lavoro con Claude).
        Il codice del progetto è in: {repoFolder} (solo lettura).

        Compila due note di questa cartella leggendo il repository (README, file di istruzioni come CLAUDE.md o
        AGENTS.md, file di progetto, cartella docs). Non toccare altri file. Gli ultimi commit li trovi qui sotto: non
        servono comandi git.

        1. «Scheda progetto.md»: riempi solo quello che si ricava dal repository (nome, descrizione in una riga,
           stack, piattaforme come GitHub e CI, comandi principali se il repo non li descrive già). Lascia invariata la
           sezione «Template base». Dove un dato non si ricava dal codice scrivi *(da completare)*. Non ripetere quello
           che le istruzioni del repo dicono già: rimanda a loro. Aggiorna la riga introduttiva dicendo che la scheda è
           ricavata dal repo. Aggiorna la proprietà updated con la data di oggi.
        2. «Stato attuale.md»: segui il formato già presente nella nota; scrivi in poche righe a che punto è il
           progetto secondo il repository (ultimi commit, parti presenti), niente conteggi.

        Scrivi in italiano, frasi brevi. Mai password, chiavi o token. Alla fine rispondi con una sola riga che dice
        cosa hai compilato.

        Ultimi commit (data e messaggio):
        {(recentCommits.Length > 0 ? recentCommits : "(nessun commit o non è un repository git)")}
        """;

    /// <summary>The name and the two paths of the "Template base" section, the only parts known without the repo.</summary>
    internal static string FillScheda(string text, string name, string templateFolder, string projectFolder)
    {
        text = text.Replace("- **Percorso del template base:** `…`", $"- **Percorso del template base:** `{templateFolder}`");
        text = Regex.Replace(text, @"^- \*\*Cartella del progetto nel vault:\*\* `…`.*$", $"- **Cartella del progetto nel vault:** `{projectFolder}`", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^- \*\*Nome:\*\* *$", $"- **Nome:** {name}", RegexOptions.Multiline);
        return text;
    }

    private static string VaultLink(string root, string folder) =>
        Path.GetRelativePath(root, folder).Replace('\\', '/');

    private static bool IsInside(string folder, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(folder));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static string SafeName(string name) =>
        string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '-' : c));

    /// <summary>"Intelligenza Artificiale/Template flusso/Nota" inside a wikilink or a path.</summary>
    [GeneratedRegex(@"Intelligenza Artificiale/Template flusso/(?<note>[^\]|#\n`]+?)(?=\]\]|\||#|`|\.md|$)", RegexOptions.Multiline)]
    private static partial Regex TemplateLink();
}
