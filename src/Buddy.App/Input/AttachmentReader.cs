using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Buddy.Core.Chat;

namespace Buddy.App.Input;

/// <summary>Turns clipboard content or a drag-and-drop payload into an <see cref="Attachment"/>.</summary>
public static class AttachmentReader
{
    public static async Task<Attachment?> FromClipboardAsync(IClipboard? clipboard, int maxChars)
    {
        if (clipboard is null)
        {
            return null;
        }

        try
        {
            if (await clipboard.TryGetFilesAsync() is { Length: > 0 } files && LocalPaths(files) is { Count: > 0 } paths)
            {
                return Attachment.FromFiles(paths, maxChars);
            }

            if (await clipboard.TryGetBitmapAsync() is { } image)
            {
                return Attachment.FromImage(ToPng(image), "immagine dagli appunti");
            }

            if (await clipboard.TryGetTextAsync() is { Length: > 0 } text)
            {
                return Attachment.FromText(text.Length > maxChars ? text[..maxChars] : text, "appunti");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Runtime.InteropServices.COMException)
        {
            // The clipboard is momentarily locked by another program, or holds something unreadable.
        }

        return null;
    }

    public static Attachment? FromDrop(IDataTransfer data, int maxChars)
    {
        if (data.TryGetFiles() is { Length: > 0 } files && LocalPaths(files) is { Count: > 0 } paths)
        {
            return Attachment.FromFiles(paths, maxChars);
        }

        if (data.TryGetBitmap() is { } bitmap)
        {
            return Attachment.FromImage(ToPng(bitmap), "immagine trascinata");
        }

        if (data.TryGetText() is { Length: > 0 } text)
        {
            return Attachment.FromText(text.Length > maxChars ? text[..maxChars] : text, "testo trascinato");
        }

        return null;
    }

    private static List<string> LocalPaths(IEnumerable<IStorageItem> items) =>
        items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList();

    private static byte[] ToPng(Bitmap image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }
}
