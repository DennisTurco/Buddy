using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Buddy.Core;
using Buddy.Core.Vault;

using Buddy.App.Chat;

namespace Buddy.App.Project;

/// <summary>Asks the AI for ideas: the prompt and whether to use Claude; returns its answer or why it failed.</summary>
public delegate Task<(bool Ok, string Text)> IdeaSource(string prompt, bool useClaude, CancellationToken cancellationToken);

/// <summary>
/// "💡 Nuove idee": what the user would like to add to the project → a few ideas from Claude or the local model,
/// written straight into To-do Appunti ("- data · 💡 idea"). They can be taken back, or sorted at once with /smista.
/// </summary>
public sealed class IdeasWindow : Window
{
    private readonly ProjectBoard _board;
    private readonly string? _scheda;
    private readonly string _inboxPath;
    private readonly IdeaSource _source;
    private readonly TextBox _request = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 90,
        PlaceholderText = "es. «più statistiche per il docente», «rendere più veloce la correzione», «idee per coinvolgere gli studenti»",
    };

    private readonly ComboBox _count = new() { ItemsSource = new[] { 3, 5, 8 }, SelectedIndex = 1, MinWidth = 70 };
    private readonly ComboBox _model = new() { ItemsSource = new[] { "Claude", "Modello locale" }, SelectedIndex = 0, MinWidth = 150 };
    private readonly Button _generate = new() { Content = Icons.Label("💡 Genera e aggiungi agli appunti"), IsDefault = true };
    private readonly ProgressBar _busy = new() { IsIndeterminate = true, IsVisible = false, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly StackPanel _results = new() { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _afterButtons = new() { Orientation = Orientation.Horizontal, Spacing = 6, IsVisible = false, Margin = new Thickness(0, 12, 0, 0) };
    private CancellationTokenSource? _running;
    private List<string> _added = [];

    public IdeasWindow(ProjectBoard board, string? scheda, string inboxPath, BuddySettings settings, IdeaSource source)
    {
        _board = board;
        _scheda = scheda;
        _inboxPath = inboxPath;
        _source = source;

        Title = "Nuove idee";
        Width = 640;
        Height = 600;
        MinWidth = 480;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Appearance.Apply(this, settings.Appearance, font: false);
        RequestedThemeVariant = Appearance.Variant(settings.Appearance);
        Appearance.ApplyControls(this, settings.Appearance);
        Background = (IBrush)Resources["Paper"]!;

        var undo = new Button { Content = "Togli queste idee" };
        undo.Click += async (_, _) => await UndoAsync();
        var sort = new Button { Content = Icons.Label("🗂 Smista ora con Claude") };
        sort.Click += (_, _) =>
        {
            SortRequested?.Invoke();
            Close();
        };
        var close = new Button { Content = "Chiudi" };
        close.Click += (_, _) => Close();
        _afterButtons.Children.Add(sort);
        _afterButtons.Children.Add(undo);
        _afterButtons.Children.Add(close);

        _generate.Click += async (_, _) => await GenerateAsync();

        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        options.Children.Add(new TextBlock { Text = "Quante", VerticalAlignment = VerticalAlignment.Center });
        options.Children.Add(_count);
        options.Children.Add(new TextBlock { Text = "con", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        options.Children.Add(_model);

        // Fixed size: the window does not reliably grow on X11 once shown, so the ideas scroll in the middle.
        var top = new StackPanel();
        var panel = new DockPanel { Margin = new Thickness(22, 18) };
        top.Children.Add(new TextBlock { Text = "💡 Cosa vorresti aggiungere o migliorare nel progetto?", FontSize = 17, FontWeight = FontWeight.Bold });
        top.Children.Add(new TextBlock
        {
            Text = "L'AI riceve il progetto (Scheda, Stato attuale, task aperti, fatti e rifiutati) e propone idee nuove, che finiscono "
                   + "subito in «To-do Appunti». Da lì le porta nel flusso /smista.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (IBrush)Resources["Muted"]!,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 10),
        });
        top.Children.Add(_request);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };
        DockPanel.SetDock(_generate, Dock.Right);
        _generate.VerticalAlignment = VerticalAlignment.Bottom;
        row.Children.Add(_generate);
        row.Children.Add(options);
        top.Children.Add(row);
        top.Children.Add(_busy);
        top.Children.Add(_status);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_afterButtons, Dock.Bottom);
        panel.Children.Add(top);
        panel.Children.Add(_afterButtons);
        panel.Children.Add(new ScrollViewer { Content = _results });
        Content = panel;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
            else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _ = GenerateAsync();
                e.Handled = true;
            }
        };
        Opened += (_, _) => _request.Focus();
        Closed += (_, _) => _running?.Cancel();
    }

    /// <summary>For the snapshot tool: fills the request and generates with the local model.</summary>
    internal Task GenerateForSnapshotAsync(string request)
    {
        _request.Text = request;
        _model.SelectedIndex = 1;
        _count.SelectedIndex = 0;
        return GenerateAsync();
    }

    /// <summary>"Smista ora": the app sends /smista to Claude.</summary>
    public event Action? SortRequested;

    private async Task GenerateAsync()
    {
        var request = _request.Text?.Trim() ?? "";
        if (request.Length < 3 || _running is not null)
        {
            _status.Text = request.Length < 3 ? "Scrivi prima cosa vorresti aggiungere." : _status.Text;
            return;
        }

        var count = _count.SelectedItem is int n ? n : 5;
        var useClaude = _model.SelectedIndex == 0;
        _generate.IsEnabled = false;
        _busy.IsVisible = true;
        _results.Children.Clear();
        _afterButtons.IsVisible = false;
        _status.Foreground = (IBrush)Resources["Muted"]!;
        _status.Text = useClaude ? "Claude ci sta pensando… (di solito 20-60 secondi)" : "Il modello locale ci sta pensando…";
        _running = new CancellationTokenSource();
        try
        {
            var (ok, text) = await _source(IdeaGenerator.BuildPrompt(_board, _scheda, request, count), useClaude, _running.Token);
            var ideas = ok ? IdeaGenerator.Parse(text, count) : [];
            if (!ok || ideas.Count == 0)
            {
                _status.Foreground = Brushes.IndianRed;
                _status.Text = ok ? "La risposta non conteneva idee in elenco: riprova o cambia la richiesta." : $"Non è andata: {text}";
                return;
            }

            var day = DateOnly.FromDateTime(DateTime.Today);
            var lines = ideas.Select(i => IdeaGenerator.ToAppunto(i, day)).ToList();
            foreach (var line in lines)
            {
                VaultWriter.AppendItem(_inboxPath, "Appunti", line);
            }

            _added = lines;
            _status.Foreground = (IBrush)Resources["Ink"]!;
            _status.Text = $"✓ {ideas.Count} idee aggiunte a «{Path.GetFileNameWithoutExtension(_inboxPath)}»:";
            foreach (var idea in ideas)
            {
                _results.Children.Add(new Border
                {
                    Background = (IBrush)Resources["InputBack"]!,
                    BorderBrush = (IBrush)Resources["MentorAccent"]!,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6),
                    Child = new SelectableTextBlock { Text = "💡 " + idea, TextWrapping = TextWrapping.Wrap },
                });
            }

            _afterButtons.IsVisible = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            _status.Foreground = Brushes.IndianRed;
            _status.Text = $"Non è andata: {ex.Message}";
        }
        finally
        {
            _running?.Dispose();
            _running = null;
            _busy.IsVisible = false;
            _generate.IsEnabled = true;
        }
    }

    /// <summary>Takes the ideas just added out of the note again (only those lines, if still there as written).</summary>
    private async Task UndoAsync()
    {
        var removed = 0;
        foreach (var line in _added)
        {
            try
            {
                VaultWriter.ReplaceBlock(_inboxPath, line, "");
                removed++;
            }
            catch (VaultConflictException)
            {
                // Already moved or edited (e.g. by /smista): leave it.
            }
        }

        _added = [];
        _results.Children.Clear();
        _afterButtons.IsVisible = false;
        _status.Foreground = (IBrush)Resources["Muted"]!;
        _status.Text = removed > 0 ? $"Tolte {removed} idee dagli appunti." : "Le idee non erano più negli appunti.";
        await Task.CompletedTask;
    }
}
