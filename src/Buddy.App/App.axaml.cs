using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Buddy.App.Chat;
using Buddy.App.Pet;
using Buddy.App.Platform;
using Buddy.Core;
using Buddy.Core.Chat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Buddy.App;

public partial class App : Application
{
    private readonly Buddy.Core.Logging.FileLoggerProvider _logs = new(Path.Combine(SettingsStore.Folder, "logs"));
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private ILogger? _log;
    private IHost? _host;
    private ITrayIcon? _tray;
    private IDisposable? _hotkey;
    private Voice.VoiceInput? _voice;
    private Project.ProjectWindow? _project;

    /// <summary>Set by <see cref="Program"/>: the single-instance channel (null for the development tools).</summary>
    public static SingleInstance? Instance { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        if (!OperatingSystem.IsWindows())
        {
            Input.DeadKeyComposer.Register();
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.Exit += (_, _) => CleanUp();
            Dispatcher.UIThread.Post(async () => await StartAsync(desktop.Args ?? []));
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The flow's skills (/smista, /lavora…) ship with Buddy: the missing ones go to ~/.claude/skills, so the board's
    /// skill buttons appear and Claude knows the commands. Existing skills are left as they are.
    /// </summary>
    private void InstallSkills(BuddySettings settings)
    {
        try
        {
            if (Buddy.Core.Claude.ClaudeSkills.InstallMissing(settings) is { Count: > 0 } added)
            {
                _log?.LogInformation("Skill di Claude installate: {Skills}", string.Join(", ", added));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(ex, "Non riesco a installare le skill di Claude");
        }
    }

    /// <summary>Closes the buddy (menu "Esci").</summary>
    public static void Exit() => (Current as App)?._desktop?.Shutdown();

    private async Task StartAsync(string[] args)
    {
        HandleCrashes();

        // The content root is the app's folder, not the current directory: started at login the current directory is
        // the home folder, and the host would watch all of it for appsettings changes (tens of thousands of inotify
        // watches, a slow start, and the vault's change events lost in the flood: the board stopped updating live).
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray(),
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile(SettingsStore.UserSettingsPath, optional: true, reloadOnChange: false);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(_logs);
        builder.Logging.AddFilter("Microsoft", Microsoft.Extensions.Logging.LogLevel.Warning);
        builder.Services.Configure<BuddySettings>(builder.Configuration.GetSection(BuddySettings.SectionName));
        builder.Services.AddBuddyCore();
        builder.Services.AddSingleton(_ => PlatformServices.CreateActivityMonitor());
        builder.Services.AddSingleton<PetWindow>();
        builder.Services.AddSingleton<BubbleWindow>();
        _host = builder.Build();
        _log = _host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("App");
        _log.LogInformation("Avvio Buddy {Version} su {Os} (progetto: {Project})",
            typeof(App).Assembly.GetName().Version, System.Runtime.InteropServices.RuntimeInformation.OSDescription, builder.Configuration["Buddy:ProjectPath"]);

        _host.Services.GetRequiredService<Buddy.Core.Claude.ClaudeUsage>().PersistTo(Path.Combine(SettingsStore.Folder, "usage.json"));
        _host.Services.GetRequiredService<Buddy.Core.Claude.ClaudePlanUsage>().Start(TimeSpan.FromMinutes(2)); // the 5-hour and weekly bars, as in Claude Code
        var conversation = _host.Services.GetRequiredService<Conversation>();
        conversation.Archive = new ConversationArchive(Path.Combine(SettingsStore.Folder, "conversations"));
        conversation.LogFolder = _logs.Folder;
        conversation.RestartRequested = () => Dispatcher.UIThread.Post(() => Restart());

        // The chat keeps talking to the model chosen last time: after a restart a request for Claude doesn't land
        // on the local model (and need "/c" again).
        if (SettingsStore.LoadChatMode() is { } lastMode)
        {
            conversation.RestoreMode(lastMode);
        }

        conversation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Conversation.Mode))
            {
                SettingsStore.SaveChatMode(conversation.Mode);
            }
        };
        var pet = _host.Services.GetRequiredService<PetWindow>();
        // The buddy's own sprite next to the mentor's messages in the chat (bubble and board).
        Resources["MentorAvatar"] = pet.Sprites.Frames(PetState.Idle, ChatMode.Local)[0];
        var bubble = _host.Services.GetRequiredService<BubbleWindow>();
        var settings = _host.Services.GetRequiredService<IOptions<BuddySettings>>().Value;

        if (args is ["--export-icon", var iconFolder])
        {
            Snapshot.ExportIcon(pet.Sprites, iconFolder);
            Exit();
            return;
        }

        if (args is ["--snapshot", var folder])
        {
            await Snapshot.RenderAsync(conversation, pet, bubble, folder, _host.Services);
            Exit();
            return;
        }

        InstallSkills(settings);

        if (!settings.FirstRunCompleted)
        {
            var wizard = new Settings.FirstRunWindow(settings);
            if (await wizard.ShowAndWaitAsync() is { } result)
            {
                SettingsStore.Save(result);
                Restart("--welcome");
            }
            else
            {
                Exit();
            }

            return;
        }

        if (args.Contains("--welcome"))
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
        bubble.LocalModelsSource = () => _host.Services.GetRequiredService<Buddy.Core.Llm.OllamaClient>().ListModelsAsync();
        bubble.ChatShownElsewhere = () => _project is { IsChatOpen: true };
        pet.HoldStill = () => bubble.IsVisible;
        pet.Moved += (_, _) =>
        {
            if (bubble.IsVisible)
            {
                bubble.PositionNear(pet);
            }
        };
        pet.NewConversationRequested += async (_, _) => await conversation.SendAsync("/nuova");
        pet.SettingsRequested += async (_, _) => await OpenSettingsAsync();
        pet.ProjectBoardRequested += (_, _) => OpenProject();
        pet.PresetRequested += async name => await SwitchPresetAsync(name);

        void OpenChat()
        {
            pet.Show();
            bubble.ShowAndFocus();
        }

        // Hidden: no pet and no chat; the tray icon, the shortcut and notifications bring it back.
        void HideBuddy()
        {
            bubble.Hide();
            pet.Hide();
        }

        void ToggleBuddy()
        {
            if (pet.IsVisible)
            {
                HideBuddy();
            }
            else
            {
                pet.Show();
            }
        }

        pet.HideRequested += (_, _) => HideBuddy();

        async void OnHotkey()
        {
            conversation.PendingAttachment = await Input.AttachmentReader.FromClipboardAsync(pet.Clipboard, settings.Assistant.MaxAttachmentChars);
            pet.Show();
            bubble.ShowAndFocus();
        }

        var name = string.IsNullOrWhiteSpace(settings.Appearance.Name) ? "Buddy" : settings.Appearance.Name;
        try
        {
            _tray = PlatformServices.CreateTrayIcon(name, pet.Sprites.IconPng(32),
                new TrayActions(OpenChat, ToggleBuddy, OpenProject, () => _ = OpenSettingsAsync(), OpenLogs, Exit));
        }
        catch (Exception ex)
        {
            // No notification area (e.g. a desktop without StatusNotifier support): the pet's menu still works.
            _log.LogWarning(ex, "Icona nell'area di notifica non disponibile");
        }

        conversation.LongTurnCompleted += turn =>
        {
            if (!bubble.IsActive && _project is not { IsActive: true, IsChatOpen: true })
            {
                var title = turn.Success ? $"Claude ha finito ({turn.Elapsed.TotalMinutes:0.#} min)" : "Claude si è fermato con un errore";
                _tray?.Notify(title, turn.Answer ?? turn.Request, turn.Success);
            }
        };

        _hotkey = PlatformServices.RegisterHotkey(settings.Assistant.Hotkey, OnHotkey, out var hotkeyError);
        if (hotkeyError is not null)
        {
            conversation.Entries.Add(new ChatEntry(Speaker.System, hotkeyError));
        }

        if (Instance is { } instance)
        {
            instance.CommandReceived += command =>
            {
                if (command == SingleInstance.HotkeyCommand)
                {
                    OnHotkey();
                }
                else
                {
                    OpenChat();
                }
            };
        }

        if (settings.Voice.Enabled)
        {
            var model = string.IsNullOrWhiteSpace(settings.Voice.ModelPath)
                ? Path.Combine(SettingsStore.Folder, "models", "ggml-small.bin")
                : settings.Voice.ModelPath;
            _voice = new Voice.VoiceInput(model, settings.Voice.Language, PlatformServices.CreateAudioCapture());
            bubble.Voice = _voice;
        }

        conversation.MentorPosted += text =>
        {
            // Out of sight: a notification, so a check-in or a question is not missed.
            if (!bubble.IsVisible && _project is not { IsChatOpen: true })
            {
                _tray?.Notify(name, text.Replace("**", ""), success: true);
            }
        };

        // Running from the sources: a newer build is announced, and /aggiorna rebuilds and restarts on it.
        var exe = Environment.ProcessPath ?? "";
        if (_builds.IsAvailable && !Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            _builds.NewerBuild += built => Dispatcher.UIThread.Post(() =>
            {
                conversation.AnnounceNewerBuild(built);
                conversation.UpdateRequested = () =>
                {
                    if (_builds.RestartWithBuild(exe))
                    {
                        Exit();
                    }
                };
                if (!bubble.IsVisible)
                {
                    _tray?.Notify(name, "È pronta una versione più nuova: scrivi /aggiorna nella chat per usarla.", success: true);
                }
            });
        }

        pet.Show();
        _ = _host.Services.GetRequiredService<Buddy.Core.Llm.OllamaClient>().WarmUpAsync();
        _host.Services.GetRequiredService<Buddy.Core.Mentor.MentorService>().Start();
    }

    private readonly BuildWatcher _builds = new();

    /// <summary>
    /// Switches to another project (null: asks for a new one) and restarts on it. The project being left is saved
    /// first, so switching back restores it as it was.
    /// </summary>
    private async Task SwitchPresetAsync(string? name, Avalonia.Controls.Window? owner = null)
    {
        var settings = SettingsStore.Clone(_host!.Services.GetRequiredService<IOptions<BuddySettings>>().Value);

        ProjectPreset? target;
        if (name is null)
        {
            target = await new Settings.NewProjectWindow(settings).ShowAndWaitAsync(owner);
        }
        else
        {
            target = settings.Presets.FirstOrDefault(p => p.Name == name);
            if (target is null || string.Equals(target.Name, settings.ActivePreset, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        if (target is null)
        {
            return;
        }

        _log?.LogInformation("Cambio progetto: {From} → {To}", settings.ActivePreset, target.Name);
        settings.SwitchTo(target);
        SettingsStore.Save(settings);
        Restart();
    }

    /// <summary>
    /// Unexpected errors go to the log instead of closing the app silently: UI-thread exceptions are logged and
    /// reported in the chat (the app keeps running), background ones are logged.
    /// </summary>
    private void HandleCrashes()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
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

    /// <summary>Shows the project board, reusing the window when it is already open.</summary>
    private void OpenProject()
    {
        if (_project is { } open)
        {
            open.WindowState = Avalonia.Controls.WindowState.Normal;
            open.Activate();
            return;
        }

        var services = _host!.Services;
        _project = new Project.ProjectWindow(
            services.GetRequiredService<Buddy.Core.Vault.VaultIndex>(),
            services.GetRequiredService<IOptions<BuddySettings>>(),
            services.GetRequiredService<Buddy.Core.Claude.ClaudeUsage>(),
            services.GetRequiredService<PetWindow>().Sprites.Frames(PetState.Idle, ChatMode.Local)[0]);
        _project.Commands = services.GetRequiredService<Conversation>().Commands;
        _project.PresetRequested +=async name => await SwitchPresetAsync(name, _project);
        _project.IdeaSource = async (prompt, useClaude, cancellationToken) =>
        {
            var settings = services.GetRequiredService<IOptions<BuddySettings>>().Value;
            if (useClaude)
            {
                // A one-off call in the vault folder, like the mentor's: no chat session, no permission prompts.
                var result = await services.GetRequiredService<Buddy.Core.Claude.ClaudeOneShot>().RunAsync(
                    prompt, settings.ProjectPath, string.IsNullOrWhiteSpace(settings.Claude.Model) ? null : settings.Claude.Model,
                    cancellationToken: cancellationToken);
                return (result.Success, result.Text);
            }

            var answer = new System.Text.StringBuilder();
            await foreach (var delta in services.GetRequiredService<Buddy.Core.Llm.OllamaClient>()
                               .ChatAsync([Buddy.Core.Llm.LlmMessage.User(prompt)], cancellationToken))
            {
                answer.Append(delta);
            }

            return (true, answer.ToString());
        };
        // The board's own chat: the same conversation as the bubble, in a panel next to the board.
        var project = _project;
        var bubble = services.GetRequiredService<BubbleWindow>();
        project.AttachChat(services.GetRequiredService<Conversation>(), services.GetRequiredService<Buddy.Core.Claude.ClaudeUsage>(),
            _voice, bubble.LocalModelsSource);

        // What the board sends to the chat goes to its own chat while that is open, to the bubble otherwise.
        async Task SendToChatAsync(string text)
        {
            if (project.IsChatOpen)
            {
                await project.SendInChatAsync(text);
                return;
            }

            bubble.ShowAndFocus();
            await services.GetRequiredService<Conversation>().SendAsync(text);
        }

        project.AskRequested += question =>
        {
            if (project.IsChatOpen)
            {
                project.ShowChatWithText(question);
            }
            else
            {
                bubble.ShowWithText(question);
            }
        };

        // From Ctrl+K: reopen a saved conversation, or run a chat command, in the chat.
        project.ChatCommandRequested += async command => await SendToChatAsync(command);

        // The skill runs in the chat with Claude, where its questions and permission requests show up.
        project.ClaudeRequested += async command => await SendToChatAsync($"/c {command}");
        _project.Closed += (_, _) => _project = null;
        _project.Show();
    }

    private void OpenLogs()
    {
        Directory.CreateDirectory(_logs.Folder);
        Shell.Open(File.Exists(_logs.CurrentFile) ? _logs.CurrentFile : _logs.Folder);
    }

    /// <summary>Starts a new buddy (which waits for this one to release the single-instance lock) and closes this one.</summary>
    private void Restart(string argument = "")
    {
        var (file, arguments) = PlatformServices.SelfCommand();
        if (file.Length > 0)
        {
            var startInfo = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            foreach (var item in arguments.Append("--restarted").Append(argument).Where(a => a.Length > 0))
            {
                startInfo.ArgumentList.Add(item);
            }

            Process.Start(startInfo)?.Dispose();
        }

        Exit();
    }

    private async Task OpenSettingsAsync()
    {
        var services = _host!.Services;
        var window = new Settings.SettingsWindow(
            services.GetRequiredService<IOptions<BuddySettings>>().Value,
            services.GetRequiredService<Buddy.Core.Llm.OllamaClient>(),
            services.GetRequiredService<Conversation>().Commands.Everything);

        if (await window.ShowAndWaitAsync())
        {
            // Restart so every component picks up the new settings.
            Restart();
        }
    }

    private void CleanUp()
    {
        _hotkey?.Dispose();
        _tray?.Dispose();
        _voice?.Dispose();
        if (_host is not null)
        {
            var conversation = _host.Services.GetRequiredService<Conversation>();

            // Stops the Claude process; off the UI thread, which is shutting down and cannot run continuations.
            Task.Run(async () => await conversation.DisposeAsync()).Wait(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }

        _logs.Dispose();
    }
}
