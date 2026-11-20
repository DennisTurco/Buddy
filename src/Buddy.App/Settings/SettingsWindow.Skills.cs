using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Buddy.Core.Claude;

namespace Buddy.App.Settings;

/// <summary>
/// Impostazioni → Comandi → "Skill consigliate": skills by others (<see cref="RecommendedSkills"/>) with an
/// «Installa» button that runs <c>npx skills add … --global</c>. Installing does not wait for "Salva": it is done
/// on the spot, like in a terminal.
/// </summary>
public partial class SettingsWindow
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);

    private void BuildRecommendedSkills()
    {
        foreach (var skill in RecommendedSkills.All)
        {
            RecommendedSkillRows.Children.Add(RecommendedSkillRow(skill));
        }
    }

    private Control RecommendedSkillRow(RecommendedSkill skill)
    {
        var installed = RecommendedSkills.IsInstalled(skill, _settings);
        var status = new TextBlock { Classes = { "hint" }, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 2, 0, 0) };
        var install = new Button
        {
            Content = installed ? "✓ Installata" : "Installa",
            IsEnabled = !installed,
            MinWidth = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(install, installed ? "Già nelle skill di Claude: usala in chat con /" + skill.Name : skill.InstallCommand);
        install.Click += async (_, _) =>
        {
            install.IsEnabled = false;
            install.Content = "Installo…";
            status.IsVisible = true;
            status.Text = "Scarico la skill da " + skill.Source + "…";
            var (ok, message) = await InstallAsync(skill);
            install.Content = ok ? "✓ Installata" : "Riprova";
            install.IsEnabled = !ok;
            status.Text = ok
                ? $"Installata: in chat scrivi /{skill.Name}. Compare tra i comandi qui sopra al prossimo avvio."
                : message;
        };

        var source = new Button { Classes = { "link" }, Content = "Repository ↗", Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        source.Click += (_, _) => Shell.Open(skill.Source);

        var name = new TextBlock
        {
            Text = "/" + skill.Name,
            FontWeight = FontWeight.SemiBold,
            FontFamily = (FontFamily)this.FindResource("MonoFont")!,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        title.Children.AddRange([name, new TextBlock { Text = "di " + skill.Author, Classes = { "hint" }, VerticalAlignment = VerticalAlignment.Center }, source]);

        var text = new StackPanel { Spacing = 2 };
        text.Children.AddRange([title, new TextBlock { Text = skill.Description, TextWrapping = TextWrapping.Wrap }, status]);

        var row = new DockPanel();
        DockPanel.SetDock(install, Dock.Right);
        install.Margin = new Thickness(12, 0, 0, 0);
        row.Children.AddRange([install, text]);
        return new Border
        {
            Child = row,
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = this.FindResource("Frame") as IBrush,
        };
    }

    /// <summary>Runs the install command; on failure the message says why (no Node.js, no network, the CLI's last words).</summary>
    private static async Task<(bool Ok, string Message)> InstallAsync(RecommendedSkill skill)
    {
        // On Windows npx is a .cmd script: it runs through cmd.exe.
        var windows = OperatingSystem.IsWindows();
        var start = new ProcessStartInfo(windows ? "cmd.exe" : "npx")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // A global install writes nothing in the folder it runs in; the home folder keeps any leftover out of the vault.
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (windows)
        {
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("npx");
        }

        foreach (var argument in skill.InstallArguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["NO_COLOR"] = "1";
        start.Environment["CI"] = "1"; // no interactive prompts or spinners

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return (false, NoNode);
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(InstallTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return (false, "L'installazione ci ha messo troppo ed è stata fermata. Controlla la rete e riprova.");
            }

            if (process.ExitCode == 0)
            {
                return (true, "");
            }

            var said = LastLines((await errors) + "\n" + (await output));
            return (false, windows && process.ExitCode == 9009 || said.Contains("not recognized", StringComparison.OrdinalIgnoreCase)
                ? NoNode
                : $"Installazione non riuscita (codice {process.ExitCode}). {said}\nPuoi provare a mano in un terminale: {skill.InstallCommand}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return (false, NoNode);
        }
    }

    private const string NoNode = "npx non trovato: installa Node.js (https://nodejs.org), riavvia Buddy e riprova.";

    /// <summary>The last few meaningful lines of the CLI's output, without its box-drawing frame.</summary>
    private static string LastLines(string text) =>
        string.Join(' ', text.Split('\n')
            .Select(l => l.Trim().Trim('│', '◇', '└', '┌', '├', '─', '╮', '╯', ' '))
            .Where(l => l.Length > 0)
            .TakeLast(3));
}
