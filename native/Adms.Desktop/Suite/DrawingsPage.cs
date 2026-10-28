using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WinRT.Interop;

namespace Adms.Desktop;

/// <summary>
/// Drawings, held by the ShilpiDB engine inside the company file: vector geometry linked to a project. DXF files come in; the sheet is
/// shown from the geometry, and each pan or zoom asks the engine's spatial index for only the shapes in the window on screen, so a
/// large drawing stays quick.
/// </summary>
internal sealed class DrawingsPage : UserControl
{
    private static readonly string[] Palette = ["#1B1C20", "#C2410C", "#1F5FD1", "#15803D", "#7E22CE", "#B45309", "#0E7490", "#BE123C"];

    private readonly AppShell _shell;
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single, MinWidth = 240 };
    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Colors.White) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly StackPanel _layers = new() { Spacing = 2 };
    private readonly HashSet<string> _hiddenLayers = [];
    private readonly DispatcherTimer _redraw = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly ComboBox _project = new() { MinWidth = 200, PlaceholderText = "Project" };
    private readonly List<(long Id, string Name)> _projects = [];
    private List<JsonElement> _drawings = [];
    private long _openId;
    private string[] _layerNames = [];
    private (double X0, double Y0, double X1, double Y1)? _bounds;
    // The view: scale = screen pixels per drawing unit; (_originX, _originY) = the drawing point at the canvas's bottom-left.
    private double _scale = 1, _originX, _originY;
    private Point? _dragFrom;
    private (double X, double Y) _dragOrigin;

    public DrawingsPage(AppShell shell)
    {
        _shell = shell;
        var root = new Grid { Padding = new Thickness(16), ColumnSpacing = 16, RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Grid.SetColumnSpan(bar, 2);
        var import = new Button { Content = "Import DXF…" };
        import.Click += async (_, _) => await Import();
        var fit = new Button { Content = "Fit to window" };
        fit.Click += (_, _) => Fit();
        var delete = new Button { Content = "Delete drawing" };
        delete.Click += async (_, _) => await DeleteOpen();
        bar.Children.Add(_project);
        bar.Children.Add(import);
        bar.Children.Add(fit);
        bar.Children.Add(delete);
        root.Children.Add(bar);

        var left = new StackPanel { Spacing = 8 };
        left.Children.Add(new TextBlock { Text = "Drawings", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        left.Children.Add(_list);
        left.Children.Add(new TextBlock { Text = "Layers", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        left.Children.Add(new ScrollViewer { Content = _layers, MaxHeight = 220 });
        Grid.SetRow(left, 1);
        root.Children.Add(left);

        var right = new Grid { RowSpacing = 6 };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.Children.Add(_title);
        var frame = new Border { Child = _canvas, BorderBrush = (Brush)Application.Current.Resources["BorderSubtleBrush"], BorderThickness = new Thickness(1) };
        Grid.SetRow(frame, 1);
        right.Children.Add(frame);
        Grid.SetRow(_status, 2);
        right.Children.Add(_status);
        Grid.SetRow(right, 1);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        Content = root;

        AutomationProperties.SetName(_canvas, "Drawing sheet. Scroll to zoom, drag to move.");
        _canvas.PointerWheelChanged += OnWheel;
        _canvas.PointerPressed += (s, e) => { _dragFrom = e.GetCurrentPoint(_canvas).Position; _dragOrigin = (_originX, _originY); _canvas.CapturePointer(e.Pointer); };
        _canvas.PointerMoved += OnDrag;
        _canvas.PointerReleased += (s, e) => { _dragFrom = null; _canvas.ReleasePointerCapture(e.Pointer); };
        _canvas.SizeChanged += (_, _) =>
        {
            _canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, _canvas.ActualWidth, _canvas.ActualHeight) };
            Schedule();
        };
        _redraw.Tick += (_, _) => { _redraw.Stop(); Draw(); };
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedIndex >= 0 && _list.SelectedIndex < _drawings.Count)
                Open(_drawings[_list.SelectedIndex].GetProperty("id").GetInt64());
        };
        _project.SelectionChanged += (_, _) => LoadList();
        LoadProjects();
        LoadList();
        _status.Text = "Choose a project and import a DXF file, or open a drawing from the list.";
    }

    private void LoadProjects()
    {
        _projects.Clear();
        _project.Items.Clear();
        _project.Items.Add("All projects");
        foreach (var p in _shell.Call("project_list", new { client_id = (long?)null, include_archived = false }).EnumerateArray())
        {
            _projects.Add((p.GetProperty("id").GetInt64(), p.GetProperty("name").GetString() ?? ""));
            _project.Items.Add(_projects[^1].Name);
        }
        _project.SelectedIndex = 0;
    }

    private long? SelectedProject => _project.SelectedIndex > 0 && _project.SelectedIndex - 1 < _projects.Count ? _projects[_project.SelectedIndex - 1].Id : null;

    private void LoadList()
    {
        _drawings = _shell.Call("drawing_list", new { project_id = SelectedProject }).EnumerateArray().Select(e => e.Clone()).ToList();
        _list.Items.Clear();
        foreach (var d in _drawings)
            _list.Items.Add($"{d.GetProperty("name").GetString()}  ·  {d.GetProperty("project").GetString()}  ·  {d.GetProperty("entities").GetInt64()} shapes");
    }

    private async Task Import()
    {
        if (SelectedProject is not long project)
        {
            _shell.Problem("Choose the project the drawing belongs to first.");
            return;
        }
        var path = await FileDialogs.PickFile(WindowNative.GetWindowHandle(_shell.Window), ".dxf");
        if (path == null)
            return;
        JsonElement result = default;
        var ok = await _shell.Run("Reading the drawing…", () => result = _shell.Call("drawing_import", new { project_id = project, name = "", path }).Clone());
        if (!ok)
            return;
        LoadList();
        var skipped = result.GetProperty("skipped").GetInt64();
        _status.Text = $"Imported {result.GetProperty("entities").GetInt64()} shapes on {result.GetProperty("layers").GetInt64()} layers."
            + (skipped > 0 ? $" Not read: {string.Join(", ", result.GetProperty("skipped_kinds").EnumerateArray().Select(k => k.GetString()))}." : "");
        Open(result.GetProperty("id").GetInt64());
    }

    private async Task DeleteOpen()
    {
        if (_openId == 0)
            return;
        var confirm = await new ContentDialog { Title = "Delete this drawing?", Content = "The drawing is removed from the company file. The DXF file you imported is not touched.", PrimaryButtonText = "Delete", CloseButtonText = "Cancel", XamlRoot = XamlRoot }.ShowAsync();
        if (confirm != ContentDialogResult.Primary)
            return;
        _shell.Call("drawing_delete", new { id = _openId });
        _openId = 0;
        _canvas.Children.Clear();
        _title.Text = "";
        LoadList();
    }

    private void Open(long id)
    {
        _openId = id;
        _hiddenLayers.Clear();
        var view = _shell.Call("drawing_view", new { id, window = (double[]?)null });
        _title.Text = $"{view.GetProperty("name").GetString()}  ({view.GetProperty("units").GetString()})";
        _layerNames = view.GetProperty("layers").EnumerateArray().Select(l => l.GetProperty("name").GetString() ?? "").ToArray();
        BuildLayerList();
        if (view.GetProperty("bbox").ValueKind == JsonValueKind.Array)
        {
            var b = view.GetProperty("bbox").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            _bounds = (b[0], b[1], b[2], b[3]);
        }
        else
            _bounds = null;
        Fit();
    }

    private void BuildLayerList()
    {
        _layers.Children.Clear();
        for (var i = 0; i < _layerNames.Length; i++)
        {
            var name = _layerNames[i];
            var box = new CheckBox { Content = name, IsChecked = true, MinHeight = 28, Foreground = Brush(Palette[i % Palette.Length]) };
            box.Checked += (_, _) => { _hiddenLayers.Remove(name); Schedule(); };
            box.Unchecked += (_, _) => { _hiddenLayers.Add(name); Schedule(); };
            _layers.Children.Add(box);
        }
    }

    private void Fit()
    {
        if (_bounds is not { } b || _canvas.ActualWidth < 10 || _canvas.ActualHeight < 10)
        {
            Schedule();
            return;
        }
        var w = Math.Max(b.X1 - b.X0, 1e-6);
        var h = Math.Max(b.Y1 - b.Y0, 1e-6);
        _scale = Math.Min(_canvas.ActualWidth / w, _canvas.ActualHeight / h) * 0.95;
        _originX = b.X0 - (_canvas.ActualWidth / _scale - w) / 2;
        _originY = b.Y0 - (_canvas.ActualHeight / _scale - h) / 2;
        Schedule();
    }

    private void Schedule()
    {
        _redraw.Stop();
        _redraw.Start();
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas);
        var factor = p.Properties.MouseWheelDelta > 0 ? 1.25 : 0.8;
        var (wx, wy) = ToWorld(p.Position.X, p.Position.Y);
        _scale *= factor;
        _originX = wx - p.Position.X / _scale;
        _originY = wy - (_canvas.ActualHeight - p.Position.Y) / _scale;
        e.Handled = true;
        Schedule();
    }

    private void OnDrag(object sender, PointerRoutedEventArgs e)
    {
        if (_dragFrom is not { } from)
            return;
        var now = e.GetCurrentPoint(_canvas).Position;
        _originX = _dragOrigin.X - (now.X - from.X) / _scale;
        _originY = _dragOrigin.Y + (now.Y - from.Y) / _scale;
        Schedule();
    }

    private (double X, double Y) ToWorld(double sx, double sy) => (_originX + sx / _scale, _originY + (_canvas.ActualHeight - sy) / _scale);

    private Point ToScreen(double x, double y) => new((x - _originX) * _scale, _canvas.ActualHeight - (y - _originY) * _scale);

    private static Brush Brush(string hex) => new SolidColorBrush(Windows.UI.Color.FromArgb(255, Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16)));

    /// <summary>Asks the engine for the shapes inside the window on screen, and draws them.</summary>
    private void Draw()
    {
        if (_openId == 0 || _canvas.ActualWidth < 10)
            return;
        var (x0, y0) = ToWorld(0, _canvas.ActualHeight);
        var (x1, y1) = ToWorld(_canvas.ActualWidth, 0);
        var view = _shell.Call("drawing_view", new { id = _openId, window = new[] { x0, y0, x1, y1 } });
        _canvas.Children.Clear();
        var drawn = 0;
        foreach (var s in view.GetProperty("shapes").EnumerateArray())
        {
            var layer = s.GetProperty("layer").GetInt32();
            if (layer < _layerNames.Length && _hiddenLayers.Contains(_layerNames[layer]))
                continue;
            var brush = Brush(Palette[layer % Palette.Length]);
            switch (s.GetProperty("t").GetString())
            {
                case "poly":
                    var points = new PointCollection();
                    foreach (var p in s.GetProperty("pts").EnumerateArray())
                        points.Add(ToScreen(p[0].GetDouble(), p[1].GetDouble()));
                    if (s.GetProperty("closed").GetBoolean() && points.Count > 0)
                        points.Add(points[0]);
                    _canvas.Children.Add(new Polyline { Points = points, Stroke = brush, StrokeThickness = 1 });
                    break;
                case "arc":
                    var c = s.GetProperty("c");
                    var r = s.GetProperty("r").GetDouble();
                    double a0 = s.GetProperty("a0").GetDouble(), a1 = s.GetProperty("a1").GetDouble();
                    var steps = Math.Clamp((int)(Math.Abs(a1 - a0) * Math.Max(r * _scale, 4) / 6), 12, 180);
                    var arc = new PointCollection();
                    for (var i = 0; i <= steps; i++)
                    {
                        var a = a0 + (a1 - a0) * i / steps;
                        arc.Add(ToScreen(c[0].GetDouble() + r * Math.Cos(a), c[1].GetDouble() + r * Math.Sin(a)));
                    }
                    _canvas.Children.Add(new Polyline { Points = arc, Stroke = brush, StrokeThickness = 1 });
                    break;
                case "text":
                    var at = s.GetProperty("pts")[0];
                    var size = Math.Max(s.GetProperty("h").GetDouble() * _scale, 1);
                    if (size < 4)
                        break;
                    var label = new TextBlock { Text = s.GetProperty("text").GetString(), FontSize = Math.Min(size, 96), Foreground = brush };
                    var pos = ToScreen(at[0].GetDouble(), at[1].GetDouble());
                    Canvas.SetLeft(label, pos.X);
                    Canvas.SetTop(label, pos.Y - Math.Min(size, 96));
                    _canvas.Children.Add(label);
                    break;
                case "point":
                    var pt = s.GetProperty("pts")[0];
                    var dot = ToScreen(pt[0].GetDouble(), pt[1].GetDouble());
                    var mark = new Ellipse { Width = 4, Height = 4, Fill = brush };
                    Canvas.SetLeft(mark, dot.X - 2);
                    Canvas.SetTop(mark, dot.Y - 2);
                    _canvas.Children.Add(mark);
                    break;
            }
            drawn++;
        }
        var total = view.GetProperty("total").GetInt64();
        _status.Text = $"Showing {drawn} of {total} shapes in this window" + (view.GetProperty("truncated").GetBoolean() ? " (zoom in to see the rest)." : ".");
    }
}
