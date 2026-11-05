using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Buddy.Core;
using Buddy.Core.Chat;
using Microsoft.Extensions.Options;

namespace Buddy.App.Pet;

public partial class PetWindow : Window
{
    private readonly Conversation _conversation;
    private readonly BuddySettings _settings;
    private readonly PetSprites _sprites;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _walkTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly Random _random = new();
    private int _tick;
    private double? _walkTarget;
    private DateTime _nextWalk = DateTime.Now.AddSeconds(8);
    private DateTime _lastWalkStep;

    public PetWindow(Conversation conversation, IOptions<BuddySettings> settings)
    {
        InitializeComponent();
        _conversation = conversation;
        _settings = settings.Value;
        _sprites = new PetSprites(_settings.Pet);

        var pet = _settings.Pet;
        Topmost = pet.AlwaysOnTop;
        Sprite.Width = Sprite.Height = Math.Clamp(pet.DisplaySize, 32, 512);
        Sprite.Margin = new Thickness(0, 0, 0, Sprite.Height / 8);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Clamp(pet.FrameMilliseconds, 50, 2000)) };
        _timer.Tick += (_, _) =>
        {
            MaybeStartWalk();
            Animate();
        };
        _walkTimer.Tick += (_, _) => WalkStep();

        _conversation.PropertyChanged += OnConversationChanged;
        Sprite.MouseLeftButtonDown += OnSpriteMouseDown;
        Drop += OnDrop;
        DragOver += (_, e) =>
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        };
        Sprite.ToolTipOpening += async (_, _) => await _conversation.RefreshCurrentTaskAsync();
        UpdateToolTip();
        Loaded += (_, _) =>
        {
            PlaceAtStart();
            Animate();
            _timer.Start();
        };
    }

    /// <summary>Raised on a click (not a drag) on the pet.</summary>
    public event EventHandler? Clicked;

    public event EventHandler? NewConversationRequested;

    public event EventHandler? SettingsRequested;

    public PetSprites Sprites => _sprites;

    /// <summary>When it returns true the pet stays put (e.g. while the chat bubble is open).</summary>
    public Func<bool> HoldStill { get; set; } = () => false;

    private bool IsWalking => _walkTarget is not null;

    /// <summary>Starts a stroll to a random spot on the same row of the screen, when idle and nothing holds the pet.</summary>
    private void MaybeStartWalk()
    {
        var pet = _settings.Pet;
        if (!pet.Walk || IsWalking || _conversation.PetState != PetState.Idle || HoldStill() || IsMouseOver || DateTime.Now < _nextWalk)
        {
            return;
        }

        var (left, right) = HorizontalBounds();
        if (right - left < 100)
        {
            ScheduleNextWalk();
            return;
        }

        // Pick a target at least 80 px away, so a walk is visible.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var target = left + _random.NextDouble() * (right - left);
            if (Math.Abs(target - Left) >= 80)
            {
                _walkTarget = target;
                _lastWalkStep = DateTime.Now;
                Facing.ScaleX = target < Left ? -1 : 1;
                _tick = 0;
                _walkTimer.Start();
                return;
            }
        }

        ScheduleNextWalk();
    }

    private void WalkStep()
    {
        if (_walkTarget is not { } target || _conversation.PetState != PetState.Idle || HoldStill() || IsMouseOver || !_settings.Pet.Walk)
        {
            StopWalking();
            return;
        }

        var now = DateTime.Now;
        var distance = Math.Clamp(_settings.Pet.WalkSpeed, 5, 400) * (now - _lastWalkStep).TotalSeconds;
        _lastWalkStep = now;

        var remaining = target - Left;
        if (Math.Abs(remaining) <= distance)
        {
            Left = target;
            StopWalking();
            return;
        }

        Left += Math.Sign(remaining) * distance;
    }

    private void StopWalking()
    {
        if (!IsWalking)
        {
            return;
        }

        _walkTimer.Stop();
        _walkTarget = null;
        Facing.ScaleX = 1;
        ScheduleNextWalk();
        if (_settings.Pet.RememberPosition)
        {
            SettingsStore.SavePosition(Left, Top);
        }

        Animate();
    }

    private void ScheduleNextWalk()
    {
        var pause = Math.Max(3, _settings.Pet.WalkPauseSeconds);
        _nextWalk = DateTime.Now.AddSeconds(pause * (0.5 + _random.NextDouble()));
    }

    /// <summary>Left-edge range (WPF units) that keeps the pet inside the work area of the monitor it is on.</summary>
    private (double Left, double Right) HorizontalBounds()
    {
        var area = Screens.WorkAreaOf(this);
        return (area.Left, area.Right - ActualWidth);
    }

    /// <summary>Raised when something is dropped on the pet; the attachment is already set on the conversation.</summary>
    public event EventHandler? AttachmentDropped;

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (Input.AttachmentReader.FromDrop(e.Data, _settings.Assistant.MaxAttachmentChars) is { } attachment)
        {
            _conversation.PendingAttachment = attachment;
            AttachmentDropped?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateToolTip()
    {
        var name = string.IsNullOrWhiteSpace(_settings.Appearance.Name) ? "Buddy" : _settings.Appearance.Name;
        Sprite.ToolTip = _conversation.CurrentTask is { } task
            ? $"{name}\n📌 {task}\nClick: chat · trascina qui file o testo"
            : $"{name}\nClick: chat · trascina qui file o testo";
    }

    private void PlaceAtStart()
    {
        var area = SystemParameters.WorkArea;
        if (_settings.Pet.RememberPosition && SettingsStore.LoadPosition() is var (left, top)
            && left >= SystemParameters.VirtualScreenLeft && left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
            && top >= SystemParameters.VirtualScreenTop && top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40)
        {
            Left = left;
            Top = top;
            return;
        }

        Left = area.Right - ActualWidth - 24;
        Top = area.Bottom - ActualHeight;
    }

    private void OnConversationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Conversation.PetState) or nameof(Conversation.Mode))
        {
            _tick = 0;
            Animate();
        }
        else if (e.PropertyName == nameof(Conversation.CurrentTask))
        {
            UpdateToolTip();
        }
    }

    private void Animate()
    {
        var state = _conversation.PetState;
        if (IsWalking)
        {
            var steps = _sprites.WalkFrames(_conversation.Mode);
            Sprite.Source = steps[_tick % steps.Count];
            BadgeText.Text = "";
            Bob.Y = _tick % 2 == 0 ? 0 : Sprite.Height / PetSprites.BuiltInSize;
            _tick++;
            return;
        }

        var frames = _sprites.Frames(state, _conversation.Mode);
        Sprite.Source = frames[_tick % frames.Count];
        BadgeText.Text = PetSprites.Badge(state, _tick / 2);

        // Bob by 1/16 of the size (one pixel of the built-in art): slow when idle, faster while busy, a hop on alerts.
        var step = Sprite.Height / PetSprites.BuiltInSize;
        Bob.Y = state switch
        {
            PetState.Alert or PetState.Happy => _tick % 2 == 0 ? -2 * step : 0,
            PetState.Work or PetState.Think => _tick % 2 == 0 ? step : 0,
            PetState.Error => step,
            _ => _tick / 3 % 2 == 0 ? 0 : step,
        };

        _tick++;
    }

    private void OnSpriteMouseDown(object sender, MouseButtonEventArgs e)
    {
        StopWalking();
        var (left, top) = (Left, Top);
        DragMove();
        if (Math.Abs(Left - left) < 3 && Math.Abs(Top - top) < 3)
        {
            Clicked?.Invoke(this, EventArgs.Empty);
        }
        else if (_settings.Pet.RememberPosition)
        {
            SettingsStore.SavePosition(Left, Top);
        }
    }

    private void OnChatClick(object sender, RoutedEventArgs e) => Clicked?.Invoke(this, EventArgs.Empty);

    private void OnNewConversationClick(object sender, RoutedEventArgs e) => NewConversationRequested?.Invoke(this, EventArgs.Empty);

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised with a preset name to switch to it, or with null to save the current setup as a new preset.</summary>
    public event Action<string?>? PresetRequested;

    /// <summary>Rebuilds the "Progetto" submenu: one entry per preset (the active one checked) and "save as".</summary>
    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        ProjectsMenu.Items.Clear();
        foreach (var preset in _settings.Presets)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = preset.Name,
                IsCheckable = false,
                IsChecked = string.Equals(preset.Name, _settings.ActivePreset, StringComparison.OrdinalIgnoreCase),
                ToolTip = preset.ProjectPath,
            };
            item.Click += (_, _) => PresetRequested?.Invoke(preset.Name);
            ProjectsMenu.Items.Add(item);
        }

        if (_settings.Presets.Count > 0)
        {
            ProjectsMenu.Items.Add(new System.Windows.Controls.Separator());
        }

        var save = new System.Windows.Controls.MenuItem { Header = "Salva il progetto attuale come preset…" };
        save.Click += (_, _) => PresetRequested?.Invoke(null);
        ProjectsMenu.Items.Add(save);
    }

    private void OnOpenVaultClick(object sender, RoutedEventArgs e)
    {
        try
        {
            ObsidianLauncher.OpenProject(_settings);
        }
        catch (System.IO.DirectoryNotFoundException ex)
        {
            MessageBox.Show(ex.Message, "Buddy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
