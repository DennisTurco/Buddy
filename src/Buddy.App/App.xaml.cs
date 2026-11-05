using System.IO;
using System.Windows;
using System.Windows.Threading;
using Buddy.App.Chat;
using Buddy.App.Pet;
using Buddy.Core;
using Buddy.Core.Chat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Buddy.App;

public partial class App : Application
{
    private readonly Buddy.Core.Logging.FileLoggerProvider _logs = new(Path.Combine(SettingsStore.Folder, "logs"));
    private ILogger? _log;
    private IHost? _host;
    private TrayIcon? _tray;
    private Input.GlobalHotkey? _hotkey;
    private Voice.VoiceInput? _voice;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The conversation captures this context to marshal background events onto the UI thread.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher));
        HandleCrashes();

        var builder = Host.CreateApplicationBuilder(e.Args);
        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile(SettingsStore.UserSettingsPath, optional: true, reloadOnChange: false);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(_logs);
        builder.Logging.AddFilter("Microsoft", Microsoft.Extensions.Logging.LogLevel.Warning);
        builder.Services.Configure<BuddySettings>(builder.Configuration.GetSection(BuddySettings.SectionName));
        builder.Services.AddBuddyCore();
        builder.Services.AddSingleton<Buddy.Core.Mentor.IActivityMonitor, Input.ActivityMonitor>();
        builder.Services.AddSingleton<PetWindow>();
        builder.Services.AddSingleton<BubbleWindow>();
        _host = builder.Build();
        _log = _host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("App");
        _log.LogInformation("Avvio Buddy {Version} (progetto: {Project})", typeof(App).Assembly.GetName().Version, builder.Configuration["Buddy:ProjectPath"]);

        var conversation = _host.Services.GetRequiredService<Conversation>();
        var pet = _host.Services.GetRequiredService<PetWindow>();
        var bubble = _host.Services.GetRequiredService<BubbleWindow>();

        if (e.Args is ["--export-icon", var iconFolder])
        {
            Snapshot.ExportIcon(pet.Sprites, iconFolder);
            Shutdown();
            return;
        }

        if (e.Args is ["--snapshot", var folder])
        {
            Snapshot.Render(conversation, pet, bubble, folder, _host.Services);
            Shutdown();
            return;
        }

        var currentSettings = _host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BuddySettings>>().Value;
        if (!currentSettings.FirstRunCompleted)
        {
            var wizard = new Settings.FirstRunWindow(currentSettings);
            if (wizard.ShowDialog() == true)
            {
                SettingsStore.Save(wizard.Result);
                Restart("--welcome");
            }
            else
            {
                Shutdown();
            }

            return;
        }

        if (e.Args.Contains("--welcome"))
        {
            _ = _host.Services.GetRequiredService<Buddy.Core.Mentor.MentorService>().WelcomeAsync();
        }
        else
        {
            conversation.Greet();
        }

        pet.Clicked += (_, _) =>
        {
            if (bubble.IsVisible)
            {
                bubble.Hide();
                return;
            }

            bubble.ShowAndFocus();
        };
        pet.AttachmentDropped += (_, _) => bubble.ShowAndFocus();
        bubble.Anchor = pet;
        pet.HoldStill = () => bubble.IsVisible;

        var settings = _host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BuddySettings>>().Value;
        _tray = new TrayIcon(
            string.IsNullOrWhiteSpace(settings.Appearance.Name) ? "Buddy" : settings.Appearance.Name,
            pet.Sprites.Frames(PetState.Idle, ChatMode.Local)[0],
            openChat: () => { pet.Show(); bubble.ShowAndFocus(); },
            openSettings: OpenSettings,
            openLogs: OpenLogs,
            exit: Shutdown);

        conversation.LongTurnCompleted += turn =>
        {
            if (!bubble.IsActive)
            {
                var title = turn.Success ? $"Claude ha finito ({turn.Elapsed.TotalMinutes:0.#} min)" : "Claude si è fermato con un errore";
                _tray.Notify(title, turn.Answer ?? turn.Request, turn.Success);
            }
        };

        pet.SourceInitialized += (_, _) =>
        {
            _hotkey = Input.GlobalHotkey.TryRegister(pet, settings.Assistant.Hotkey, () =>
            {
                conversation.PendingAttachment = Input.AttachmentReader.FromClipboard(settings.Assistant.MaxAttachmentChars);
                bubble.ShowAndFocus();
            }, out var hotkeyError);

            if (hotkeyError is not null)
            {
                conversation.Entries.Add(new ChatEntry(Speaker.System, hotkeyError));
            }
        };
        pet.LocationChanged += (_, _) =>
        {
            if (bubble.IsVisible)
            {
                bubble.PositionNear(pet);
            }
        };
        pet.NewConversationRequested += async (_, _) => await conversation.SendAsync("/nuova");
        pet.SettingsRequested += (_, _) => OpenSettings();
        pet.PresetRequested += name => SwitchPreset(name);
        bubble.IsVisibleChanged += (_, _) =>
        {
            if (bubble.IsVisible)
            {
                bubble.PositionNear(pet);
            }
        };

        if (settings.Voice.Enabled)
        {
            var model = string.IsNullOrWhiteSpace(settings.Voice.ModelPath)
                ? Path.Combine(SettingsStore.Folder, "models", "ggml-small.bin")
                : settings.Voice.ModelPath;
            _voice = new Voice.VoiceInput(model, settings.Voice.Language);
            bubble.Voice = _voice;
        }

        conversation.MentorPosted += text =>
        {
            // Out of sight: a notification, so a check-in or a question is not missed.
            if (!bubble.IsVisible)
            {
                var name = string.IsNullOrWhiteSpace(settings.Appearance.Name) ? "Buddy" : settings.Appearance.Name;
                _tray.Notify(name, text.Replace("**", ""), success: true);
            }
        };

        pet.Show();
        _ = _host.Services.GetRequiredService<Buddy.Core.Llm.OllamaClient>().WarmUpAsync();
        _host.Services.GetRequiredService<Buddy.Core.Mentor.MentorService>().Start();
    }

    /// <summary>
    /// Switches to a preset (copy its project fields, save, restart) or, with null, saves the current project
    /// setup under a new name.
    /// </summary>
    private void SwitchPreset(string? name)
    {
        var settings = SettingsStore.Clone(_host!.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BuddySettings>>().Value);

        if (name is null)
        {
            var suggested = string.IsNullOrWhiteSpace(settings.ActivePreset) ? Path.GetFileName(settings.ProjectPath) : settings.ActivePreset;
            if (Settings.PromptWindow.Ask(null, "Nuovo preset", "Nome del progetto:", suggested) is not { } newName)
            {
                return;
            }

            settings.Presets.RemoveAll(p => string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase));
            settings.Presets.Add(ProjectPreset.From(settings, newName));
            settings.ActivePreset = newName;
            SettingsStore.Save(settings);
            Restart();
            return;
        }

        var preset = settings.Presets.FirstOrDefault(p => p.Name == name);
        if (preset is null || preset.Name == settings.ActivePreset)
        {
            return;
        }

        // Keep the project being left up to date, so switching back restores what was last used.
        if (settings.Presets.FirstOrDefault(p => p.Name == settings.ActivePreset) is { } current)
        {
            settings.Presets[settings.Presets.IndexOf(current)] = ProjectPreset.From(settings, current.Name);
        }

        preset.ApplyTo(settings);
        SettingsStore.Save(settings);
        Restart();
    }

    /// <summary>
    /// Unexpected errors go to the log instead of closing the app silently: UI-thread exceptions are logged and
    /// reported in the chat (the app keeps running), background ones are logged.
    /// </summary>
    private void HandleCrashes()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            _log?.LogError(e.Exception, "Errore non gestito (interfaccia)");
            e.Handled = true;
            var conversation = _host?.Services.GetService<Conversation>();
            conversation?.Entries.Add(new ChatEntry(Speaker.System, $"⚠ Errore imprevisto: {e.Exception.Message}. Dettagli nei log (tray → Apri i log)."));
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            _log?.LogCritical(e.ExceptionObject as Exception, "Errore fatale, il buddy si chiude");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _log?.LogError(e.Exception, "Errore non gestito (attività in background)");
            e.SetObserved();
        };
    }

    private void OpenLogs()
    {
        Directory.CreateDirectory(_logs.Folder);
        var target = File.Exists(_logs.CurrentFile) ? _logs.CurrentFile : _logs.Folder;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
    }

    private void Restart(string arguments = "")
    {
        if (Environment.ProcessPath is { } exe)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, arguments) { UseShellExecute = true })?.Dispose();
        }

        Shutdown();
    }

    private void OpenSettings()
    {
        var services = _host!.Services;
        var window = new Settings.SettingsWindow(
            services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BuddySettings>>().Value,
            services.GetRequiredService<Buddy.Core.Llm.OllamaClient>());

        if (window.ShowDialog() == true)
        {
            // Restart so every component picks up the new settings.
            Restart();
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _tray?.Dispose();
        _voice?.Dispose();
        if (_host is not null)
        {
            await _host.Services.GetRequiredService<Conversation>().DisposeAsync();
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
