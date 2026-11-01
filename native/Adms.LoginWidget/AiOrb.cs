using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Osas.Core.Suite;
using Windows.Foundation;
using Windows.UI;

namespace Adms.LoginWidget;

/// <summary>
/// The symbol of the AI agent (docs/BRANDING.md): the logo itself, on a round Mirage tile. Its three circles (the big C, the bowl under it and the ring at the
/// centre) are each lit in a different colour of the suite, and the colours pass from one circle to the next, two seconds a step, so that at any moment
/// the three circles show three of the four colours and over eight seconds each circle has worn all four. A fainter, wider copy behind each circle is the glow,
/// and it breathes. While the agent starts, the three circles are a steady amber; when it is not running they are grey and the orb is dim.
/// </summary>
internal sealed class AiOrb
{
    // Flame Orange, Wild Sand, Deep Sea Green (lifted for a dark ground), Mirage (lifted)
    static readonly Color[] Tones =
    [
        Color.FromArgb(255, 0xFF, 0x5B, 0x04),
        Color.FromArgb(255, 0xE4, 0xEE, 0xF0),
        Color.FromArgb(255, 0x14, 0x96, 0xA0),
        Color.FromArgb(255, 0x4F, 0x7C, 0x93),
    ];

    readonly Grid _root;
    readonly SolidColorBrush[] _ink = [new(Tones[0]), new(Tones[1]), new(Tones[2])];
    readonly Canvas _glowLayer = new() { Width = 194, Height = 194, Opacity = 0 };
    readonly System.Diagnostics.Stopwatch _clock = new();
    DispatcherQueueTimer? _timer;

    internal AiOrb(double size)
    {
        var inkLayer = new Canvas { Width = 194, Height = 194 };
        AddMark(_glowLayer, wide: true);
        AddMark(inkLayer, wide: false);
        var art = new Canvas { Width = 194, Height = 194 };
        art.Children.Add(new Ellipse { Width = 194, Height = 194, Fill = new SolidColorBrush(Color.FromArgb(255, 0x16, 0x23, 0x2B)) });
        // the mark fills a little less than the disc: 74 % about its centre
        var markHost = new Canvas { Width = 194, Height = 194, RenderTransformOrigin = new Point(96 / 194.0, 96 / 194.0), RenderTransform = new ScaleTransform { ScaleX = 0.74, ScaleY = 0.74 } };
        markHost.Children.Add(_glowLayer);
        markHost.Children.Add(inkLayer);
        art.Children.Add(markHost);
        _root = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        _root.Children.Add(new Viewbox { Width = size, Height = size, Child = art });
        SetState(ServiceLevel.Off);
    }

    internal FrameworkElement Root => _root;

    // The logo as the tile draws it: three strokes round the centre (107, 96).
    void AddMark(Canvas layer, bool wide)
    {
        double extra = wide ? 16 : 0;
        layer.Children.Add(Arc(new Point(107, 27), new Point(107, 165), 69, 18 + extra, _ink[0], wide));
        layer.Children.Add(Arc(new Point(57, 96), new Point(157, 96), 50, 12 + extra, _ink[1], wide));
        var ring = new Ellipse { Width = 37, Height = 37, Stroke = _ink[2], StrokeThickness = 5 + extra, Opacity = wide ? 0.55 : 1 };
        Canvas.SetLeft(ring, 107 - 18.5);
        Canvas.SetTop(ring, 96 - 18.5);
        layer.Children.Add(ring);
    }

    static Path Arc(Point from, Point to, double radius, double thickness, Brush brush, bool wide) => new()
    {
        Stroke = brush,
        StrokeThickness = thickness,
        Opacity = wide ? 0.55 : 1,
        Data = new PathGeometry
        {
            Figures =
            {
                new PathFigure
                {
                    StartPoint = from,
                    IsClosed = false,
                    Segments = { new ArcSegment { Point = to, Size = new Size(radius, radius), SweepDirection = SweepDirection.Counterclockwise, IsLargeArc = false } },
                },
            },
        },
    };

    internal void SetState(ServiceLevel level)
    {
        switch (level)
        {
            case ServiceLevel.Running:
                _root.Opacity = 1;
                Turn();
                break;
            case ServiceLevel.Waiting:
                Still(Color.FromArgb(255, 0xD2, 0x99, 0x22), 0.2, 1);
                break;
            default:
                Still(Color.FromArgb(255, 0x8B, 0x94, 0x9E), 0, 0.5);
                break;
        }
    }

    void Still(Color colour, double glow, double opacity)
    {
        _timer?.Stop();
        foreach (var ink in _ink)
            ink.Color = colour;
        _glowLayer.Opacity = glow;
        _root.Opacity = opacity;
    }

    /// <summary>Pass the colours on, two seconds a step, eased; the glow breathes every four seconds. A timer on the UI thread drives it, so it stops the moment the agent does.</summary>
    void Turn()
    {
        if (_timer is { IsRunning: true })
            return;
        _clock.Restart();
        _timer ??= CreateTimer();
        _timer.Start();
    }

    DispatcherQueueTimer CreateTimer()
    {
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        timer.Tick += (_, _) =>
        {
            var seconds = _clock.Elapsed.TotalSeconds;
            var phase = seconds % 8.0 / 2.0;
            var step = (int)phase;
            var t = 0.5 - 0.5 * Math.Cos(Math.PI * (phase - step)); // ease in and out
            for (var circle = 0; circle < _ink.Length; circle++)
                _ink[circle].Color = Mix(Tones[(step + circle) % Tones.Length], Tones[(step + circle + 1) % Tones.Length], t);
            _glowLayer.Opacity = 0.3 + 0.5 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * seconds / 4.0));
        };
        return timer;
    }

    static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        255,
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));
}
