using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Buddy.Core;
using Buddy.Core.Chat;
using Microsoft.Extensions.Options;

namespace Buddy.App.Pet;

public partial class PetWindow : Window
{
    /// <summary>Pointer travel (pixels) below which a press on the pet is a click rather than a drag.</summary>
    private const int DragThreshold = 4;

    private readonly Conversation _conversation;
    private readonly BuddySettings _settings;
    private readonly PetSprites _sprites;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _walkTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly Random _random = new();

    /// <summary>ScaleX -1 mirrors the sprite while walking left.</summary>
    private readonly Avalonia.Media.ScaleTransform Facing = new();

    /// <summary>Moves the sprite up and down a pixel of the art to make it look alive.</summary>
    private readonly Avalonia.Media.TranslateTransform Bob = new();
    private int _tick;
    private double? _walkTarget;
    private double _walkX;
    private DateTime _nextWalk = DateTime.Now.AddSeconds(8);
    private DateTime _lastWalkStep;
    private PixelPoint? _pressScreen;
    private PixelPoint _pressWindow;
    private bool _dragging;

    /// <summary>For the XAML previewer (see <see cref="DesignData"/>).</summary>
    public PetWindow() : this(DesignData.Conversation, DesignData.Settings)
    {
    }

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
        Sprite.RenderTransform = new Avalonia.Media.TransformGroup { Children = { Facing, Bob } };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Clamp(pet.FrameMilliseconds, 50, 2000)) };
        _timer.Tick += (_, _) =>
        {
            MaybeStartWalk();
            Animate();
        };
        _walkTimer.Tick += (_, _) => WalkStep();

        _conversation.PropertyChanged += OnConversationChanged;
        Sprite.PointerPressed += OnSpritePointerPressed;
        Sprite.PointerMoved += OnSpritePointerMoved;
        Sprite.PointerReleased += OnSpritePointerReleased;
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        });
        ToolTip.AddToolTipOpeningHandler(Sprite, async (_, _) => await _conversation.RefreshCurrentTaskAsync());
        Menu.Opening += (_, _) => RebuildProjectsMenu();
        UpdateToolTip();
        Opened += (_, _) =>
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

    /// <summary>Menu "Nascondi Buddy": the app hides the pet and the chat.</summary>
    public event EventHandler? HideRequested;

    /// <summary>Menu "Bacheca del progetto".</summary>
    public event EventHandler? ProjectBoardRequested;

    /// <summary>Raised when the pet has been moved (dragged or walked), so the bubble can follow.</summary>
    public event EventHandler? Moved;

    /// <summary>Raised when something is dropped on the pet; the attachment is already set on the conversation.</summary>
    public event EventHandler? AttachmentDropped;

    /// <summary>Raised with a preset name to switch to it, or with null to save the current setup as a new preset.</summary>
    public event Action<string?>? PresetRequested;

    public PetSprites Sprites => _sprites;

    /// <summary>When it returns true the pet stays put (e.g. while the chat bubble is open).</summary>
    public Func<bool> HoldStill { get; set; } = () => false;

    private bool IsWalking => _walkTarget is not null;

    /// <summary>Starts a stroll to a random spot on the same row of the screen, when idle and nothing holds the pet.</summary>
    private void MaybeStartWalk()
    {
        var pet = _settings.Pet;
        if (!pet.Walk || IsWalking || _dragging || _conversation.PetState != PetState.Idle || HoldStill() || IsPointerOver || DateTime.Now < _nextWalk)
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
            if (Math.Abs(target - Position.X) >= 80)
            {
                _walkTarget = target;
                _walkX = Position.X;
                _lastWalkStep = DateTime.Now;
                Facing.ScaleX = target < Position.X ? -1 : 1;
                _tick = 0;
                _walkTimer.Start();
                return;
            }
        }

        ScheduleNextWalk();
    }

    private void WalkStep()
    {
        if (_walkTarget is not { } target || _dragging || _conversation.PetState != PetState.Idle || HoldStill() || IsPointerOver || !_settings.Pet.Walk)
        {
            StopWalking();
            return;
        }

        var now = DateTime.Now;
        var distance = Math.Clamp(_settings.Pet.WalkSpeed, 5, 400) * Displays.Scaling(this) * (now - _lastWalkStep).TotalSeconds;
        _lastWalkStep = now;

        // Positions are whole pixels: keep the exact x apart so slow walks do not stall on rounding.
        var remaining = target - _walkX;
        if (Math.Abs(remaining) <= distance)
        {
            _walkX = target;
            Position = new PixelPoint((int)Math.Round(target), Position.Y);
            StopWalking();
            return;
        }

        _walkX += Math.Sign(remaining) * distance;
        Position = new PixelPoint((int)Math.Round(_walkX), Position.Y);
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
        SavePosition();
        Animate();
    }

    private void ScheduleNextWalk()
    {
        var pause = Math.Max(3, _settings.Pet.WalkPauseSeconds);
        _nextWalk = DateTime.Now.AddSeconds(pause * (0.5 + _random.NextDouble()));
    }

    /// <summary>Left-edge range (pixels) that keeps the pet inside the work area of the monitor it is on.</summary>
    private (double Left, double Right) HorizontalBounds()
    {
        var area = Displays.WorkAreaOf(this);
        return (area.X, area.Right - Displays.SizeInPixels(this).Width);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Input.AttachmentReader.FromDrop(e.DataTransfer, _settings.Assistant.MaxAttachmentChars) is { } attachment)
        {
            _conversation.PendingAttachment = attachment;
            AttachmentDropped?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateToolTip()
    {
        var name = string.IsNullOrWhiteSpace(_settings.Appearance.Name) ? "Buddy" : _settings.Appearance.Name;
        ToolTip.SetTip(Sprite, _conversation.CurrentTask is { } task
            ? $"{name}\n📌 {task}\nClick: chat · trascina qui file o testo"
            : $"{name}\nClick: chat · trascina qui file o testo");
    }

    private void PlaceAtStart()
    {
        if (_settings.Pet.RememberPosition && SettingsStore.LoadPosition() is { } saved
            && Displays.IsOnScreen(this, new PixelPoint((int)saved.Left, (int)saved.Top)))
        {
            Position = new PixelPoint((int)saved.Left, (int)saved.Top);
            return;
        }

        var area = Displays.WorkAreaOf(this);
        var size = Displays.SizeInPixels(this);
        Position = new PixelPoint(area.Right - size.Width - 24, area.Bottom - size.Height);
    }

    private void SavePosition()
    {
        if (_settings.Pet.RememberPosition)
        {
            SettingsStore.SavePosition(Position.X, Position.Y);
        }

        Moved?.Invoke(this, EventArgs.Empty);
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

    // Dragging is done by hand rather than with BeginMoveDrag: on X11 the window manager takes over the move and
    // returns at once, so a click could not be told apart from a drag.
    private void OnSpritePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Sprite).Properties.IsLeftButtonPressed)
        {
            return;
        }

        StopWalking();
        _pressScreen = this.PointToScreen(e.GetPosition(this));
        _pressWindow = Position;
        _dragging = false;
        e.Pointer.Capture(Sprite);
        e.Handled = true;
    }

    private void OnSpritePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressScreen is not { } start)
        {
            return;
        }

        var now = this.PointToScreen(e.GetPosition(this));
        var delta = now - start;
        if (!_dragging && Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
        {
            return;
        }

        _dragging = true;
        Position = _pressWindow + delta;
        Moved?.Invoke(this, EventArgs.Empty);
    }

    private void OnSpritePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressScreen is null)
        {
            return;
        }

        _pressScreen = null;
        e.Pointer.Capture(null);
        if (_dragging)
        {
            _dragging = false;
            SavePosition();
        }
        else if (e.InitialPressMouseButton == MouseButton.Left)
        {
            Clicked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnChatClick(object? sender, RoutedEventArgs e) => Clicked?.Invoke(this, EventArgs.Empty);

    private void OnNewConversationClick(object? sender, RoutedEventArgs e) => NewConversationRequested?.Invoke(this, EventArgs.Empty);

    private void OnSettingsClick(object? sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnHideClick(object? sender, RoutedEventArgs e) => HideRequested?.Invoke(this, EventArgs.Empty);

    private void OnProjectBoardClick(object? sender, RoutedEventArgs e) => ProjectBoardRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Rebuilds the "Progetto" submenu: one entry per project (the current one checked), new, manage.</summary>
    private void RebuildProjectsMenu()
    {
        ProjectsMenu.Items.Clear();
        foreach (var preset in _settings.Presets)
        {
            var item = new MenuItem
            {
                Header = preset.Name,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = string.Equals(preset.Name, _settings.ActivePreset, StringComparison.OrdinalIgnoreCase),
            };
            ToolTip.SetTip(item, preset.ProjectPath);
            item.Click += (_, _) => PresetRequested?.Invoke(preset.Name);
            ProjectsMenu.Items.Add(item);
        }

        if (_settings.Presets.Count > 0)
        {
            ProjectsMenu.Items.Add(new Separator());
        }

        var create = new MenuItem { Header = "Nuovo progetto…" };
        create.Click += (_, _) => PresetRequested?.Invoke(null);
        ProjectsMenu.Items.Add(create);
        var manage = new MenuItem { Header = "Gestisci progetti…" };
        manage.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        ProjectsMenu.Items.Add(manage);
    }

    private async void OnOpenVaultClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            ObsidianLauncher.OpenProject(_settings);
        }
        catch (DirectoryNotFoundException ex)
        {
            await Settings.Dialogs.ShowMessageAsync(null, ex.Message);
        }
    }

    private async void OnOpenVaultFolderClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            ObsidianLauncher.OpenFolder(_settings);
        }
        catch (DirectoryNotFoundException ex)
        {
            await Settings.Dialogs.ShowMessageAsync(null, ex.Message);
        }
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => App.Exit();
}
