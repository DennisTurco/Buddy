using Avalonia.Input;
using Avalonia.Interactivity;

namespace Buddy.App.Project;

/// <summary>
/// Back and forward between the pages, like a browser: the mouse's side buttons (X1 back, X2 forward), Alt+← / Alt+→.
/// A step is a page and, in the notes view, the note that was open.
/// </summary>
public partial class ProjectWindow
{
    /// <summary>Where the window was: the view, and the note open in the notes view.</summary>
    internal sealed record Location(string View, string? Note);

    private const int HistoryMax = 50;

    private readonly List<Location> _back = [];
    private readonly List<Location> _forward = [];
    private bool _restoring;
    private bool _selectingNote;
    private string? _currentNote;

    /// <summary>Selects a note in the notes list from code, without making it a step of the history.</summary>
    private void SelectNoteQuietly(object? note)
    {
        _selectingNote = true;
        try
        {
            if (note is NoteView view && !NotesList.Items.Contains(view))
            {
                BuildNotesList(NotesList.SelectedItem as NoteView, reveal: view); // in a folded folder: listed under its heading
            }

            NotesList.SelectedItem = note;
        }
        finally
        {
            _selectingNote = false;
        }
    }

    /// <summary>Steps back and forward (for tests).</summary>
    internal int BackCount => _back.Count;

    internal int ForwardCount => _forward.Count;

    /// <summary>The title of the page shown (for tests).</summary>
    internal string ViewTitleText => ViewTitle.Text ?? "";

    private Location Here => new(_view, _view == "notes" ? _currentNote : null);

    private void SetUpHistory()
    {
        // Tunnel: the side buttons work wherever the pointer is, even over a list or a text box.
        AddHandler(PointerPressedEvent, OnHistoryPointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnHistoryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        switch (e.GetCurrentPoint(this).Properties.PointerUpdateKind)
        {
            case PointerUpdateKind.XButton1Pressed:
                e.Handled = GoBack();
                break;
            case PointerUpdateKind.XButton2Pressed:
                e.Handled = GoForward();
                break;
        }
    }

    /// <summary>Before leaving a place: remember it, and drop the steps forward (a new branch, as in a browser).</summary>
    private void Remember(Location from)
    {
        if (_restoring || (_back.Count > 0 && _back[^1] == from))
        {
            return;
        }

        _back.Add(from);
        if (_back.Count > HistoryMax)
        {
            _back.RemoveAt(0);
        }

        _forward.Clear();
    }

    /// <summary>Called by ShowView before switching: a different view is a step.</summary>
    private void RememberBeforeView(string key)
    {
        if (key != _view)
        {
            Remember(Here);
        }
    }

    /// <summary>The note shown in the notes view changed: going from one note to another is a step too.</summary>
    private void OnNoteShown(string? relativePath)
    {
        // A note chosen by code (a link that opens it, a reload) is not a step of its own: the page left before is.
        if (!_selectingNote && _view == "notes" && _currentNote is not null && relativePath is not null && relativePath != _currentNote)
        {
            Remember(new Location("notes", _currentNote));
        }

        _currentNote = relativePath ?? _currentNote;
    }

    internal bool GoBack() => Step(_back, _forward);

    internal bool GoForward() => Step(_forward, _back);

    private bool Step(List<Location> from, List<Location> to)
    {
        if (from.Count == 0)
        {
            return false;
        }

        var target = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(Here);
        _restoring = true;
        try
        {
            ShowView(target.View);
            if (target.Note is { } note && _notes.FirstOrDefault(n => n.Note.RelativePath == note) is { } view)
            {
                SelectNoteQuietly(view);
                NotesList.ScrollIntoView(view);
            }
        }
        finally
        {
            _restoring = false;
        }

        return true;
    }
}
