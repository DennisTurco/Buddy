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

        pet.Show();
        bubble.Anchor = pet;
        bubble.ShowAndFocus();
        await FlushAsync();

        Save(bubble, Path.Combine(folder, "bubble.png"));

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

            await FlushAsync();
            Save(project, Path.Combine(folder, $"project-{view}.png"));
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
