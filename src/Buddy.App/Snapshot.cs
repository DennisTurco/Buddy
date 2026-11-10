using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Buddy.App.Chat;
using Buddy.App.Pet;
using Buddy.Core;
using Buddy.Core.Chat;
using Buddy.Core.Claude;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Buddy.App;

/// <summary>
/// Development aid: `Buddy.App --snapshot &lt;folder&gt;` renders the sprites, a sample chat and every tab of the
/// settings window to PNG files, so the UI can be checked without looking at the desktop. The windows appear
/// on screen for a moment while they are captured.
/// </summary>
internal static class Snapshot
{
    public static async Task RenderAsync(Conversation conversation, PetWindow pet, BubbleWindow bubble, string folder, IServiceProvider services)
    {
        Directory.CreateDirectory(folder);
        conversation.Archive = null; // the sample chat must not end up among the user's saved conversations
        await RenderSpritesAsync(pet.Sprites, Path.Combine(folder, "sprites.png"));

        conversation.Greet();
        await conversation.RefreshCurrentTaskAsync();
        await conversation.SendAsync("/stato");
        File.WriteAllText(Path.Combine(folder, "instant.md"), string.Join("\n\n-----\n\n", conversation.Entries.Select(e => $"[{e.Speaker}] {e.Text}")));
        conversation.Entries.Clear();
        conversation.PendingAttachment = Attachment.Combine(
            Attachment.FromText("System.NullReferenceException: Object reference not set to an instance of an object.\n   at LoginFallito.Registra()", "appunti"),
            Attachment.FromImage(pet.Sprites.IconPng(96), "immagine dagli appunti"));
        conversation.Entries.Add(new ChatEntry(Speaker.Mentor, "**TCP o UDP?** TCP garantisce ordine e consegna (handshake, ritrasmissioni); UDP spedisce e basta, più veloce. Nel tuo lavoro, dove sceglieresti UDP?"));
        conversation.Entries.Add(new ChatEntry(Speaker.User, "a che punto è NUSR-2367?"));
        conversation.Entries.Add(new ChatEntry(Speaker.Local, "È in **Da controllare**: mancano i test di\n- riattivazione NUSR-2370\n- report *Account bloccati*\n\nFonti: [To-Do.md (NUSR-2367)](obsidian://open?path=x) · [Stato attuale.md](obsidian://open?path=y)")
        {
            Question = "a che punto è NUSR-2367?",
            CanEscalate = true,
        });
        conversation.Entries.Add(new ChatEntry(Speaker.System, "Ora parli con Claude."));
        var screenshot = Attachment.FromImage(pet.Sprites.IconPng(128), "immagine dagli appunti");
        conversation.Entries.Add(new ChatEntry(Speaker.User, "perché il pulsante è verde?\n📎 1 immagine") { Images = screenshot.Images });
        conversation.Entries.Add(new ChatEntry(Speaker.User, "/lavora NUSR-2367"));
        conversation.Entries.Add(new ChatEntry(Speaker.Claude, "Leggo la voce e il codice. Piano:\n\n1. aggiorno `LoginFallito.vb`\n2. rigenero lo script\n\n| Task | Dove | Stato |\n|---|---|---|\n| T-010 | `To-Do` | da fare |\n| T-011 | `Da valutare` | idea |\n\n```sql\nselect * from utenti where bloccato = 1; -- da sbloccare\n```\n\n```csharp\npublic async Task<bool> ResetAsync(int id)\n{\n    // azzera i tentativi\n    return await _db.ExecuteAsync(\"update Utenti set Tentativi = 0\", id) > 0;\n}\n```\n\n> Nota: vedi [Stato attuale](obsidian://open?path=x)"));
        using var input = JsonDocument.Parse("""{"file_path":"/home/me/portale20/Common/Business/Accounts/Data/LoginFallito.vb"}""");
        conversation.Entries.Add(new PermissionEntry(new PermissionRequest("r1", "Edit", input.RootElement.Clone())));
        using var question = JsonDocument.Parse("""
            {"questions":[{"question":"T-004 è in Da valutare da 22 giorni. Cosa faccio?","header":"T-004","multiSelect":false,
              "options":[{"label":"Rimanda","description":"Nuova data tra 30 giorni"},{"label":"Approva","description":"Va in To-Do con Fatto quando e stima"},
                         {"label":"Rifiuta","description":"Va in To-Do Rifiutati con il motivo"},{"label":"Salta","description":"Non cambia niente"}]}]}
            """);
        var ask = new PermissionEntry(new PermissionRequest("r2", "AskUserQuestion", question.RootElement.Clone()));
        ask.Questions[0].Options[1].IsSelected = true;
        conversation.Entries.Add(ask);
        conversation.Entries.Add(new ChatEntry(Speaker.Mentor, "Quale piccola funzione del tuo lavoro potresti provare con il TDD la prossima volta?")
        {
            Actions = [new ChatAction("Il calcolo dello sconto", "x"), new ChatAction("Non saprei da dove partire", "x"), new ChatAction("Fammi un esempio", "x")],
        });

        pet.Show();
        bubble.Anchor = pet;
        bubble.ShowAndFocus();
        await FlushAsync();

        Save(bubble, Path.Combine(folder, "bubble.png"));

        // Text selected across `inline code`: the highlight must show on the code too, not stop at it.
        var markdown = new MarkdownView { Markdown = "Da committare: i 3 script nuovi (`Migrations/0011`, `Migrations/0012`, `DataMigrations/0010`) più `schema.lock` e `R__00_Core_4_UpdateValoriDefault.sql`." };
        var selection = new Window { Width = 620, Height = 90, Content = new Border { Padding = new Thickness(12), Child = markdown } };
        Appearance.Apply(selection, services.GetRequiredService<IOptions<BuddySettings>>().Value.Appearance, font: false);
        selection.Show();
        await FlushAsync();
        foreach (var block in selection.GetVisualDescendants().OfType<ColorTextBlock.Avalonia.CTextBlock>())
        {
            block.Select(0, block.Text.Length);
        }

        await FlushAsync();
        Save(selection, Path.Combine(folder, "markdown-selection.png"));
        selection.Close();

        // The chat in each built-in theme (only the snapshot's copy of the settings changes).
        var looks = new AppearanceSettings();
        foreach (var (name, create) in ThemeColors.BuiltIn)
        {
            looks.Theme = create();
            Appearance.Apply(bubble, looks, font: false);
            Appearance.ApplyControls(bubble, looks);
            bubble.RequestedThemeVariant = Appearance.Variant(looks);
            await FlushAsync();
            Save(bubble, Path.Combine(folder, $"theme-{name}.png"));
        }

        var current = services.GetRequiredService<IOptions<BuddySettings>>().Value.Appearance;
        Appearance.Apply(bubble, current, font: false);
        Appearance.ApplyControls(bubble, current);
        bubble.RequestedThemeVariant = Appearance.Variant(current);

        // The code blocks of Claude's answer (highlighted, with the copy button), scrolled into view.
        if (bubble.FindControl<ScrollViewer>("Scroller") is { } scroller
            && bubble.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("CodeBlock")) is { } code
            && code.TranslatePoint(new Point(0, 0), scroller.Content as Visual ?? scroller) is { } top)
        {
            scroller.Offset = new Vector(0, Math.Max(0, top.Y - 40));
            await FlushAsync();
            Save(bubble, Path.Combine(folder, "bubble-code.png"));
        }
        Save(pet, Path.Combine(folder, "pet.png"));

        // Local answer with its sources and the "Chiedi a Claude" button.
        var local = conversation.Entries.Where(e => e.Speaker == Speaker.Local || e.Text.StartsWith("a che punto")).ToList();
        conversation.PendingAttachment = null;
        conversation.Entries.Clear();
        local.ForEach(conversation.Entries.Add);
        await FlushAsync();
        Save(bubble, Path.Combine(folder, "bubble-local.png"));

        // Maximized: fills the screen of the pet.
        conversation.Entries.Add(new ChatEntry(Speaker.Claude, string.Join("\n\n", Enumerable.Range(1, 4).Select(i =>
            $"**Punto {i}.** Una risposta lunga di Claude con molte informazioni, che nella nuvoletta piccola va a capo ogni poche parole e diventa difficile da leggere; a schermo intero le righe si allargano fino a una lunghezza comoda."))));
        bubble.SetMaximized(true);
        await FlushAsync();
        Save(bubble, Path.Combine(folder, "bubble-max.png"));
        bubble.SetMaximized(false);
        bubble.Hide();

        var settings = new Settings.SettingsWindow(
            services.GetRequiredService<IOptions<BuddySettings>>().Value,
            services.GetRequiredService<Buddy.Core.Llm.OllamaClient>());
        settings.Show();
        await FlushAsync();
        var tabs = settings.GetVisualDescendants().OfType<TabControl>().First();
        for (var i = 0; i < tabs.ItemCount; i++)
        {
            tabs.SelectedIndex = i;
            await FlushAsync();
            Save(settings, Path.Combine(folder, $"settings-{i}.png"));
        }

        settings.Close();

        var wizard = new Settings.FirstRunWindow(services.GetRequiredService<IOptions<BuddySettings>>().Value);
        wizard.Show();
        await FlushAsync();
        Save(wizard, Path.Combine(folder, "first-run.png"));
        wizard.Close();

        var newProject = new Settings.NewProjectWindow(new BuddySettings());
        newProject.Show();
        await FlushAsync();
        Save(newProject, Path.Combine(folder, "new-project.png"));
        newProject.Close();

        var jira = new Project.TrackerImportWindow("Jira",
        [
            new("ABC-12", "Export CSV dei voti vuoto con più corsi", "", "High", "Bug", "To Do", "Persona A", new DateOnly(2026, 11, 20), ""),
            new("ABC-15", "Filtro per anno accademico nella lista studenti", "", "Medium", "Story", "In Progress", "Persona B", null, ""),
        ], 3);
        jira.Show();
        await FlushAsync();
        Save(jira, Path.Combine(folder, "jira-import.png"));
        jira.Close();

        var issue = new Project.IssueFormWindow("DennisTurco/Buddy", new Buddy.Core.Tracker.IssueDraft("Export CSV dei voti vuoto",
            "- **Contesto**: il file scaricato non ha righe\n- **Fatto quando**: il CSV contiene tutti i voti\n\n_Creata da Buddy dalla voce T-014._", ["bug"]));
        issue.Show();
        await FlushAsync();
        Save(issue, Path.Combine(folder, "issue-form.png"));
        issue.Close();

        foreach (var kind in new[] { "appunti", "todo", "attesa", "controllare" })
        {
            var form = new Project.EntryFormWindow(kind, "T-014", Project.Tags.Defaults.Select(d => d.Name));
            form.Show();
            await FlushAsync();
            Save(form, Path.Combine(folder, $"entry-{kind}.png"));
            form.Close();
        }

        // Project window on the configured vault: every view, an item in the side panel, and editing it.
        var project = new Project.ProjectWindow(
            services.GetRequiredService<Buddy.Core.Vault.VaultIndex>(),
            services.GetRequiredService<IOptions<BuddySettings>>(),
            services.GetRequiredService<Buddy.Core.Claude.ClaudeUsage>(),
            services.GetRequiredService<PetWindow>().Sprites.Frames(PetState.Idle, ChatMode.Local)[0]);
        project.Show();
        foreach (var view in new[] { "overview", "board", "archive", "manualtests", "webtests", "notes", "graph", "week", "costs", "decisions", "glossary", "prs" })
        {
            project.ShowView(view);
            if (view is "week" or "prs")
            {
                await Task.Delay(TimeSpan.FromSeconds(4)); // git and gh answer in the background
                await FlushAsync();
            }

            if (view == "graph")
            {
                await Task.Delay(TimeSpan.FromSeconds(7)); // the force layout settles and fits itself
            }

            // Right click on a note of the graph → "Collega a un'altra nota…": the picker, then cancelled (no write).
            if (view == "graph" && project.FindControl<ListBox>("NotesList")?.Items.OfType<Project.NoteView>().FirstOrDefault() is { } first)
            {
                var linking = project.LinkNoteAsync(first.Note.RelativePath);
                await Task.Delay(TimeSpan.FromSeconds(1));
                await FlushAsync();
                if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    && desktop.Windows.FirstOrDefault(w => w.Title == "Collega a un'altra nota") is { } picker)
                {
                    Save(picker, Path.Combine(folder, "graph-link-picker.png"));
                    picker.Close();
                }

                await linking;
            }

            await FlushAsync();
            Save(project, Path.Combine(folder, $"project-{view}.png"));

            // A note with pictures, to check that they load next to the text.
            if (view == "notes" && project.FindControl<ListBox>("NotesList") is { } notesList
                && notesList.Items.OfType<Project.NoteView>().FirstOrDefault(n => System.Text.RegularExpressions.Regex.IsMatch(n.Note.Content, @"!\[[^\]]*\]\([^)\s]+\.(png|jpe?g|gif)\)")) is { } withImages)
            {
                notesList.SelectedItem = withImages;
                await Task.Delay(TimeSpan.FromSeconds(1));
                await FlushAsync();
                Save(project, Path.Combine(folder, "project-notes-images.png"));
            }

            // The usage card folded to one line (the user's choice is put back afterwards).
            if (view == "board")
            {
                var folded = SettingsStore.UsageFolded;
                SettingsStore.UsageFolded = true;
                project.ShowView(view);
                project.RefreshUsage();
                await FlushAsync();
                Save(project, Path.Combine(folder, "project-board-usage-folded.png"));
                SettingsStore.UsageFolded = folded;
                project.RefreshUsage();
            }
            if (view == "overview" && project.FindControl<ScrollViewer>("OverviewView") is { } overview)
            {
                project.FindControl<Control>("HealthPanel")?.BringIntoView();
                await FlushAsync();
                Save(project, Path.Combine(folder, "project-overview-end.png"));
                overview.ScrollToHome();
            }
        }

        project.ShowView("overview");
        var menu = project.OpenProjectMenu();
        await FlushAsync();
        File.WriteAllText(Path.Combine(folder, "project-switcher.txt"), menu);
        project.CloseProjectMenu();

        // A run of the first saved script (the snapshot vault points it to a harmless command).
        if (project.Scripts.FirstOrDefault() is { } script && Environment.GetEnvironmentVariable("BUDDY_SNAPSHOT_RUN") == "1")
        {
            project.ShowView("webtests");
            await project.RunScriptAsync(script);
            await FlushAsync();
            Save(project, Path.Combine(folder, "project-webtest-run.png"));
        }

        // "Nuove idee" with the local model (free), into the snapshot vault's appunti.
        if (Environment.GetEnvironmentVariable("BUDDY_SNAPSHOT_IDEAS") == "1"
            && services.GetRequiredService<Buddy.Core.Vault.VaultIndex>() is { IsAvailable: true } vault
            && Buddy.Core.Vault.ProjectBoard.Load(vault, services.GetRequiredService<IOptions<BuddySettings>>().Value.TaskIdPattern) is { } board
            && board.Columns.FirstOrDefault(c => c.Key == "appunti") is { } inbox)
        {
            var settingsValue = services.GetRequiredService<IOptions<BuddySettings>>().Value;
            var scheda = vault.Notes.FirstOrDefault(n => n.Title == "Scheda progetto")?.Content;
            var ideas = new Project.IdeasWindow(board, scheda, Path.Combine(settingsValue.ProjectPath, inbox.NotePath), settingsValue,
                async (prompt, _, token) =>
                {
                    var answer = new System.Text.StringBuilder();
                    await foreach (var delta in services.GetRequiredService<Buddy.Core.Llm.OllamaClient>()
                                       .ChatAsync([Buddy.Core.Llm.LlmMessage.User(prompt)], token))
                    {
                        answer.Append(delta);
                    }

                    File.WriteAllText(Path.Combine(folder, "ideas-answer.txt"), answer.ToString());
                    return (true, answer.ToString());
                });
            ideas.Show();
            await ideas.GenerateForSnapshotAsync("più statistiche utili per il docente sui risultati degli studenti");
            await FlushAsync();
            await FlushAsync();
            Save(ideas, Path.Combine(folder, "ideas.png"));
            ideas.Close();
        }

        // A note in the editor: tool bar and live preview (nothing is saved).
        project.ShowView("notes");
        if (project.NotesList.Items.OfType<Project.NoteView>().FirstOrDefault(n => n.Title == "Stato attuale") is { } noteToEdit)
        {
            project.NotesList.SelectedItem = noteToEdit;
            await FlushAsync();
            project.EditNoteButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await FlushAsync();
            await Task.Delay(400);
            await FlushAsync();
            Save(project, Path.Combine(folder, "project-note-edit.png"));
            project.CancelNoteButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        }

        project.ShowView("board");
        if ((project.Cards.FirstOrDefault(c => c.HasDetails) ?? project.Cards.FirstOrDefault()) is { } card)
        {
            project.Select(card);
            await FlushAsync();
            Save(project, Path.Combine(folder, "project-detail.png"));
            project.BeginEditSelected();
            await FlushAsync();
            Save(project, Path.Combine(folder, "project-edit.png"));
        }

        project.Close();
    }

    /// <summary>
    /// `--export-icon &lt;folder&gt;`: writes the project icon from the idle sprite: icon.png (256 px, for the README)
    /// and buddy.ico (16-256 px, PNG frames) for the Windows exe.
    /// </summary>
    public static void ExportIcon(PetSprites sprites, string folder)
    {
        Directory.CreateDirectory(folder);
        int[] sizes = [16, 24, 32, 48, 64, 128, 256];
        var frames = sizes.ToDictionary(s => s, sprites.IconPng);
        File.WriteAllBytes(Path.Combine(folder, "icon.png"), frames[256]);

        using var ico = new BinaryWriter(File.Create(Path.Combine(folder, "buddy.ico")));
        ico.Write((short)0);
        ico.Write((short)1);
        ico.Write((short)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        foreach (var size in sizes)
        {
            ico.Write((byte)(size >= 256 ? 0 : size));
            ico.Write((byte)(size >= 256 ? 0 : size));
            ico.Write((byte)0);
            ico.Write((byte)0);
            ico.Write((short)1);
            ico.Write((short)32);
            ico.Write(frames[size].Length);
            ico.Write(offset);
            offset += frames[size].Length;
        }

        foreach (var size in sizes)
        {
            ico.Write(frames[size]);
        }
    }

    /// <summary>Lets layout, rendering and the markdown viewers settle.</summary>
    private static async Task FlushAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(100);
        }
    }

    private static async Task RenderSpritesAsync(PetSprites sprites, string path)
    {
        var panel = new StackPanel { Background = Brushes.White };
        foreach (var mode in Enum.GetValues<ChatMode>())
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var all = Enum.GetValues<PetState>().SelectMany(s => sprites.Frames(s, mode).Distinct()).Concat(sprites.WalkFrames(mode).Distinct());
            foreach (var frame in all.Distinct())
            {
                var image = new Image { Source = frame, Width = 64, Height = 64, Margin = new Thickness(4) };
                RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
                row.Children.Add(image);
            }

            panel.Children.Add(row);
        }

        var window = new Window { Content = panel, SizeToContent = SizeToContent.WidthAndHeight, WindowDecorations = WindowDecorations.None };
        window.Show();
        await FlushAsync();
        Save(window, path);
        window.Close();
    }

    private static void Save(Window window, string path)
    {
        var size = window.Bounds.Size;
        using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width)), Math.Max(1, (int)Math.Ceiling(size.Height))));
        bitmap.Render(window);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }
}
