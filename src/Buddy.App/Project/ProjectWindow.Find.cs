using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using ColorTextBlock.Avalonia;

namespace Buddy.App.Project;

/// <summary>
/// Ctrl+F on a note or a checklist: finds the text in what is shown (markdown paragraphs, check boxes, plain text),
/// tints the paragraphs that contain it, scrolls to them one at a time. The global filter stays on Ctrl+Shift+F.
/// </summary>
public partial class ProjectWindow
{
    private static readonly IBrush HitBack = new SolidColorBrush(Color.Parse("#40F2B544"));
    private static readonly IBrush CurrentBack = new SolidColorBrush(Color.Parse("#90F2B544"));

    private readonly List<Control> _pageHits = [];
    private readonly Dictionary<TextBlock, IBrush?> _hitBackgrounds = [];
    private int _pageHit = -1;

    /// <summary>The views where Ctrl+F searches the page instead of filtering the lists.</summary>
    private bool PageFindAvailable => _view is "notes" or "manualtests" && _editedNote is null;

    private Control? PageFindRoot => _view switch
    {
        "notes" => NoteScroller,
        "manualtests" => ManualScroller,
        _ => null,
    };

    internal void OpenPageFind()
    {
        PageFindBox.PlaceholderText = _view == "notes" ? "Cerca nella nota…" : "Cerca nella checklist…";
        PageFindBar.IsVisible = true;
        PageFindBox.Focus();
        PageFindBox.SelectAll();
        UpdatePageFind();
    }

    private void ClosePageFind()
    {
        ClearPageHits();
        PageFindBar.IsVisible = false;
        PageFindCount.Text = "";
    }

    private void ClearPageHits()
    {
        foreach (var hit in _pageHits)
        {
            switch (hit)
            {
                case CTextBlock markdown:
                    markdown.Classes.Remove("findhit");
                    markdown.Classes.Remove("findcurrent");
                    break;
                case TextBlock text when _hitBackgrounds.TryGetValue(text, out var background):
                    text.Background = background;
                    break;
            }
        }

        _pageHits.Clear();
        _hitBackgrounds.Clear();
        _pageHit = -1;
    }

    /// <summary>Finds again (the text changed, or the page did: another note, a reload).</summary>
    private void UpdatePageFind()
    {
        ClearPageHits();
        var query = PageFindBox.Text?.Trim() ?? "";
        if (query.Length == 0 || PageFindRoot is not { } root)
        {
            PageFindCount.Text = "";
            return;
        }

        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            var text = control switch
            {
                CTextBlock markdown => markdown.Text,
                TextBlock plain when plain.FindAncestorOfType<TextBox>() is null => plain.Text,
                _ => null,
            };
            if (text is null || !text.Contains(query, StringComparison.CurrentCultureIgnoreCase) || !control.IsEffectivelyVisible)
            {
                continue;
            }

            // A markdown paragraph holds its runs as children text blocks of its own: count the paragraph only.
            if (control is TextBlock && control.FindAncestorOfType<CTextBlock>() is not null)
            {
                continue;
            }

            _pageHits.Add(control);
            switch (control)
            {
                case CTextBlock markdown:
                    markdown.Classes.Add("findhit");
                    break;
                case TextBlock plain:
                    _hitBackgrounds[plain] = plain.Background;
                    plain.Background = HitBack;
                    break;
            }
        }

        _pageHit = _pageHits.Count > 0 ? 0 : -1;
        ShowPageHit();
    }

    private void MovePageHit(int step)
    {
        if (_pageHits.Count == 0)
        {
            return;
        }

        _pageHit = (_pageHit + step + _pageHits.Count) % _pageHits.Count;
        ShowPageHit();
    }

    private void ShowPageHit()
    {
        PageFindCount.Text = _pageHits.Count == 0
            ? (string.IsNullOrWhiteSpace(PageFindBox.Text) ? "" : "0")
            : $"{_pageHit + 1}/{_pageHits.Count}";
        for (var i = 0; i < _pageHits.Count; i++)
        {
            switch (_pageHits[i])
            {
                case CTextBlock markdown:
                    markdown.Classes.Set("findcurrent", i == _pageHit);
                    break;
                case TextBlock plain:
                    plain.Background = i == _pageHit ? CurrentBack : HitBack;
                    break;
            }
        }

        if (_pageHit >= 0)
        {
            _pageHits[_pageHit].BringIntoView();
        }
    }

    private void OnPageFindKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
            case Key.Up:
                MovePageHit(-1);
                break;
            case Key.Enter:
            case Key.Down:
                MovePageHit(1);
                break;
            case Key.Escape:
                ClosePageFind();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnPageFindPreviousClick(object? sender, RoutedEventArgs e) => MovePageHit(-1);

    private void OnPageFindNextClick(object? sender, RoutedEventArgs e) => MovePageHit(1);

    private void OnPageFindCloseClick(object? sender, RoutedEventArgs e) => ClosePageFind();

    /// <summary>The page changed under an open search (another note, a reload): search it again once laid out.</summary>
    private void RefreshPageFind()
    {
        if (PageFindBar.IsVisible)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdatePageFind, Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }
}
