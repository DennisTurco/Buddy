using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Buddy.App.Chat;
using Buddy.App.Pet;
using Buddy.Core;
using Buddy.Core.Chat;
using Buddy.Core.Claude;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Buddy.App;

/// <summary>
/// Development aid: `Buddy.App.exe --snapshot <folder>` renders the sprites, a sample chat and every tab of the
/// settings window to PNG files, so the UI can be checked without looking at the desktop.
/// </summary>
internal static class Snapshot
{
    public static void Render(Conversation conversation, PetWindow pet, BubbleWindow bubble, string folder, IServiceProvider services)
    {
        Directory.CreateDirectory(folder);
        RenderSprites(pet.Sprites, Path.Combine(folder, "sprites.png"));

        conversation.Greet();
        Pump(conversation.RefreshCurrentTaskAsync());
        Pump(conversation.SendAsync("/modifiche"));
        File.WriteAllText(Path.Combine(folder, "instant.md"), string.Join("\n\n-----\n\n", conversation.Entries.Select(e => $"[{e.Speaker}] {e.Text}")));
        Pump(conversation.SendAsync("/stato"));
        Pump(conversation.SendAsync("/cerca retention"));
        File.WriteAllText(Path.Combine(folder, "instant.md"), string.Join("\n\n-----\n\n", conversation.Entries.Select(e => $"[{e.Speaker}] {e.Text}")));
        conversation.Entries.Clear();
        conversation.PendingAttachment = Attachment.FromText("System.NullReferenceException: Object reference not set to an instance of an object.\n   at LoginFallito.Registra()", "appunti");
        conversation.Entries.Add(new ChatEntry(Speaker.Mentor, "**TCP o UDP?** TCP garantisce ordine e consegna (handshake, ritrasmissioni); UDP spedisce e basta, più veloce. Nel tuo lavoro, dove sceglieresti UDP?"));
        conversation.Entries.Add(new ChatEntry(Speaker.User, "a che punto è NUSR-2367?"));
        conversation.Entries.Add(new ChatEntry(Speaker.Local, "È in **Da controllare**: mancano i test di\n- riattivazione NUSR-2370\n- report *Account bloccati*\n\nFonti: [To-Do.md (NUSR-2367)](obsidian://open?path=x) · [Stato attuale.md](obsidian://open?path=y)")
        {
            Question = "a che punto è NUSR-2367?",
            CanEscalate = true,
        });
        conversation.Entries.Add(new ChatEntry(Speaker.System, "Ora parli con Claude."));
        conversation.Entries.Add(new ChatEntry(Speaker.User, "/lavora NUSR-2367"));
        conversation.Entries.Add(new ChatEntry(Speaker.Claude, "Leggo la voce e il codice. Piano:\n\n1. aggiorno `LoginFallito.vb`\n2. rigenero lo script"));
        using var input = JsonDocument.Parse("""{"file_path":"D:\\Projects\\portale20\\Common\\Business\\BusinessBase\\Accounts\\Data\\LoginFallito.vb"}""");
        conversation.Entries.Add(new PermissionEntry(new PermissionRequest("r1", "Edit", input.RootElement.Clone())));

        pet.Show();
        pet.Left = -3000;
        pet.Top = 0;
        bubble.Show();
        bubble.Left = -3000;
        bubble.Top = 300;
        Flush();

        Save(bubble, Path.Combine(folder, "bubble.png"));
        Save(pet, Path.Combine(folder, "pet.png"));

        // Local answer with its sources and the "Chiedi a Claude" button.
        var local = conversation.Entries.Where(e => e.Speaker == Speaker.Local || e.Text.StartsWith("a che punto")).ToList();
        conversation.PendingAttachment = null;
        conversation.Entries.Clear();
        local.ForEach(conversation.Entries.Add);
        Flush();
        Save(bubble, Path.Combine(folder, "bubble-local.png"));

        // Maximized: fills a screen; moved off-screen again before rendering.
        conversation.Entries.Add(new ChatEntry(Speaker.Claude, string.Join("\n\n", Enumerable.Range(1, 4).Select(i =>
            $"**Punto {i}.** Una risposta lunga di Claude con molte informazioni, che nella nuvoletta piccola va a capo ogni poche parole e diventa difficile da leggere; a schermo intero le righe si allargano fino a una lunghezza comoda."))));
        bubble.SetMaximized(true);
        bubble.Left = -6000;
        Flush();
        Save(bubble, Path.Combine(folder, "bubble-max.png"));
        bubble.SetMaximized(false);

        var settings = new Settings.SettingsWindow(
            services.GetRequiredService<IOptions<BuddySettings>>().Value,
            services.GetRequiredService<Buddy.Core.Llm.OllamaClient>())
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -3000,
            Top = 0,
        };
        settings.Show();
        var tabs = FindChild<TabControl>(settings)!;
        for (var i = 0; i < tabs.Items.Count; i++)
        {
            tabs.SelectedIndex = i;
            Flush();
            Save(settings, Path.Combine(folder, $"settings-{i}.png"));
        }

        settings.Close();
    }

    /// <summary>
    /// `--export-icon <folder>`: writes the project icon from the idle sprite: icon.png (256 px, for the README)
    /// and buddy.ico (16-256 px, PNG frames) for the exe.
    /// </summary>
    public static void ExportIcon(PetSprites sprites, string folder)
    {
        Directory.CreateDirectory(folder);
        var sprite = sprites.Frames(PetState.Idle, ChatMode.Local)[0];
        int[] sizes = [16, 24, 32, 48, 64, 128, 256];
        var frames = sizes.ToDictionary(s => s, s => Png(sprite, s));
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

    /// <summary>The sprite scaled to <paramref name="size"/> pixels without smoothing, as PNG bytes.</summary>
    private static byte[] Png(BitmapSource sprite, int size)
    {
        // Nearest-neighbour by hand: each target pixel copies the source pixel it falls on, so edges stay sharp.
        var source = new FormatConvertedBitmap(sprite, PixelFormats.Bgra32, null, 0);
        var (w, h) = (source.PixelWidth, source.PixelHeight);
        var input = new byte[w * h * 4];
        source.CopyPixels(input, w * 4, 0);
        var output = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                Array.Copy(input, ((y * h / size) * w + x * w / size) * 4, output, (y * size + x) * 4, 4);
            }
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, output, size * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Runs the dispatcher until the task completes (the snapshot runs on the UI thread).</summary>
    private static void Pump(Task task)
    {
        while (!task.IsCompleted)
        {
            Application.Current.Dispatcher.Invoke(DispatcherPriority.Background, () => { });
            Thread.Sleep(10);
        }

        task.GetAwaiter().GetResult();
    }

    private static void Flush() =>
        Application.Current.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, () => { });

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static void RenderSprites(PetSprites sprites, string path)
    {
        var panel = new StackPanel { Background = Brushes.White };
        foreach (var mode in Enum.GetValues<ChatMode>())
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var all = Enum.GetValues<PetState>().SelectMany(s => sprites.Frames(s, mode).Distinct()).Concat(sprites.WalkFrames(mode).Distinct());
            foreach (var frame in all.Distinct())
            {
                var image = new Image { Source = frame, Width = 64, Height = 64, Margin = new Thickness(4) };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
                row.Children.Add(image);
            }

            panel.Children.Add(row);
        }

        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        panel.Arrange(new Rect(panel.DesiredSize));
        Save(panel, path);
    }

    private static void Save(FrameworkElement element, string path)
    {
        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        var bitmap = new RenderTargetBitmap(Math.Max(width, 1), Math.Max(height, 1), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
