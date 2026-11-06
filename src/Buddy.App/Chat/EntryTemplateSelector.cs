using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Buddy.Core.Chat;

namespace Buddy.App.Chat;

/// <summary>Picks the chat template for an entry: user, assistant (local, Claude, mentor), system or permission prompt.</summary>
public sealed class EntryTemplateSelector : IDataTemplate
{
    public IDataTemplate? User { get; set; }
    public IDataTemplate? Assistant { get; set; }
    public IDataTemplate? System { get; set; }
    public IDataTemplate? Permission { get; set; }

    public Control? Build(object? param) => Select(param)?.Build(param);

    public bool Match(object? data) => data is ChatEntry;

    private IDataTemplate? Select(object? item) => item switch
    {
        PermissionEntry => Permission,
        ChatEntry { Speaker: Speaker.User } => User,
        ChatEntry { Speaker: Speaker.System } => System,
        _ => Assistant,
    };
}

/// <summary>True when the bound <see cref="Speaker"/> equals the parameter (e.g. "Claude"): drives the card style classes.</summary>
public sealed class SpeakerIs : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Speaker speaker && string.Equals(speaker.ToString(), parameter as string, StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>An attached image (base64 in the attachment) as a small bitmap for the chat; decoded once per image.</summary>
public sealed class ImageThumbnail : IValueConverter
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AttachedImage, Avalonia.Media.Imaging.Bitmap> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not AttachedImage image)
        {
            return null;
        }

        if (Cache.TryGetValue(image, out var bitmap))
        {
            return bitmap;
        }

        try
        {
            using var stream = new MemoryStream(System.Convert.FromBase64String(image.Base64));
            bitmap = Avalonia.Media.Imaging.Bitmap.DecodeToHeight(stream, 240);
            Cache.AddOrUpdate(image, bitmap);
            return bitmap;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or IOException)
        {
            return null; // e.g. a format Avalonia cannot decode: the label still says an image is attached
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A "/" command offered while typing; the selected one is completed with Tab or Enter.</summary>
public sealed class SuggestionView(ChatCommand command) : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isSelected;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public ChatCommand Command { get; } = command;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}
