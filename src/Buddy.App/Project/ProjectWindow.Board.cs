using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Buddy.Core.Vault;

namespace Buddy.App.Project;

/// <summary>
/// Board extras: dragging a card to another column (written to the vault) or onto "Chiudi" / "Rifiuta" (sent to
/// Claude as /chiudi or /rifiuta), reminders on a card ("ricordamelo il…"),
/// and estimate against reality in the side panel.
/// </summary>
public partial class ProjectWindow
{
    private PointerPressedEventArgs? _dragPress;
    private Point _dragStart;
    private CardView? _dragCard;
    private Border? _dropHighlight;

    private void SetUpBoardDrag()
    {
        BoardColumns.AddHandler(PointerPressedEvent, OnCardPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        BoardColumns.AddHandler(PointerMovedEvent, OnCardPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        BoardColumns.AddHandler(PointerReleasedEvent, (_, _) => _dragPress = null, RoutingStrategies.Tunnel, handledEventsToo: true);

        // On the whole page: the columns and, under them while dragging, the "close" and "refuse" zones.
        BoardPage.AddHandler(DragDrop.DragOverEvent, OnColumnDragOver);
        BoardPage.AddHandler(DragDrop.DragLeaveEvent, (_, _) => HighlightDrop(null));
        BoardPage.AddHandler(DragDrop.DropEvent, OnColumnDrop);
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragPress = null;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && CardUnder(e.Source) is { } card
            && BoardMoves.Movable.Contains(card.Column.Key) && card.Item.Raw.Length > 0 && !_editingCard)
        {
            _dragPress = e;
            _dragStart = e.GetPosition(this);
            _dragCard = card;
        }
    }

    /// <summary>A press that moves a few pixels becomes a drag; a still click still opens the side panel.</summary>
    private async void OnCardPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragPress is not { } press || _dragCard is null)
        {
            return;
        }

        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 8)
        {
            return;
        }

        _dragPress = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText("buddy-card"));
        ShowCloseZones(_dragCard);
        try
        {
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
        }
        finally
        {
            HighlightDrop(null);
            CloseZones.IsVisible = false;
            _dragCard = null;
        }
    }

    /// <summary>While dragging a task: "✅ Chiudi" and "🚫 Rifiuta" under the columns, when the card can go there.</summary>
    private void ShowCloseZones(CardView card)
    {
        var close = card.HasId && BoardMoves.ClosingSkill(card.Column.Key, "fatti") is { } c && _skills.Contains(c);
        var refuse = card.HasId && BoardMoves.ClosingSkill(card.Column.Key, "rifiutati") is { } r && _skills.Contains(r);
        CloseZoneDone.IsVisible = close;
        CloseZoneRejected.IsVisible = refuse;
        CloseZones.IsVisible = close || refuse;
    }

    /// <summary>Dropped where it already is: its own column, or Da fare for a ⏸ task (it is shown there).</summary>
    private static bool SameColumn(string from, string to) => from == to || (from, to) is ("attesa", "todo");

    private bool CanDrop(CardView card, string to) =>
        SameColumn(card.Column.Key, to) ? false
        : to is "fatti" or "rifiutati" ? card.HasId && BoardMoves.ClosingSkill(card.Column.Key, to) is { } skill && _skills.Contains(skill)
        : BoardMoves.Refusal(card.Column.Key, to) is null;

    private void OnColumnDragOver(object? sender, DragEventArgs e)
    {
        var target = DropTargetUnder(e.Source);
        var allowed = _dragCard is { } card && target is { } t && CanDrop(card, t.Key);
        e.DragEffects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        HighlightDrop(allowed ? target!.Value.Border : null);
    }

    private async void OnColumnDrop(object? sender, DragEventArgs e)
    {
        HighlightDrop(null);
        CloseZones.IsVisible = false;
        if (_dragCard is not { } card || DropTargetUnder(e.Source) is not { } target || SameColumn(card.Column.Key, target.Key))
        {
            return;
        }

        if (target.Key is "fatti" or "rifiutati")
        {
            await CloseWithSkillAsync(card, target.Key);
            return;
        }

        if (BoardMoves.Refusal(card.Column.Key, target.Key) is { } refusal)
        {
            if (refusal.Length > 0)
            {
                await Settings.Dialogs.ShowMessageAsync(this, refusal);
            }

            return;
        }

        await MoveCardAsync(card, target.Key);
    }

    /// <summary>Moves a card to the column <paramref name="key"/> (Da fare, In attesa, In corso…), in the vault.</summary>
    private async Task MoveCardAsync(CardView card, string key)
    {
        if (_board?.Columns.FirstOrDefault(c => c.Key == key) is not { } target)
        {
            return;
        }

        if (await WriteAsync(() => BoardMoves.Move(_settings.ProjectPath, card.Column, card.Item, target)))
        {
            AfterEdit();
        }
    }

    /// <summary>
    /// A task dropped on "Chiudi" or "Rifiuta": not moved by hand but sent to Claude as /chiudi ID PR or
    /// /rifiuta ID motivo, so the closed task gets its PR, cost, documentation, and the refused one its reason.
    /// </summary>
    internal async Task CloseWithSkillAsync(CardView card, string key)
    {
        if (card.Id is not { } id || BoardMoves.ClosingSkill(card.Column.Key, key) is not { } skill)
        {
            return;
        }

        var (title, question, command) = skill == "chiudi"
            ? ("Chiudi", card.Column.Key == "controllare"
                    ? "Link o numero della PR:"
                    : $"{id} non è passato da «Da controllare». Link o numero della PR (vuoto se non ce n'è una):",
                "/chiudi {id} {input}")
            : ("Rifiuta", Flow.RejectAsk, "/rifiuta {id} {input}");
        if (await Settings.Dialogs.AskAsync(this, $"{title} {id}", question) is not { } answer)
        {
            return;
        }

        if (skill == "chiudi" && card.Column.Key != "controllare" && answer.Trim().Length == 0)
        {
            answer = "senza PR";
        }

        ClaudeRequested?.Invoke(command.Replace("{id}", id).Replace("{input}", answer.Trim()).Trim());
    }

    private void HighlightDrop(Border? border)
    {
        if (_dropHighlight == border)
        {
            return;
        }

        if (_dropHighlight is not null)
        {
            _dropHighlight.ClearValue(Border.BorderBrushProperty);
            _dropHighlight.ClearValue(Border.BorderThicknessProperty);
        }

        _dropHighlight = border;
        if (border is not null)
        {
            border.BorderBrush = (IBrush?)this.FindResource("ClaudeAccent") ?? Brushes.MediumPurple;
            border.BorderThickness = new Thickness(2);
        }
    }

    private static CardView? CardUnder(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<CardView>().FirstOrDefault();

    /// <summary>The column (its key) or close zone (Tag "fatti" / "rifiutati") under the pointer.</summary>
    private static (string Key, Border Border)? DropTargetUnder(object? source)
    {
        var border = (source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>()
            .FirstOrDefault(b => DragDrop.GetAllowDrop(b) && (b.Tag is string || b.DataContext is ColumnView));
        return border switch
        {
            { Tag: string key } => (key, border),
            { DataContext: ColumnView view } => (view.Column.Key, border),
            _ => null,
        };
    }

    // ---- Reminders --------------------------------------------------------------------------------------------

    /// <summary>"⏰ Ricordamelo…": a [promemoria:: date] field on the item; the buddy brings it back that day.</summary>
    private async void OnRemindClick(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } card || card.Item.Raw.Length == 0)
        {
            return;
        }

        var current = card.Item.Field("promemoria") ?? DateTime.Today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var answer = await Settings.Dialogs.AskAsync(this, "Promemoria",
            "Quando te la ripropongo? (AAAA-MM-GG, oppure «lunedì», «domani», «+3» giorni; vuoto per toglierlo)", current);
        DateOnly? date = answer is null ? null : Reminders.ParseDay(answer, DateOnly.FromDateTime(DateTime.Today));
        if (answer is not null && date is null)
        {
            await Settings.Dialogs.ShowMessageAsync(this, $"Non capisco la data «{answer}».");
            return;
        }

        var updated = ItemFields.Set(card.Item.Raw, "promemoria", date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "");
        if (await WriteAsync(() => VaultWriter.ReplaceBlock(FullPath(card.Column.NotePath), card.Item.Raw, updated)))
        {
            AfterEdit();
        }
    }

    // ---- Estimate against reality -----------------------------------------------------------------------------

    /// <summary>"⏱ stima 4h · 🗓 3 giorni lavorativi (2–5 nov) · 💳 7,22 $": what was planned and what happened.</summary>
    private static string? EstimateLine(CardView card, IReadOnlyList<TimelineEvent> events)
    {
        var parts = new List<string>();
        if (card.Item.Field("stima") is { } estimate)
        {
            parts.Add($"⏱ stima {estimate}");
        }

        if (events.Count > 0)
        {
            var first = events.Min(e => e.Date);
            var last = card.Column.Key is "fatti" or "controllare" && card.Item.Date is { } done ? done : events.Max(e => e.Date);
            var workdays = Enumerable.Range(0, Math.Max(1, last.DayNumber - first.DayNumber + 1))
                .Select(first.AddDays).Count(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday));
            parts.Add($"🗓 {Math.Max(1, workdays)} giorn{(workdays == 1 ? "o" : "i")} lavorativ{(workdays == 1 ? "o" : "i")} "
                      + $"({first.ToString("d MMM", Italian)}{(last != first ? $" – {last.ToString("d MMM", Italian)}" : "")})");
        }

        if (card.Item.Cost is { } cost)
        {
            parts.Add($"💳 {cost.ToString("0.00", Italian)} $");
        }

        return parts.Count <= 1 && card.Item.Field("stima") is null ? null : string.Join(" · ", parts);
    }
}
