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
/// Board extras: dragging a card to another column (written to the vault), reminders on a card ("ricordamelo il…"),
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
        BoardColumns.AddHandler(DragDrop.DragOverEvent, OnColumnDragOver);
        BoardColumns.AddHandler(DragDrop.DragLeaveEvent, (_, _) => HighlightDrop(null));
        BoardColumns.AddHandler(DragDrop.DropEvent, OnColumnDrop);
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
        try
        {
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
        }
        finally
        {
            HighlightDrop(null);
            _dragCard = null;
        }
    }

    private void OnColumnDragOver(object? sender, DragEventArgs e)
    {
        var column = ColumnUnder(e.Source);
        var allowed = _dragCard is not null && column is { } target && BoardMoves.Refusal(_dragCard.Column.Key, target.View.Column.Key) is null;
        e.DragEffects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        HighlightDrop(allowed ? column!.Value.Border : null);
    }

    private async void OnColumnDrop(object? sender, DragEventArgs e)
    {
        HighlightDrop(null);
        if (_dragCard is not { } card || ColumnUnder(e.Source) is not { } column)
        {
            return;
        }

        if (BoardMoves.Refusal(card.Column.Key, column.View.Column.Key) is { } refusal)
        {
            if (refusal.Length > 0)
            {
                await Settings.Dialogs.ShowMessageAsync(this, refusal);
            }

            return;
        }

        if (await WriteAsync(() => BoardMoves.Move(_settings.ProjectPath, card.Column, card.Item, column.View.Column)))
        {
            AfterEdit();
        }
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

    private static (ColumnView View, Border Border)? ColumnUnder(object? source)
    {
        var border = (source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>()
            .FirstOrDefault(b => b.DataContext is ColumnView && DragDrop.GetAllowDrop(b));
        return border is { DataContext: ColumnView view } ? (view, border) : null;
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
