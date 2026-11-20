using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Buddy.App.Chat;
using Buddy.Core.Vault;

namespace Buddy.App.Project;

/// <summary>
/// The task's sheet in the side panel (<see cref="TaskSheet"/>, "Task/ID.md"): "Cosa manca" for an open task, "Come è
/// stato fatto" for a closed one. Read as markdown, edited by hand (the user knows things the notes don't), or written
/// by Claude from the vault with "✨".
/// </summary>
public partial class ProjectWindow
{
    /// <summary>The task whose sheet is open in the editor, and the section's text it started from.</summary>
    private (string Id, string Heading, string Original)? _sheetEdit;

    /// <summary>The section shown for the card: none for a card without id or a refused one.</summary>
    private static string? SheetHeading(CardView card) =>
        !card.HasId || card.Item.Raw.Length == 0 ? null
        : card.Column.Key switch
        {
            "rifiutati" => null,
            "fatti" => TaskSheet.HowDone,
            _ => TaskSheet.Missing,
        };

    private string SheetPath(string id) => FullPath(TaskSheet.RelativePath(id));

    /// <summary>Typed in the sheet editor and not saved yet.</summary>
    private bool SheetDirty => _sheetEdit is { } edit && (SheetEditor.Text ?? "").Trim() != edit.Original;

    /// <summary>Fills the sheet for the selected card (an open editor on the same task is left as it is).</summary>
    private void LoadSheet(CardView card)
    {
        if (SheetHeading(card) is not { } heading || card.Id is not { } id)
        {
            StopEditingSheet();
            SheetPanel.IsVisible = false;
            return;
        }

        if (_sheetEdit is { } edit && (edit.Id != id || edit.Heading != heading))
        {
            StopEditingSheet();
        }

        string text;
        try
        {
            text = TaskSheet.Read(SheetPath(id), heading);
        }
        catch (IOException)
        {
            text = "";
        }

        var howDone = heading == TaskSheet.HowDone;
        SheetPanel.IsVisible = true;
        SheetTitle.Text = howDone ? "🧭 Come è stato fatto" : "📋 Cosa manca";
        SheetView.Markdown = text;
        SheetRead.IsVisible = text.Length > 0 && _sheetEdit is null;
        SheetEmpty.IsVisible = text.Length == 0 && _sheetEdit is null;
        SheetEmpty.Text = howDone
            ? "Nessuno storico: con ✨ Claude riassume i passi fatti, da ripetere per un task simile."
            : "Niente ancora: con ✨ Claude scrive cosa manca dalle note, poi puoi correggerlo tu.";
        SheetClaudeButton.Content = Icons.Label(text.Length == 0 ? "✨ Scrivi con Claude" : "✨ Aggiorna");
        ToolTip.SetTip(SheetClaudeButton, howDone
            ? "Claude scrive in Task/ID.md i passi fatti per chiudere il task, tenendo quello che hai scritto tu"
            : "Claude scrive in Task/ID.md cosa manca, tenendo quello che hai corretto tu");
        SheetClaudeButton.IsVisible = _sheetEdit is null;
        SheetEditButton.IsVisible = _sheetEdit is null;
    }

    private void OnSheetEditClick(object? sender, RoutedEventArgs e) => BeginEditSheet();

    internal void BeginEditSheet()
    {
        if (_selected is not { Id: { } id } card || SheetHeading(card) is not { } heading)
        {
            return;
        }

        var text = TaskSheet.Read(SheetPath(id), heading);
        _sheetEdit = (id, heading, text);
        SheetEditor.Text = text;
        SheetEdit.IsVisible = true;
        SheetRead.IsVisible = SheetEmpty.IsVisible = SheetClaudeButton.IsVisible = SheetEditButton.IsVisible = false;
        SheetEditor.Focus();
    }

    private void OnSheetClaudeClick(object? sender, RoutedEventArgs e)
    {
        if (_selected is { Id: { } id } card && SheetHeading(card) is { } heading)
        {
            ClaudeRequested?.Invoke(TaskSheet.Request(id, heading));
        }
    }

    private async void OnSheetSaveClick(object? sender, RoutedEventArgs e) => await SaveSheetAsync();

    private void OnSheetCancelClick(object? sender, RoutedEventArgs e) => _ = CancelSheetEditAsync();

    private async void OnSheetEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            await SaveSheetAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            await CancelSheetEditAsync();
        }
    }

    internal async Task SaveSheetAsync()
    {
        if (_sheetEdit is not { } edit || _selected is not { } card)
        {
            return;
        }

        var text = SheetEditor.Text ?? "";
        if (await WriteAsync(() => TaskSheet.Write(SheetPath(edit.Id), edit.Id, card.Title, edit.Heading, edit.Original, text)))
        {
            StopEditingSheet();
            LoadSheet(card);
        }
    }

    /// <summary>Annulla / Esc: with unsaved changes, asks before throwing them away. False when the user kept them.</summary>
    private async Task<bool> CancelSheetEditAsync()
    {
        if (_sheetEdit is null)
        {
            return true;
        }

        if (SheetDirty && !await Settings.Dialogs.ConfirmAsync(this, "Ci sono modifiche non salvate alla scheda del task. Le scarto?", "Scarta le modifiche"))
        {
            SheetEditor.Focus();
            return false;
        }

        StopEditingSheet();
        if (_selected is { } card)
        {
            LoadSheet(card);
        }

        return true;
    }

    private void StopEditingSheet()
    {
        _sheetEdit = null;
        SheetEdit.IsVisible = false;
    }

    /// <summary>✕ or Esc on the side panel: an unsaved sheet is asked about first.</summary>
    private async Task CloseDetailAsync()
    {
        if (await CancelSheetEditAsync())
        {
            CloseDetail();
        }
    }
}
