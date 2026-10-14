using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;

namespace Adms.Desktop;

internal sealed class PdfViewer : UserControl
{
    private readonly Action _close;
    private readonly ScrollViewer _scroll = new();
    private readonly StackPanel _pages = new() { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24, 16, 24, 24) };
    private readonly TextBlock _title = Flow.Heading("PDF preview");
    private readonly TextBlock _status;
    private readonly TextBlock _zoomLabel = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 64, TextAlignment = TextAlignment.Center, Style = Flow.Look("BodyStyle") };
    private InMemoryRandomAccessStream? _stream;
    private PdfDocument? _document;
    private byte[] _bytes = [];
    private string _name = "document";
    private MainWindow? _window;
    private double _zoom = 1;
    private bool _rendering;

    public PdfViewer(Action close)
    {
        _close = close;
        _status = Flow.Secondary("Preparing the preview…");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(16, 12, 16, 8) };
        bar.Children.Add(ActionButton("Close", "Close preview", () => _close()));
        bar.Children.Add(ActionButton("Save a copy", "Save a copy of this PDF", Save));
        bar.Children.Add(ActionButton("Smaller", "Zoom out", () => _ = ChangeZoom(-0.25)));
        bar.Children.Add(_zoomLabel);
        bar.Children.Add(ActionButton("Larger", "Zoom in", () => _ = ChangeZoom(0.25)));
        bar.Children.Add(ActionButton("Fit width", "Fit the page to the window", () => _ = FitWidth()));
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new StackPanel { Spacing = 4, Margin = new Thickness(16, 8, 16, 0) };
        heading.Children.Add(_title);
        heading.Children.Add(Flow.Secondary("The pages below are rendered from the PDF. Save a copy if you want the file."));
        root.Children.Add(heading);
        Grid.SetRow(bar, 1);
        root.Children.Add(bar);
        _status.Margin = new Thickness(16, 0, 16, 8);
        Grid.SetRow(_status, 2);
        root.Children.Add(_status);
        _scroll.Content = _pages;
        Grid.SetRow(_scroll, 3);
        root.Children.Add(_scroll);
        Content = root;
        IsTabStop = true;
        AutomationProperties.SetName(this, "PDF preview");
        KeyDown += OnKey;
        UpdateZoomLabel();
    }

    public async Task Load(MainWindow window, byte[] pdf, string name)
    {
        _window = window;
        _bytes = pdf;
        _name = string.IsNullOrWhiteSpace(name) ? "document" : name;
        _title.Text = _name;
        _zoom = 1;
        UpdateZoomLabel();
        ClearDocument();
        _status.Text = "Opening the PDF…";
        if (pdf.Length < 5 || pdf[0] != (byte)'%' || pdf[1] != (byte)'P')
        {
            _status.Text = "This file is not a PDF.";
            return;
        }
        try
        {
            var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream);
            writer.WriteBytes(pdf);
            await writer.StoreAsync();
            writer.DetachStream();
            writer.Dispose();
            stream.Seek(0);
            _stream = stream;
            _document = await PdfDocument.LoadFromStreamAsync(stream);
        }
        catch (Exception ex)
        {
            _status.Text = "The PDF could not be opened. " + ex.Message;
            return;
        }
        await Render();
        Focus(FocusState.Programmatic);
    }

    public void Clear()
    {
        _pages.Children.Clear();
        ClearDocument();
        _bytes = [];
        _status.Text = "";
    }

    public async void Save()
    {
        if (_bytes.Length == 0 || _window == null)
            return;
        StorageFile? file;
        try { file = await FileDialogs.PickPdfDestination(_window.Handle, _name); }
        catch (Exception ex) { _window.ShowProblem("The save picker could not be opened. " + ex.Message); return; }
        if (file == null)
            return;
        try
        {
            await FileIO.WriteBytesAsync(file, _bytes);
            _status.Text = "Saved.";
        }
        catch (Exception ex)
        {
            _window.ShowProblem("The PDF could not be saved. " + ex.Message);
        }
    }

    private async Task Render()
    {
        if (_document == null || _rendering)
            return;
        _rendering = true;
        _pages.Children.Clear();
        var count = _document.PageCount;
        _status.Text = "Rendering…";
        try
        {
            for (uint i = 0; i < count; i++)
            {
                using var page = _document.GetPage(i);
                var pixelWidth = (uint)Math.Clamp(page.Size.Width * _zoom * 2, 1, 4096);
                var ras = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(ras, new PdfPageRenderOptions { DestinationWidth = pixelWidth });
                ras.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(ras);
                var image = new Image
                {
                    Source = bitmap,
                    Width = Math.Max(1, page.Size.Width * _zoom),
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                AutomationProperties.SetName(image, $"Page {i + 1} of {count}");
                _pages.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                    BorderBrush = Flow.Ink("DividerStrokeColorDefaultBrush"),
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Child = image,
                });
            }
            _status.Text = count == 1 ? "1 page." : $"{count} pages.";
        }
        catch (Exception ex)
        {
            _status.Text = "A page could not be drawn. " + ex.Message;
        }
        finally
        {
            _rendering = false;
        }
    }

    private async Task ChangeZoom(double delta)
    {
        var next = Math.Clamp(_zoom + delta, 0.5, 2.5);
        if (Math.Abs(next - _zoom) < 0.01)
            return;
        _zoom = next;
        UpdateZoomLabel();
        await Render();
    }

    private async Task FitWidth()
    {
        if (_document == null || _document.PageCount == 0)
            return;
        using var page = _document.GetPage(0);
        var available = Math.Max(240, _scroll.ViewportWidth - 48);
        _zoom = Math.Clamp(available / Math.Max(1, page.Size.Width), 0.5, 2.5);
        UpdateZoomLabel();
        await Render();
    }

    private void UpdateZoomLabel() => _zoomLabel.Text = $"{Math.Round(_zoom * 100)}%";

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            _close();
            e.Handled = true;
        }
    }

    private void ClearDocument()
    {
        _document = null;
        _stream?.Dispose();
        _stream = null;
    }

    private static Button ActionButton(string label, string name, Action action)
    {
        var button = new Button { Content = label, MinHeight = 32 };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => action();
        return button;
    }
}
