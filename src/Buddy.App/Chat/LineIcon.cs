using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Buddy.App.Chat;

/// <summary>
/// A thin line icon drawn from a 24×24 path (the Icon* geometries in Theme.axaml), in the foreground of the button it
/// sits in. Unlike emoji it looks the same on every system and font, and takes the colour of its button.
/// </summary>
public sealed class LineIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<LineIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<LineIcon>();

    /// <summary>Width and height in pixels.</summary>
    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<LineIcon, double>(nameof(Size), 14);

    /// <summary>Line width in pixels at the final size.</summary>
    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<LineIcon, double>(nameof(Thickness), 1.5);

    static LineIcon()
    {
        AffectsRender<LineIcon>(DataProperty, ForegroundProperty, SizeProperty, ThicknessProperty);
        AffectsMeasure<LineIcon>(SizeProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data || Foreground is not { } brush)
        {
            return;
        }

        var scale = Size / 24;
        var pen = new Pen(brush, Thickness / scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var offset = new Point((Bounds.Width - Size) / 2, (Bounds.Height - Size) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            context.DrawGeometry(null, pen, data);
        }
    }
}

/// <summary>Line icons made in code, for buttons whose look changes (copy → tick, mic → recording…).</summary>
public static class Icons
{
    /// <summary>The icon drawn for a label's leading emoji (the emoji names it in the label, in one place).</summary>
    private static readonly Dictionary<string, string> ForEmoji = new()
    {
        ["💡"] = "IconIdea", ["🐞"] = "IconBug", ["✨"] = "IconSparkles", ["❤"] = "IconHeart", ["💬"] = "IconChat",
        ["⬇"] = "IconDownload", ["🧪"] = "IconFlask", ["📄"] = "IconFile", ["✏"] = "IconEdit", ["■"] = "IconStop",
        ["🧠"] = "IconAsk", ["▶"] = "IconPlay", ["🗑"] = "IconTrash", ["🕸"] = "IconGraph", ["☐"] = "IconCheckbox",
        ["❝"] = "IconQuote", ["🔗"] = "IconLink", ["▦"] = "IconTable", ["👁"] = "IconEye", ["↻"] = "IconRefresh",
        ["🔔"] = "IconBell", ["🗂"] = "IconInbox", ["🛠"] = "IconWrench", ["🧭"] = "IconCompass", ["🚫"] = "IconBan",
        ["🤔"] = "IconHelp", ["✅"] = "IconCheckCircle", ["🌐"] = "IconGlobe", ["🔁"] = "IconRepeat", ["✚"] = "IconPlus",
        ["↗"] = "IconExternal", ["📝"] = "IconNote", ["✕"] = "IconClose", ["📌"] = "IconPin", ["💾"] = "IconSave",
        ["⟳"] = "IconRefresh", ["↶"] = "IconUndo",
    };

    /// <summary>"🐞 Bug" → ("IconBug", "Bug"); a label without a known leading emoji → (null, label).</summary>
    public static (string? Key, string Text) Split(string label)
    {
        foreach (var (emoji, key) in ForEmoji)
        {
            if (label.StartsWith(emoji, StringComparison.Ordinal))
            {
                // Also drop the variation selector some emoji carry (✏️, ❤️).
                return (key, label[emoji.Length..].TrimStart('\uFE0F').Trim());
            }
        }

        return (null, label);
    }

    /// <summary>A button label with its emoji drawn as a line icon (see <see cref="IconLabel"/>).</summary>
    public static IconLabel Label(string label) => new() { Label = label };

    /// <summary>The icon <paramref name="key"/> (e.g. "IconCopy" in Theme.axaml), optionally followed by a short text.</summary>
    public static Control Of(string key, string? text = null, double size = 14)
    {
        var icon = new LineIcon { Size = size, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        if (Avalonia.Application.Current?.TryFindResource(key, out var geometry) == true)
        {
            icon.Data = geometry as Geometry;
        }

        if (text is null)
        {
            return icon;
        }

        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 3 };
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        return panel;
    }
}

/// <summary>
/// A button label with a line icon in place of its leading emoji: "🐞 Bug" draws the bug icon and "Bug". The emoji
/// stays in the label as the name of the icon (see <see cref="Icons.Split"/>); one without an icon is left as it is.
/// </summary>
public sealed class IconLabel : StackPanel
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<IconLabel, string?>(nameof(Label));

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<IconLabel, double>(nameof(IconSize), 13);

    public IconLabel()
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal;
        Spacing = 5;
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty || change.Property == IconSizeProperty)
        {
            Build();
        }
    }

    private void Build()
    {
        Children.Clear();
        var (key, text) = Icons.Split(Label ?? "");
        if (key is not null)
        {
            Children.Add(Icons.Of(key, size: IconSize));
        }

        if (text.Length > 0)
        {
            Children.Add(new TextBlock { Text = text, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        }
    }

    public override string ToString() => Label ?? "";
}
