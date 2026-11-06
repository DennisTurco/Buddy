using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Buddy.Core.Vault;

namespace Buddy.App.Project;

/// <summary>
/// The notes of the vault as a graph, like Obsidian's: a force layout (linked notes pull together, all notes push
/// apart), drawn by hand. Wheel zooms around the pointer, dragging the background pans, dragging a note moves it;
/// hovering a note lights up its links, clicking it opens it. Nodes grow with their links and take the colour of their
/// top folder; links to notes that do not exist yet are hollow.
/// </summary>
public sealed class NoteGraphView : Control
{
    /// <summary>Colours for the top folders, readable on light and dark backgrounds.</summary>
    private static readonly Color[] Palette =
    [
        Color.Parse("#7FA7E8"), Color.Parse("#B99BF5"), Color.Parse("#7FD1AE"), Color.Parse("#F2B544"),
        Color.Parse("#E8857A"), Color.Parse("#9CC46B"), Color.Parse("#D98BC8"), Color.Parse("#5CC2C7"),
        Color.Parse("#C9A27E"), Color.Parse("#A0A4B8"),
    ];

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Dictionary<string, Node> _byId = new(StringComparer.OrdinalIgnoreCase);
    private List<Node> _nodes = [];
    private List<(Node A, Node B)> _edges = [];
    private double _alpha;
    private double _zoom = 1;
    private Vector _pan;
    private Node? _hover;
    private Node? _drag;
    private Node? _focus;
    private Point _pressPoint;
    private Point _lastPoint;
    private bool _panning;
    private bool _moved;
    private bool _fitPending = true;
    private string _search = "";

    public NoteGraphView()
    {
        ClipToBounds = true;
        Focusable = true;
        _timer.Tick += (_, _) => Step();
    }

    /// <summary>A note was clicked: its relative path in the vault.</summary>
    public event Action<string>? NoteActivated;

    /// <summary>Folder → colour, for the legend.</summary>
    public IReadOnlyList<(string Folder, Color Color)> Legend { get; private set; } = [];

    public int NodeCount => _nodes.Count;

    public int EdgeCount => _edges.Count;

    /// <summary>Notes whose title contains this are highlighted, the others fade.</summary>
    public string Search
    {
        get => _search;
        set
        {
            _search = value?.Trim() ?? "";
            InvalidateVisual();
        }
    }

    /// <summary>Shows <paramref name="graph"/>; notes already on screen keep their place, new ones appear next to their links.</summary>
    /// <param name="byTask">Colour by the task each note is about (grey: notes about no single task) instead of by folder.</param>
    public void SetGraph(NoteGraph graph, bool showOrphans, bool showMissing, bool byTask = false)
    {
        var visible = graph.Nodes.Where(n => (showMissing || !n.IsMissing) && (showOrphans || n.Degree > 0)).ToList();
        var ids = visible.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edges = graph.Edges.Where(e => ids.Contains(e.From) && ids.Contains(e.To)).ToList();

        string GroupOf(NoteGraphNode n) => byTask ? n.TaskId ?? "" : n.Folder;
        var folders = visible.Where(n => !n.IsMissing).Select(GroupOf).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f.Length == 0 ? "" : f, StringComparer.OrdinalIgnoreCase).ToList();
        var colors = folders.Select((f, i) => (f, Palette[i % Palette.Length])).ToList();
        if (byTask)
        {
            // Notes about no single task (flow notes, index…) stay neutral, so the tasks stand out.
            colors = colors.Select(c => c.f.Length == 0 ? (c.f, Color.Parse("#A0A4B8")) : c).ToList();
        }

        Legend = colors.Select(c => (c.f.Length == 0 ? (byTask ? "(più task / generali)" : "(radice)") : c.f, c.Item2)).ToList();
        var colorOf = colors.ToDictionary(c => c.f, c => c.Item2, StringComparer.OrdinalIgnoreCase);

        var random = new Random(42);
        var nodes = new List<Node>();
        var placedNew = 0;
        foreach (var data in visible)
        {
            if (!_byId.TryGetValue(data.Id, out var node))
            {
                node = new Node { Position = Spiral(_byId.Count + placedNew++) };
                _byId[data.Id] = node;
            }

            node.Data = data;
            node.Color = data.IsMissing ? Colors.Gray : colorOf.GetValueOrDefault(GroupOf(data), Palette[0]);
            node.Label = null;
            nodes.Add(node);
        }

        // Drop the ones gone from the vault.
        foreach (var gone in _byId.Keys.Except(ids, StringComparer.OrdinalIgnoreCase).ToList())
        {
            _byId.Remove(gone);
        }

        _nodes = nodes;
        _edges = edges.Select(e => (_byId[e.From], _byId[e.To])).ToList();
        foreach (var node in _nodes)
        {
            node.Neighbors.Clear();
        }

        foreach (var (a, b) in _edges)
        {
            a.Neighbors.Add(b);
            b.Neighbors.Add(a);
        }

        // New notes start next to a linked note already placed, so an update does not scramble the picture.
        foreach (var node in _nodes.Where(n => n.IsNew))
        {
            if (node.Neighbors.FirstOrDefault(n => !n.IsNew) is { } anchor)
            {
                node.Position = anchor.Position + new Vector(random.NextDouble() * 40 - 20, random.NextDouble() * 40 - 20);
            }

            node.IsNew = false;
        }

        if (_hover is not null && !_nodes.Contains(_hover))
        {
            _hover = null;
        }

        Reheat(placedNew > 0 ? 1 : 0.3);
    }

    /// <summary>Shakes the layout again (also after a filter change).</summary>
    public void Reheat(double alpha = 1)
    {
        _alpha = Math.Max(_alpha, alpha);
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    /// <summary>Lays the notes out from scratch.</summary>
    public void Relayout()
    {
        for (var i = 0; i < _nodes.Count; i++)
        {
            _nodes[i].Position = Spiral(i);
            _nodes[i].Velocity = default;
        }

        _fitPending = true;
        Reheat(1);
    }

    /// <summary>Zooms so every note is on screen.</summary>
    public void FitToView()
    {
        if (_nodes.Count == 0 || Bounds.Width <= 0)
        {
            return;
        }

        var minX = _nodes.Min(n => n.Position.X);
        var maxX = _nodes.Max(n => n.Position.X);
        var minY = _nodes.Min(n => n.Position.Y);
        var maxY = _nodes.Max(n => n.Position.Y);
        var width = Math.Max(maxX - minX, 50) + 120;
        var height = Math.Max(maxY - minY, 50) + 120;
        _zoom = Math.Clamp(Math.Min(Bounds.Width / width, Bounds.Height / height), 0.1, 2.5);
        _pan = -new Vector((minX + maxX) / 2, (minY + maxY) / 2) * _zoom;
        InvalidateVisual();
    }

    /// <summary>Centres the view on a note and highlights it with its links (from the note viewer).</summary>
    public void FocusNote(string relativePath)
    {
        if (!_byId.TryGetValue(relativePath.Replace('\\', '/'), out var node))
        {
            return;
        }

        _focus = node;
        _fitPending = false;
        _zoom = Math.Max(_zoom, 1.1);
        _pan = -(Vector)node.Position * _zoom;
        InvalidateVisual();
    }

    /// <summary>Called when the view is shown again: the layout resumes if it was still moving.</summary>
    public void Resume()
    {
        if (_alpha > MinAlpha)
        {
            _timer.Start();
        }

        InvalidateVisual();
    }

    private const double MinAlpha = 0.005;

    private static Point Spiral(int index)
    {
        // Phyllotaxis: evenly spread, deterministic starting points.
        var radius = 18 * Math.Sqrt(index + 0.5);
        var angle = index * 2.399963;
        return new Point(radius * Math.Cos(angle), radius * Math.Sin(angle));
    }

    private void Step()
    {
        if (!IsEffectivelyVisible)
        {
            _timer.Stop();
            return;
        }

        if (_alpha < MinAlpha && _drag is null)
        {
            _timer.Stop();
            return;
        }

        // Repulsion between every pair (fine for vaults of a few hundred notes), springs on the links, a mild pull
        // to the centre so separate groups do not drift away.
        var count = _nodes.Count;
        var forces = new Vector[count];
        for (var i = 0; i < count; i++)
        {
            var a = _nodes[i];
            for (var j = i + 1; j < count; j++)
            {
                var b = _nodes[j];
                var delta = a.Position - b.Position;
                var distanceSquared = Math.Max(delta.X * delta.X + delta.Y * delta.Y, 25);
                if (distanceSquared > 250_000)
                {
                    continue;
                }

                var push = delta * (1800 / distanceSquared / Math.Sqrt(distanceSquared));
                forces[i] += push;
                forces[j] -= push;
            }
        }

        var index = new Dictionary<Node, int>(count);
        for (var i = 0; i < count; i++)
        {
            index[_nodes[i]] = i;
        }

        foreach (var (a, b) in _edges)
        {
            var delta = b.Position - a.Position;
            var distance = Math.Max(Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y), 0.01);
            var pull = delta * ((distance - 70) / distance * 0.06);
            forces[index[a]] += pull;
            forces[index[b]] -= pull;
        }

        for (var i = 0; i < count; i++)
        {
            var node = _nodes[i];
            if (node == _drag)
            {
                node.Velocity = default;
                continue;
            }

            forces[i] -= (Vector)node.Position * 0.012;
            node.Velocity = (node.Velocity + forces[i] * _alpha) * 0.6;
            var speed = Math.Sqrt(node.Velocity.X * node.Velocity.X + node.Velocity.Y * node.Velocity.Y);
            if (speed > 30)
            {
                node.Velocity *= 30 / speed;
            }

            node.Position += node.Velocity;
        }

        _alpha *= 0.985;
        if (_fitPending && _alpha < 0.08)
        {
            _fitPending = false;
            FitToView();
        }

        InvalidateVisual();
    }

    // ---- Drawing ----------------------------------------------------------------------------------------------

    private Point ToScreen(Point world) => new(world.X * _zoom + Bounds.Width / 2 + _pan.X, world.Y * _zoom + Bounds.Height / 2 + _pan.Y);

    private Point ToWorld(Point screen) => new((screen.X - Bounds.Width / 2 - _pan.X) / _zoom, (screen.Y - Bounds.Height / 2 - _pan.Y) / _zoom);

    private static double Radius(Node node) => 4 + Math.Sqrt(node.Data.Degree) * 2.2;

    private IBrush Resource(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Transparent, bounds); // the whole area takes the pointer

        var ink = Resource("Ink", Brushes.Black);
        var muted = Resource("Muted", Brushes.Gray);
        var accent = Resource("ClaudeAccent", Brushes.MediumPurple);
        var mutedColor = (muted as ISolidColorBrush)?.Color ?? Colors.Gray;
        var edgePen = new Pen(new SolidColorBrush(mutedColor, 0.35), 1);
        var fadedEdgePen = new Pen(new SolidColorBrush(mutedColor, 0.08), 1);
        var hotEdgePen = new Pen(accent, 1.8);

        var highlight = _hover ?? _focus;
        var searching = _search.Length > 0;
        bool Matches(Node n) => n.Data.Title.Contains(_search, StringComparison.CurrentCultureIgnoreCase);
        bool Lit(Node n) => highlight is null ? !searching || Matches(n) : n == highlight || highlight.Neighbors.Contains(n);

        foreach (var (a, b) in _edges)
        {
            var hot = highlight is not null && (a == highlight || b == highlight);
            var pen = hot ? hotEdgePen : highlight is not null || searching ? fadedEdgePen : edgePen;
            context.DrawLine(pen, ToScreen(a.Position), ToScreen(b.Position));
        }

        var typeface = new Typeface(FontFamily.Default);
        foreach (var node in _nodes)
        {
            var center = ToScreen(node.Position);
            var radius = Radius(node) * Math.Clamp(_zoom, 0.6, 1.6);
            if (center.X < -50 || center.Y < -50 || center.X > bounds.Width + 50 || center.Y > bounds.Height + 50)
            {
                continue;
            }

            var lit = Lit(node);
            var opacity = lit ? 1 : 0.18;
            if (node.Data.IsMissing)
            {
                context.DrawEllipse(null, new Pen(new SolidColorBrush(Colors.Gray, opacity), 1.4), center, radius, radius);
            }
            else
            {
                context.DrawEllipse(new SolidColorBrush(node.Color, opacity), null, center, radius, radius);
            }

            if (node == highlight || (searching && Matches(node) && highlight is null))
            {
                context.DrawEllipse(null, new Pen(ink, 1.5), center, radius + 3, radius + 3);
            }

            // Labels: always for the highlighted note and its links, otherwise when zoomed in or for the hubs.
            var showLabel = node == highlight || (highlight?.Neighbors.Contains(node) ?? false)
                            || (lit && (_zoom >= 0.9 || node.Data.Degree >= 6 || (searching && Matches(node))));
            if (!showLabel)
            {
                continue;
            }

            var size = node == highlight ? 13 : 11;
            if (node.Label is null || node.LabelSize != size || !ReferenceEquals(node.LabelBrush, ink))
            {
                node.Label = new FormattedText(node.Data.Title, System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, typeface, size, ink);
                node.LabelSize = size;
                node.LabelBrush = ink;
            }

            using (context.PushOpacity(lit ? 1 : 0.3))
            {
                context.DrawText(node.Label, new Point(center.X - node.Label.Width / 2, center.Y + radius + 2));
            }
        }

        if (_nodes.Count == 0)
        {
            var empty = new FormattedText("Nessuna nota collegata da mostrare.", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, 13, muted);
            context.DrawText(empty, new Point((bounds.Width - empty.Width) / 2, (bounds.Height - empty.Height) / 2));
        }
    }

    // ---- Pointer ----------------------------------------------------------------------------------------------

    private Node? HitTest(Point screen)
    {
        var world = ToWorld(screen);
        Node? best = null;
        var bestDistance = double.MaxValue;
        foreach (var node in _nodes)
        {
            var delta = node.Position - world;
            var distance = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
            var reach = (Radius(node) * Math.Clamp(_zoom, 0.6, 1.6) + 4) / _zoom;
            if (distance <= reach && distance < bestDistance)
            {
                best = node;
                bestDistance = distance;
            }
        }

        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        _pressPoint = _lastPoint = point;
        _moved = false;
        _drag = HitTest(point);
        _panning = _drag is null;
        _focus = null;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _pressPoint.X) + Math.Abs(point.Y - _pressPoint.Y) > 4)
        {
            _moved = true;
        }

        if (_drag is { } drag)
        {
            drag.Position = ToWorld(point);
            Reheat(0.3);
        }
        else if (_panning)
        {
            _pan += point - _lastPoint;
            InvalidateVisual();
        }
        else
        {
            var hover = HitTest(point);
            if (hover != _hover)
            {
                _hover = hover;
                Cursor = hover is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
                ToolTip.SetTip(this, hover is null ? null
                    : hover.Data.IsMissing ? $"{hover.Data.Title} (nota non ancora creata)"
                    : $"{hover.Data.Id}\n{hover.Data.Degree} collegament{(hover.Data.Degree == 1 ? "o" : "i")}");
                InvalidateVisual();
            }
        }

        _lastPoint = point;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is { } node && !_moved && !node.Data.IsMissing)
        {
            NoteActivated?.Invoke(node.Data.Id);
        }

        _drag = null;
        _panning = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is not null && _drag is null)
        {
            _hover = null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var point = e.GetPosition(this);
        var world = ToWorld(point);
        _zoom = Math.Clamp(_zoom * Math.Pow(1.15, e.Delta.Y), 0.1, 4);

        // Keep the point under the pointer where it is.
        var after = ToScreen(world);
        _pan += point - after;
        _fitPending = false;
        InvalidateVisual();
        e.Handled = true;
    }

    private sealed class Node
    {
        public NoteGraphNode Data { get; set; } = null!;
        public Point Position { get; set; }
        public Vector Velocity { get; set; }
        public Color Color { get; set; }
        public bool IsNew { get; set; } = true;
        public HashSet<Node> Neighbors { get; } = [];
        public FormattedText? Label { get; set; }
        public double LabelSize { get; set; }
        public IBrush? LabelBrush { get; set; }
    }
}
