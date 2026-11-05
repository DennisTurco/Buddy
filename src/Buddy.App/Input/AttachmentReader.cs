using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Buddy.Core.Chat;

namespace Buddy.App.Input;

/// <summary>Turns clipboard content or a drag-and-drop payload into an <see cref="Attachment"/>.</summary>
public static class AttachmentReader
{
    public static Attachment? FromClipboard(int maxChars)
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                return Attachment.FromFiles(Clipboard.GetFileDropList().Cast<string>(), maxChars);
            }

            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                return Attachment.FromImage(ToPng(image), "immagine dagli appunti");
            }

            if (Clipboard.ContainsText() && Clipboard.GetText() is { Length: > 0 } text)
            {
                return Attachment.FromText(text.Length > maxChars ? text[..maxChars] : text, "appunti");
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is momentarily locked by another program.
        }

        return null;
    }

    public static Attachment? FromDrop(IDataObject data, int maxChars)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
        {
            return Attachment.FromFiles(files, maxChars);
        }

        if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            return Attachment.FromImage(ToPng(bitmap), "immagine trascinata");
        }

        if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text && text.Length > 0)
        {
            return Attachment.FromText(text.Length > maxChars ? text[..maxChars] : text, "testo trascinato");
        }

        return null;
    }

    private static byte[] ToPng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
