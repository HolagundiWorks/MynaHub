using System.Text.Json;
using Adms.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;

namespace Adms.Desktop;

/// <summary>
/// Project reports and 16:9 presentations for a project, with a materials library for mood boards.
/// </summary>
internal sealed class BoardsPage : UserControl, IPageCommands
{
    private readonly AppShell _shell;
    private readonly ComboBox _project = new() { Header = "Project", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _list = new() { Spacing = 4 };
    private readonly StackPanel _editor = new() { Spacing = 10, Visibility = Visibility.Collapsed };
    private readonly TextBlock _modeLabel = Flow.Section("Reports");
    private readonly TextBox _title = Flow.Input("Title");
    private readonly TextBox _body = Flow.Input("Text", true);
    private readonly TextBox _slideTitle = Flow.Input("Slide title");
    private readonly TextBox _slideBody = Flow.Input("Explainer", true);
    private readonly TextBox _pointer = Flow.Input("Pointer label");
    private readonly TextBlock _status = Flow.Secondary("");
    private readonly StackPanel _figures = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel _annexures = new() { Spacing = 4 };
    private readonly StackPanel _slides = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel _library = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)), Width = 960, Height = 540 };
    private readonly StackPanel _reportTools = new() { Spacing = 8 };
    private readonly StackPanel _deckTools = new() { Spacing = 8 };
    private readonly List<SlideModel> _deck = [];
    private readonly List<FigureModel> _reportFigures = [];
    private readonly List<AnnexModel> _annex = [];
    private readonly List<MaterialModel> _materials = [];
    private readonly Dictionary<string, byte[]> _bytes = new();
    private string _mode = "report";
    private string _kind = "report";
    private string _boardStatus = "draft";
    private string _tool = "arrow";
    private long? _id;
    private int _slide;
    private bool _dirty;
    private bool _drag;
    private double _x1;
    private double _y1;
    private Line? _preview;
    private MaterialModel? _armed;

    public BoardsPage(AppShell shell)
    {
        _shell = shell;
        _body.MinHeight = 140;
        _slideBody.MinHeight = 80;
        _pointer.PlaceholderText = "A short callout for the pointer";
        AutomationProperties.SetName(_canvas, "Presentation slide, 16:9");
        _canvas.PointerPressed += CanvasPressed;
        _canvas.PointerMoved += CanvasMoved;
        _canvas.PointerReleased += CanvasReleased;

        var page = new StackPanel { Spacing = 14, Margin = (Thickness)Application.Current.Resources["PageMargin"] };
        page.Children.Add(Flow.SheetMark("03.04", "Documents", "Reports, Presentations"));
        page.Children.Add(Flow.Heading("Reports, Presentations"));
        page.Children.Add(Flow.Secondary("Project reports and presentations submitted for a project. A presentation is a 16:9 landscape slide. Export writes a full-definition PDF. Present fills the screen from here."));
        page.Children.Add(_project);
        page.Children.Add(Flow.Bar(
            Flow.Command("Reports", () => SetMode("report")),
            Flow.Command("Presentations", () => SetMode("presentation")),
            Flow.Command("Materials", () => SetMode("materials")),
            Flow.Command("Project report", OpenProjectReport)));
        page.Children.Add(_modeLabel);
        page.Children.Add(_list);
        BuildEditor();
        page.Children.Add(_editor);
        Content = Flow.Scroll(page);
        _project.SelectionChanged += (_, _) => Reload();
        LoadProjects();
        LoadMaterials();
    }

    private void BuildEditor()
    {
        _editor.Children.Add(_status);
        _editor.Children.Add(_title);
        _reportTools.Children.Add(_body);
        _reportTools.Children.Add(Flow.Secondary("Add an image where the cursor is. It is kept in the text and printed with the report."));
        _reportTools.Children.Add(Flow.Bar(
            Flow.Command("Add image", () => _ = AddReportImage()),
            Flow.Command("Add PDF annexure", () => _ = AddAnnexure())));
        _reportTools.Children.Add(_figures);
        _reportTools.Children.Add(Flow.Section("Annexures"));
        _reportTools.Children.Add(_annexures);
        _editor.Children.Add(_reportTools);

        _deckTools.Children.Add(_slides);
        _deckTools.Children.Add(Flow.Bar(
            Flow.Command("Add slide", AddSlide),
            Flow.Command("Remove slide", RemoveSlide)));
        _deckTools.Children.Add(_slideTitle);
        _deckTools.Children.Add(_slideBody);
        _deckTools.Children.Add(Flow.Secondary("Drag on the slide to draw. Place a material from the library to build a mood board. The slide is 16:9."));
        _deckTools.Children.Add(Flow.Bar(
            Flow.Command("Line", () => _tool = "line"),
            Flow.Command("Arrow", () => _tool = "arrow"),
            Flow.Command("Pointer", () => _tool = "pointer"),
            Flow.Command("Remove last mark", RemoveLastMark)));
        _deckTools.Children.Add(_pointer);
        _deckTools.Children.Add(Flow.Section("Materials"));
        _deckTools.Children.Add(_library);
        var frame = new Border
        {
            BorderBrush = Flow.Ink("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = _canvas,
        };
        _deckTools.Children.Add(frame);
        _editor.Children.Add(_deckTools);
        _editor.Children.Add(Flow.Bar(
            Flow.Command("Save", Save, true),
            Flow.Command("Submit", Submit),
            Flow.Command("Reopen", Reopen),
            Flow.Command("Export PDF", Export),
            Flow.Command("Present", Present)));
    }

    private void SetMode(string mode)
    {
        _mode = mode;
        _modeLabel.Text = mode switch
        {
            "presentation" => "Presentations",
            "materials" => "Materials library",
            _ => "Reports",
        };
        _editor.Visibility = Visibility.Collapsed;
        if (mode == "materials")
            PaintMaterials();
        else
            Reload();
    }

    private void LoadProjects()
    {
        _ = _shell.Run("Loading projects…", () =>
        {
            var rows = _shell.Call("project_list", new { client_id = (long?)null, include_archived = false });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _project.Items.Clear();
                if (rows.ValueKind == JsonValueKind.Array)
                    foreach (var row in rows.EnumerateArray())
                        if (Kit.Id(row) is long id)
                            _project.Items.Add(new ComboBoxItem { Content = $"{Kit.Str(row, "client_name")} — {Kit.Str(row, "name")}", Tag = id });
                if (_project.Items.Count > 0)
                    _project.SelectedIndex = 0;
                else
                    Reload();
            });
        });
    }

    private long? ProjectId() => (_project.SelectedItem as ComboBoxItem)?.Tag is long id ? id : null;

    private void Reload()
    {
        if (_mode == "materials")
        {
            PaintMaterials();
            return;
        }
        if (ProjectId() is not long projectId)
        {
            _list.Children.Clear();
            _list.Children.Add(Flow.Secondary("Open a company with a project to submit a report or a presentation."));
            return;
        }
        var kind = _mode;
        _ = _shell.Run("Loading…", () =>
        {
            var rows = _shell.Call("board_list", new { project_id = projectId, kind });
            _shell.Window.DispatcherQueue.TryEnqueue(() => PaintList(rows));
        });
    }

    private void PaintList(JsonElement rows)
    {
        _list.Children.Clear();
        var empty = _mode == "presentation" ? "No presentations for this project yet." : "No reports submitted for this project yet.";
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
        {
            _list.Children.Add(Flow.Secondary(empty));
            return;
        }
        foreach (var row in rows.EnumerateArray())
        {
            if (Kit.Id(row) is not long id)
                continue;
            var label = $"{Kit.Str(row, "title")} · {Kit.Str(row, "status")}";
            _list.Children.Add(Flow.Command(label, () => Open(id)));
        }
    }

    private void OpenProjectReport()
    {
        if (ProjectId() is not long id)
        {
            _shell.Problem("Choose a project.");
            return;
        }
        _ = _shell.Run("Preparing the project report…", () =>
        {
            var encoded = _shell.Call("project_report", new { project_id = id }).GetString() ?? "";
            _shell.Window.DispatcherQueue.TryEnqueue(async () => await Kit.PreviewPdf(_shell, encoded, "project-report"));
        });
    }

    public void NewItem()
    {
        if (_mode == "materials")
        {
            _ = AddMaterial();
            return;
        }
        if (ProjectId() is null)
        {
            _shell.Problem("Choose a project.");
            return;
        }
        _id = null;
        _kind = _mode == "presentation" ? "presentation" : "report";
        _boardStatus = "draft";
        _title.Text = "";
        _body.Text = "";
        _reportFigures.Clear();
        _annex.Clear();
        _deck.Clear();
        _slide = 0;
        if (_kind == "presentation")
            _deck.Add(new SlideModel());
        _dirty = true;
        ShowEditor();
    }

    private void Open(long id)
    {
        _ = _shell.Run("Opening…", () =>
        {
            var detail = _shell.Call("board_get", new { id });
            var reportFigures = new List<FigureModel>();
            var deck = new List<SlideModel>();
            var annex = new List<AnnexModel>();
            if (detail.TryGetProperty("figures", out var figures))
                foreach (var figure in figures.EnumerateArray())
                    reportFigures.Add(ReadFigure(figure));
            if (detail.TryGetProperty("slides", out var slides))
                foreach (var slide in slides.EnumerateArray())
                {
                    var model = new SlideModel { Title = Kit.Str(slide, "title"), Body = Kit.Str(slide, "body") };
                    if (slide.TryGetProperty("marks", out var marks))
                        foreach (var mark in marks.EnumerateArray())
                            model.Marks.Add(new MarkModel
                            {
                                Kind = Kit.Str(mark, "kind"),
                                X1 = Num(mark, "x1"),
                                Y1 = Num(mark, "y1"),
                                X2 = Num(mark, "x2"),
                                Y2 = Num(mark, "y2"),
                                Label = Kit.Str(mark, "label"),
                            });
                    if (slide.TryGetProperty("figures", out var pictures))
                        foreach (var picture in pictures.EnumerateArray())
                            model.Figures.Add(ReadFigure(picture));
                    deck.Add(model);
                }
            if (detail.TryGetProperty("annexures", out var files))
                foreach (var file in files.EnumerateArray())
                    if (Kit.Id(file) is long annexId)
                        annex.Add(new AnnexModel { Id = annexId, Name = Kit.Str(file, "filename") });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _id = id;
                _kind = Kit.Str(detail, "kind");
                _boardStatus = Kit.Str(detail, "status");
                _title.Text = Kit.Str(detail, "title");
                _body.Text = Kit.Str(detail, "body");
                _reportFigures.Clear();
                _reportFigures.AddRange(reportFigures);
                _deck.Clear();
                _deck.AddRange(deck);
                _annex.Clear();
                _annex.AddRange(annex);
                _slide = 0;
                _dirty = false;
                _mode = _kind == "presentation" ? "presentation" : "report";
                _modeLabel.Text = _mode == "presentation" ? "Presentations" : "Reports";
                ShowEditor();
            });
        });
    }

    private FigureModel ReadFigure(JsonElement figure)
    {
        var model = new FigureModel
        {
            Key = Kit.Str(figure, "file_key"),
            X = Num(figure, "x"),
            Y = Num(figure, "y"),
            W = Num(figure, "w"),
            H = Num(figure, "h"),
            Caption = Kit.Str(figure, "caption"),
        };
        if (Kit.Id(figure) is long figureId && !_bytes.ContainsKey(model.Key))
        {
            var file = _shell.Call("board_figure_open", new { id = figureId });
            try
            {
                _bytes[model.Key] = Convert.FromBase64String(Kit.Str(file, "data_base64"));
            }
            catch (FormatException)
            {
                _bytes[model.Key] = [];
            }
        }
        model.Bytes = _bytes.TryGetValue(model.Key, out var stored) ? stored : [];
        return model;
    }

    private void ShowEditor()
    {
        var draft = _boardStatus == "draft" && _shell.CanEdit;
        _editor.Visibility = Visibility.Visible;
        _status.Text = _id is null ? "Not saved yet." : _boardStatus == "submitted" ? "Submitted. Reopen it to change it." : "Draft.";
        _title.IsReadOnly = !draft;
        _body.IsReadOnly = !draft;
        _slideTitle.IsReadOnly = !draft;
        _slideBody.IsReadOnly = !draft;
        _reportTools.Visibility = _kind == "report" ? Visibility.Visible : Visibility.Collapsed;
        _deckTools.Visibility = _kind == "presentation" ? Visibility.Visible : Visibility.Collapsed;
        PaintReportFigures();
        PaintAnnexures();
        PaintSlideStrip();
        BindSlide();
        PaintLibrary();
    }

    private void CommitSlide()
    {
        if (_kind != "presentation" || _slide < 0 || _slide >= _deck.Count)
            return;
        _deck[_slide].Title = _slideTitle.Text;
        _deck[_slide].Body = _slideBody.Text;
    }

    private void BindSlide()
    {
        if (_kind != "presentation" || _deck.Count == 0)
            return;
        _slide = Math.Clamp(_slide, 0, _deck.Count - 1);
        var slide = _deck[_slide];
        _slideTitle.Text = slide.Title;
        _slideBody.Text = slide.Body;
        PaintCanvas(_canvas, slide);
    }

    private void PaintSlideStrip()
    {
        _slides.Children.Clear();
        for (var i = 0; i < _deck.Count; i++)
        {
            var index = i;
            var button = Flow.Command($"Slide {i + 1}", () =>
            {
                CommitSlide();
                _slide = index;
                BindSlide();
            });
            _slides.Children.Add(button);
        }
    }

    private void AddSlide()
    {
        if (_boardStatus != "draft" || !_shell.CanEdit)
            return;
        CommitSlide();
        _deck.Add(new SlideModel());
        _slide = _deck.Count - 1;
        _dirty = true;
        PaintSlideStrip();
        BindSlide();
    }

    private void RemoveSlide()
    {
        if (_boardStatus != "draft" || !_shell.CanEdit || _deck.Count < 2)
            return;
        _deck.RemoveAt(_slide);
        _slide = Math.Min(_slide, _deck.Count - 1);
        _dirty = true;
        PaintSlideStrip();
        BindSlide();
    }

    private void RemoveLastMark()
    {
        if (_kind != "presentation" || _slide >= _deck.Count || _deck[_slide].Marks.Count == 0 || _boardStatus != "draft")
            return;
        _deck[_slide].Marks.RemoveAt(_deck[_slide].Marks.Count - 1);
        _dirty = true;
        PaintCanvas(_canvas, _deck[_slide]);
    }

    private async Task AddReportImage()
    {
        if (_boardStatus != "draft" || !_shell.CanEdit)
            return;
        var path = await FileDialogs.PickFile(_shell.Window.Handle, ".png", ".jpg", ".jpeg");
        if (path == null)
            return;
        var bytes = await File.ReadAllBytesAsync(path);
        StoreAndPlace(bytes, report: true, x: 0, y: 0);
    }

    private void StoreAndPlace(byte[] bytes, bool report, double x, double y)
    {
        string key;
        try
        {
            var stored = _shell.Call("board_store_image", new { data_base64 = Convert.ToBase64String(bytes) });
            key = Kit.Str(stored, "file_key");
        }
        catch (AdmsException ex)
        {
            _shell.Problem(ex.Message);
            return;
        }
        _bytes[key] = bytes;
        var figure = new FigureModel { Key = key, Bytes = bytes, X = x, Y = y, W = 0.32, H = 0.32 };
        if (report)
        {
            var at = _body.SelectionStart;
            _body.Text = _body.Text.Insert(at, "[[image]]");
            _reportFigures.Add(figure);
            PaintReportFigures();
        }
        else if (_slide < _deck.Count)
        {
            _deck[_slide].Figures.Add(figure);
            PaintCanvas(_canvas, _deck[_slide]);
        }
        _dirty = true;
    }

    private async Task AddAnnexure()
    {
        if (_id is not long boardId)
        {
            _shell.Problem("Save before attaching a PDF.");
            return;
        }
        if (_boardStatus != "draft" || !_shell.CanEdit)
            return;
        var path = await FileDialogs.PickFile(_shell.Window.Handle, ".pdf");
        if (path == null)
            return;
        var bytes = await File.ReadAllBytesAsync(path);
        var name = System.IO.Path.GetFileName(path);
        _ = _shell.Run("Attaching the PDF…", () =>
        {
            var id = _shell.Call("board_annexure_add", new { board_id = boardId, filename = name, data_base64 = Convert.ToBase64String(bytes) });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (id.TryGetInt64(out var annexId))
                    _annex.Add(new AnnexModel { Id = annexId, Name = name });
                PaintAnnexures();
            });
        });
    }

    private void PaintReportFigures()
    {
        _figures.Children.Clear();
        foreach (var figure in _reportFigures)
            _figures.Children.Add(Thumb(figure.Bytes, 96, string.IsNullOrWhiteSpace(figure.Caption) ? "Image" : figure.Caption));
    }

    private void PaintAnnexures()
    {
        _annexures.Children.Clear();
        if (_annex.Count == 0)
        {
            _annexures.Children.Add(Flow.Secondary("No PDF annexures yet."));
            return;
        }
        foreach (var annex in _annex)
        {
            var id = annex.Id;
            var name = annex.Name;
            _annexures.Children.Add(Flow.Command(name, () => OpenAnnexure(id, name)));
        }
    }

    private void OpenAnnexure(long id, string name)
    {
        _ = _shell.Run("Opening the annexure…", () =>
        {
            var file = _shell.Call("board_annexure_open", new { id });
            var encoded = Kit.Str(file, "data_base64");
            _shell.Window.DispatcherQueue.TryEnqueue(async () => await Kit.PreviewPdf(_shell, encoded, name));
        });
    }

    private void LoadMaterials()
    {
        _ = _shell.Run("Loading materials…", () =>
        {
            var rows = _shell.Call("material_list");
            var found = new List<MaterialModel>();
            if (rows.ValueKind == JsonValueKind.Array)
                foreach (var row in rows.EnumerateArray())
                    if (Kit.Id(row) is long id)
                        found.Add(new MaterialModel { Id = id, Name = Kit.Str(row, "name"), Category = Kit.Str(row, "category"), Key = Kit.Str(row, "file_key") });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _materials.Clear();
                _materials.AddRange(found);
                PaintLibrary();
                if (_mode == "materials")
                    PaintMaterials();
            });
        });
    }

    private void PaintLibrary()
    {
        _library.Children.Clear();
        if (_materials.Count == 0)
        {
            _library.Children.Add(Flow.Secondary("The library is empty. Add images from Materials."));
            return;
        }
        foreach (var material in _materials)
        {
            var item = material;
            _library.Children.Add(Flow.Command(string.IsNullOrWhiteSpace(item.Category) ? item.Name : $"{item.Name} · {item.Category}", () => Arm(item)));
        }
    }

    private void Arm(MaterialModel material)
    {
        _armed = material;
        _tool = "image";
        _status.Text = $"Click the slide to place {material.Name}.";
    }

    private void PaintMaterials()
    {
        _list.Children.Clear();
        _editor.Visibility = Visibility.Collapsed;
        if (_materials.Count == 0)
            _list.Children.Add(Flow.Secondary("Add photographs of materials. They can be placed on a presentation to make a mood board."));
        foreach (var material in _materials)
        {
            var item = material;
            var row = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(Flow.Secondary(string.IsNullOrWhiteSpace(item.Category) ? item.Name : $"{item.Name} · {item.Category}"));
            if (_shell.CanEdit)
                row.Children.Add(Flow.Command("Remove", () => RemoveMaterial(item.Id)));
            _list.Children.Add(row);
        }
    }

    private async Task AddMaterial()
    {
        if (!_shell.CanEdit)
            return;
        var path = await FileDialogs.PickFile(_shell.Window.Handle, ".png", ".jpg", ".jpeg");
        if (path == null)
            return;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var bytes = await File.ReadAllBytesAsync(path);
        _ = _shell.Run("Adding the material…", () =>
        {
            _shell.Call("material_add", new { name, category = "", data_base64 = Convert.ToBase64String(bytes) });
            _shell.Window.DispatcherQueue.TryEnqueue(LoadMaterials);
        });
    }

    private void RemoveMaterial(long id)
    {
        _ = _shell.Run("Removing the material…", () =>
        {
            _shell.Call("material_remove", new { id });
            _shell.Window.DispatcherQueue.TryEnqueue(LoadMaterials);
        });
    }

    private void CanvasPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_kind != "presentation" || _boardStatus != "draft" || !_shell.CanEdit || _slide >= _deck.Count)
            return;
        var point = e.GetCurrentPoint(_canvas).Position;
        var (x, y) = Norm(point.X, point.Y);
        if (_tool == "image" && _armed != null)
        {
            PlaceMaterial(_armed, x, y);
            _armed = null;
            _tool = "arrow";
            return;
        }
        _drag = true;
        _x1 = x;
        _y1 = y;
        _canvas.CapturePointer(e.Pointer);
    }

    private void CanvasMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_drag)
            return;
        var point = e.GetCurrentPoint(_canvas).Position;
        var (x, y) = Norm(point.X, point.Y);
        _preview ??= new Line { Stroke = new SolidColorBrush(Color.FromArgb(255, 26, 26, 26)), StrokeThickness = 1.5 };
        if (!_canvas.Children.Contains(_preview))
            _canvas.Children.Add(_preview);
        _preview.X1 = _x1 * _canvas.Width;
        _preview.Y1 = _y1 * _canvas.Height;
        _preview.X2 = x * _canvas.Width;
        _preview.Y2 = y * _canvas.Height;
    }

    private void CanvasReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_drag || _slide >= _deck.Count)
            return;
        _drag = false;
        _canvas.ReleasePointerCapture(e.Pointer);
        if (_preview != null)
            _canvas.Children.Remove(_preview);
        var point = e.GetCurrentPoint(_canvas).Position;
        var (x, y) = Norm(point.X, point.Y);
        if (Math.Abs(x - _x1) < 0.01 && Math.Abs(y - _y1) < 0.01)
            return;
        _deck[_slide].Marks.Add(new MarkModel
        {
            Kind = _tool is "line" or "pointer" ? _tool : "arrow",
            X1 = _x1,
            Y1 = _y1,
            X2 = x,
            Y2 = y,
            Label = _tool == "pointer" ? _pointer.Text.Trim() : "",
        });
        _dirty = true;
        PaintCanvas(_canvas, _deck[_slide]);
    }

    private void PlaceMaterial(MaterialModel material, double x, double y)
    {
        if (!_bytes.TryGetValue(material.Key, out var bytes))
        {
            try
            {
                var file = _shell.Call("material_open", new { id = material.Id });
                bytes = Convert.FromBase64String(Kit.Str(file, "data_base64"));
                _bytes[material.Key] = bytes;
            }
            catch (Exception ex) when (ex is AdmsException or FormatException)
            {
                _shell.Problem(ex.Message);
                return;
            }
        }
        _deck[_slide].Figures.Add(new FigureModel
        {
            Key = material.Key,
            Bytes = bytes,
            X = Math.Clamp(x - 0.16, 0, 0.68),
            Y = Math.Clamp(y - 0.16, 0, 0.68),
            W = 0.32,
            H = 0.32,
            Caption = material.Name,
        });
        _dirty = true;
        PaintCanvas(_canvas, _deck[_slide]);
    }

    private (double X, double Y) Norm(double x, double y)
    {
        var width = Math.Max(1, _canvas.Width);
        var height = Math.Max(1, _canvas.Height);
        return (Math.Clamp(x / width, 0, 1), Math.Clamp(y / height, 0, 1));
    }

    private void PaintCanvas(Canvas canvas, SlideModel slide)
    {
        canvas.Children.Clear();
        foreach (var figure in slide.Figures)
        {
            var image = new Image
            {
                Width = figure.W * canvas.Width,
                Height = figure.H * canvas.Height,
                Stretch = Stretch.Uniform,
            };
            _ = ShowBitmap(image, figure.Bytes);
            Canvas.SetLeft(image, figure.X * canvas.Width);
            Canvas.SetTop(image, figure.Y * canvas.Height);
            canvas.Children.Add(image);
        }
        var ink = new SolidColorBrush(Color.FromArgb(255, 26, 26, 26));
        foreach (var mark in slide.Marks)
        {
            canvas.Children.Add(new Line
            {
                X1 = mark.X1 * canvas.Width,
                Y1 = mark.Y1 * canvas.Height,
                X2 = mark.X2 * canvas.Width,
                Y2 = mark.Y2 * canvas.Height,
                Stroke = ink,
                StrokeThickness = 1.6,
            });
            if (mark.Kind == "arrow")
                canvas.Children.Add(ArrowHead(mark, canvas.Width, canvas.Height, ink));
            if (mark.Kind == "pointer")
            {
                var dot = new Ellipse { Width = 8, Height = 8, Fill = ink };
                Canvas.SetLeft(dot, mark.X2 * canvas.Width - 4);
                Canvas.SetTop(dot, mark.Y2 * canvas.Height - 4);
                canvas.Children.Add(dot);
                if (mark.Label.Length > 0)
                {
                    var label = new TextBlock { Text = mark.Label, Foreground = ink, FontSize = 12 };
                    Canvas.SetLeft(label, mark.X1 * canvas.Width + 6);
                    Canvas.SetTop(label, mark.Y1 * canvas.Height - 16);
                    canvas.Children.Add(label);
                }
            }
        }
    }

    private static Polygon ArrowHead(MarkModel mark, double width, double height, Brush ink)
    {
        var x1 = mark.X1 * width;
        var y1 = mark.Y1 * height;
        var x2 = mark.X2 * width;
        var y2 = mark.Y2 * height;
        var dx = x2 - x1;
        var dy = y2 - y1;
        var len = Math.Max(0.01, Math.Sqrt(dx * dx + dy * dy));
        var ux = dx / len;
        var uy = dy / len;
        var px = -uy;
        var py = ux;
        const double back = 12;
        const double spread = 5;
        return new Polygon
        {
            Fill = ink,
            Points =
            {
                new Windows.Foundation.Point(x2, y2),
                new Windows.Foundation.Point(x2 - ux * back + px * spread, y2 - uy * back + py * spread),
                new Windows.Foundation.Point(x2 - ux * back - px * spread, y2 - uy * back - py * spread),
            },
        };
    }

    private static UIElement Thumb(byte[] bytes, double size, string name)
    {
        var image = new Image { Width = size, Height = size, Stretch = Stretch.Uniform };
        AutomationProperties.SetName(image, name);
        _ = ShowBitmap(image, bytes);
        return image;
    }

    private static async Task ShowBitmap(Image image, byte[] bytes)
    {
        if (bytes.Length == 0)
            return;
        try
        {
            image.Source = await BrandArt.Decode(Convert.ToBase64String(bytes));
        }
        catch (Exception)
        {
            image.Source = null;
        }
    }

    public void Save()
    {
        if (!_shell.CanEdit || _boardStatus != "draft")
            return;
        if (ProjectId() is not long projectId)
        {
            _shell.Problem("Choose a project.");
            return;
        }
        if (_title.Text.Trim().Length == 0)
        {
            _shell.Problem("Give it a title.");
            return;
        }
        CommitSlide();
        var board = Payload(projectId);
        _ = _shell.Run("Saving…", () =>
        {
            var id = _shell.Call("board_save", new { board });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (id.TryGetInt64(out var saved))
                    _id = saved;
                _boardStatus = "draft";
                _dirty = false;
                _status.Text = "Draft saved.";
                Reload();
            });
        });
    }

    private object Payload(long projectId) => new
    {
        id = _id,
        project_id = projectId,
        kind = _kind,
        title = _title.Text.Trim(),
        body = _kind == "report" ? _body.Text : "",
        slides = _kind == "presentation"
            ? _deck.Select(slide => new
            {
                title = slide.Title,
                body = slide.Body,
                marks = slide.Marks.Select(mark => new { kind = mark.Kind, x1 = mark.X1, y1 = mark.Y1, x2 = mark.X2, y2 = mark.Y2, label = mark.Label }).ToArray(),
                figures = slide.Figures.Select(figure => new { file_key = figure.Key, x = figure.X, y = figure.Y, w = figure.W, h = figure.H, caption = figure.Caption }).ToArray(),
            }).ToArray()
            : Array.Empty<object>(),
        figures = _kind == "report"
            ? _reportFigures.Select(figure => new { file_key = figure.Key, x = figure.X, y = figure.Y, w = figure.W, h = figure.H, caption = figure.Caption }).ToArray()
            : Array.Empty<object>(),
    };

    private void Submit()
    {
        if (_id is not long id || _boardStatus != "draft")
        {
            _shell.Problem("Save the draft before submitting it.");
            return;
        }
        _ = _shell.Run("Submitting…", () =>
        {
            _shell.Call("board_submit", new { id });
            _shell.Window.DispatcherQueue.TryEnqueue(() => Open(id));
        });
    }

    private void Reopen()
    {
        if (_id is not long id || _boardStatus != "submitted")
            return;
        _ = _shell.Run("Reopening…", () =>
        {
            _shell.Call("board_reopen", new { id });
            _shell.Window.DispatcherQueue.TryEnqueue(() => Open(id));
        });
    }

    private void Export()
    {
        if (_id is not long id)
        {
            _shell.Problem("Save before exporting the PDF.");
            return;
        }
        var name = string.IsNullOrWhiteSpace(_title.Text) ? _kind : _title.Text.Trim();
        _ = _shell.Run("Exporting the PDF…", () =>
        {
            var encoded = _shell.Call("board_pdf", new { id }).GetString() ?? "";
            _shell.Window.DispatcherQueue.TryEnqueue(async () => await Kit.PreviewPdf(_shell, encoded, name));
        });
    }

    private void Present()
    {
        if (_kind != "presentation" || _deck.Count == 0)
        {
            _shell.Problem("Open a presentation to present it.");
            return;
        }
        CommitSlide();
        var index = Math.Clamp(_slide, 0, _deck.Count - 1);
        var canvas = new Canvas { Background = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)), Width = 960, Height = 540 };
        var title = new TextBlock { Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)), FontSize = 28, TextWrapping = TextWrapping.WrapWholeWords };
        var body = new TextBlock { Foreground = new SolidColorBrush(Color.FromArgb(255, 220, 220, 220)), FontSize = 16, TextWrapping = TextWrapping.WrapWholeWords, MaxWidth = 960 };
        var hint = new TextBlock
        {
            Text = "Left and right move between slides. Escape leaves the presentation.",
            Foreground = new SolidColorBrush(Color.FromArgb(255, 160, 160, 160)),
            FontSize = 12,
        };
        void Show()
        {
            var slide = _deck[index];
            title.Text = string.IsNullOrWhiteSpace(slide.Title) ? _title.Text : slide.Title;
            body.Text = slide.Body;
            PaintCanvas(canvas, slide);
        }
        var layout = new StackPanel { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        layout.Children.Add(title);
        layout.Children.Add(body);
        layout.Children.Add(canvas);
        layout.Children.Add(hint);
        var stage = new StageControl(layout);
        void Move(int step)
        {
            index = Math.Clamp(index + step, 0, _deck.Count - 1);
            Show();
        }
        stage.AddKey(VirtualKey.Escape, () => _shell.Window.EndShow());
        stage.AddKey(VirtualKey.Left, () => Move(-1));
        stage.AddKey(VirtualKey.Right, () => Move(1));
        stage.SizeChanged += (_, _) =>
        {
            var width = Math.Max(320, Math.Min(stage.ActualWidth - 80, (stage.ActualHeight - 180) * 16 / 9));
            canvas.Width = width;
            canvas.Height = width * 9 / 16;
            Show();
        };
        Show();
        _shell.Window.BeginShow(stage);
    }

    public void Print() => Export();
    public void FocusSearch() => _project.Focus(FocusState.Programmatic);
    public async Task<bool> CanLeave() => await Flow.Discard(this, _dirty);

    private static double Num(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.TryGetDouble(out var number) ? number : 0;

    private sealed class SlideModel
    {
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public List<MarkModel> Marks { get; } = [];
        public List<FigureModel> Figures { get; } = [];
    }

    private sealed class MarkModel
    {
        public string Kind { get; set; } = "line";
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        public string Label { get; set; } = "";
    }

    private sealed class FigureModel
    {
        public string Key { get; set; } = "";
        public byte[] Bytes { get; set; } = [];
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
        public string Caption { get; set; } = "";
    }

    private sealed class AnnexModel
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class MaterialModel
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public string Key { get; set; } = "";
    }
}

internal sealed class StageControl : UserControl
{
    public StageControl(UIElement content)
    {
        Content = content;
        Background = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0));
        IsTabStop = true;
    }

    public void AddKey(VirtualKey key, Action run)
    {
        var accelerator = new KeyboardAccelerator { Key = key };
        accelerator.Invoked += (_, args) =>
        {
            run();
            args.Handled = true;
        };
        KeyboardAccelerators.Add(accelerator);
    }
}
