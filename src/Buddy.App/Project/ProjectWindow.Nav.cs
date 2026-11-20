using System.Globalization;

namespace Buddy.App.Project;

/// <summary>
/// The side bar's pages in groups that fold and unfold (remembered between runs). The flow's pages stay always in sight;
/// a folded group shows its pages' numbers on its heading, and opens by itself while one of its pages is shown.
/// </summary>
public partial class ProjectWindow
{
    /// <summary>The groups, in order; "" is the flow's group, with no heading. Your pages go in <see cref="PagesGroup"/>.</summary>
    private static readonly (string Key, string Title, string[] Pages)[] NavGroups =
    [
        ("", "", ["overview", "board", "archive"]),
        ("test", "Test", ["manualtests", "webtests"]),
        ("knowledge", "Conoscenza", ["notes", "graph", "glossary", "decisions", "people", "episodes"]),
        ("trend", "Andamento", ["week", "costs", "prs"]),
        (PagesGroup, "Le tue pagine", []),
    ];

    private const string PagesGroup = "pages";
    private const string HeaderPrefix = "group:";

    /// <summary>Every page with its badge, as the last render computed them (the groups are laid out from these).</summary>
    private IReadOnlyList<NavItem> _navPages = [];

    /// <summary>A folded group open for the moment because the page on screen is in it (see <see cref="OpenGroupOf"/>).</summary>
    private string? _forcedOpenGroup;

    private static string GroupOf(string page) =>
        NavGroups.FirstOrDefault(g => g.Pages.Contains(page)).Key
        ?? (page.StartsWith(PagePrefix, StringComparison.Ordinal) ? PagesGroup : "");

    /// <summary>Lays out the side bar from <see cref="_navPages"/>, and selects the page shown. Call with _switchingView set.</summary>
    private void BuildNav()
    {
        var items = new List<NavItem>();
        foreach (var (key, title, _) in NavGroups)
        {
            var pages = _navPages.Where(p => GroupOf(p.Key) == key).ToList();
            if (pages.Count == 0)
            {
                continue;
            }

            if (key.Length == 0)
            {
                items.AddRange(pages);
                continue;
            }

            // Folded, unless the page on screen was opened from elsewhere (Ctrl+K, Ctrl+1…9, a link) while it was.
            var open = !SettingsStore.NavGroupClosed(key) || key == _forcedOpenGroup;
            items.Add(new NavItem(HeaderPrefix + key, title, open ? "" : FoldedBadge(pages), IsHeader: true, IsOpen: open));
            if (open)
            {
                items.AddRange(pages);
            }
        }

        Nav.ItemsSource = items;
        Nav.SelectedItem = items.FirstOrDefault(n => n.Key == _view);
        BuildRail();
    }

    // ---- The rail: the side bar folded to its icons -----------------------------------------------------------

    /// <summary>The narrow strip shown instead of the folded side bar: one icon per page, a gap between groups.</summary>
    private void BuildRail()
    {
        string? lastGroup = null;
        SideRailList.ItemsSource = _navPages.Where(p => p.Key != NewPageKey).Select(p =>
        {
            var group = GroupOf(p.Key);
            var item = new RailItem(p.Key, IconOf(p.Label), TextOf(p.Label), p.Badge, p.Key == _view,
                StartsGroup: lastGroup is not null && group != lastGroup);
            lastGroup = group;
            return item;
        }).ToList();
    }

    /// <summary>"🏠  Panoramica" → "🏠"; "Panoramica" → "Panoramica".</summary>
    private static string IconOf(string label) => label.Split("  ", 2)[0];

    private static string TextOf(string label) => label.Split("  ", 2) is [_, var text] ? text : label;

    private void OnRailItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Avalonia.Controls.Control)?.DataContext is RailItem item)
        {
            ShowView(item.Key);
        }
    }

    // ---- Notes list by folder ---------------------------------------------------------------------------------

    /// <summary>The notes the list shows (all, or those matching the filter), before grouping.</summary>
    private IReadOnlyList<NoteView> _shownNotes = [];

    private bool _notesFiltered;
    private bool _buildingNotes;

    /// <summary>
    /// Fills the notes list: the project's top notes, then each folder, under a heading that folds it. While filtering
    /// every folder is open (the matches must show); a folded folder still lists <paramref name="keep"/>, the note
    /// being read, so it stays selected, and <paramref name="reveal"/>, a note about to be selected. Selects
    /// <paramref name="keep"/> if it is there.
    /// </summary>
    private void BuildNotesList(NoteView? keep, NoteView? reveal = null)
    {
        _buildingNotes = true;
        try
        {
            var items = new List<object>();
            foreach (var group in _shownNotes.GroupBy(n => n.Folder, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => g.Key.Length > 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var open = _notesFiltered || !SettingsStore.NotesFolderClosed(group.Key);
                items.Add(new NoteFolderHeader(group.Key, group.Count(), open));
                items.AddRange(open ? group : group.Where(n => n.Equals(keep) || n.Equals(reveal)));
            }

            NotesList.ItemsSource = items;
            NotesList.SelectedItem = keep is not null && items.Contains(keep) ? keep : null;
        }
        finally
        {
            _buildingNotes = false;
        }

        if (!Equals(NotesList.SelectedItem, keep))
        {
            OnNoteSelectionChanged(); // the note on screen is gone (filtered out, deleted): the view follows
        }
    }

    private void ToggleNotesFolder(string folder, NoteView? keep)
    {
        if (!_notesFiltered)
        {
            SettingsStore.SetNotesFolderClosed(folder, !SettingsStore.NotesFolderClosed(folder));
        }

        BuildNotesList(keep);
    }

    /// <summary>A folded group's badge: its pages' numbers added up ("3/5" counts are kept apart: "3/5 · 4").</summary>
    private static string FoldedBadge(IEnumerable<NavItem> pages)
    {
        var badges = pages.Select(p => p.Badge).Where(b => b.Length > 0).ToList();
        var sum = badges.Select(b => int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : (int?)null)
            .OfType<int>().Sum();
        var others = badges.Where(b => !int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out _));
        return string.Join(" · ", others.Concat(sum > 0 ? [sum.ToString(CultureInfo.InvariantCulture)] : []));
    }

    /// <summary>On showing a page: if its group is folded it opens for now, until the page changes or it is folded again.</summary>
    private void OpenGroupOf(string page)
    {
        var group = GroupOf(page);
        _forcedOpenGroup = group.Length > 0 && SettingsStore.NavGroupClosed(group) ? group : null;
    }

    private void ToggleNavGroup(string headerKey)
    {
        var group = headerKey[HeaderPrefix.Length..];
        var open = !SettingsStore.NavGroupClosed(group) || group == _forcedOpenGroup;
        SettingsStore.SetNavGroupClosed(group, open);
        if (group == _forcedOpenGroup)
        {
            _forcedOpenGroup = null; // folded again by hand: it stays folded, the page stays on screen
        }

        _switchingView = true;
        BuildNav();
        _switchingView = false;
    }
}
