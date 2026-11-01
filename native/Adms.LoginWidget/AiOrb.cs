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
/// The symbol of the AI agent (docs/BRANDING.md): the suite mark in white on a molten-gold disc (radial gradient, highlight upper left). While the
/// agent runs, the disc breathes, a wider copy of the strokes glows behind them every four seconds, and four gold particles orbit the disc. Starting:
/// strokes at half white on a disc at 60 %. Not running: everything at 30 %.
/// </summary>
internal sealed class AiOrb
{
    static readonly Color[] Particle =
    [
        Color.FromArgb(255, 0xF0, 0xC8, 0x40),
        Color.FromArgb(255, 0xD4, 0x98, 0x0E),
        Color.FromArgb(255, 0xE8, 0xB0, 0x30),
        Color.FromArgb(255, 0xC8, 0x80, 0x10),
    ];

    readonly Grid _root;
    readonly Ellipse _disc;
    readonly SolidColorBrush _ink = new(Color.FromArgb(255, 255, 255, 255));
    readonly Canvas _inkLayer = new() { Width = 194, Height = 194 };
    readonly Canvas _glowLayer = new() { Width = 194, Height = 194, Opacity = 0 };
    readonly Canvas _particles = new() { Width = 194, Height = 194, Opacity = 0 };
    readonly RotateTransform[] _spin = [new(), new(), new(), new()];
    readonly System.Diagnostics.Stopwatch _clock = new();
    DispatcherQueueTimer? _timer;

    internal AiOrb(double size)
    {
        AddMark(_glowLayer, wide: true);
        AddMark(_inkLayer, wide: false);
        var art = new Canvas { Width = 194, Height = 194 };
        _disc = new Ellipse
        {
            Width = 194,
            Height = 194,
            Fill = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.35, 0.30),
                RadiusX = 0.75,
                RadiusY = 0.75,
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(255, 0xF0, 0xC8, 0x40), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(255, 0xD4, 0x98, 0x0E), Offset = 0.35 },
                    new GradientStop { Color = Color.FromArgb(255, 0x9B, 0x60, 0x00), Offset = 0.65 },
                    new GradientStop { Color = Color.FromArgb(255, 0x6B, 0x40, 0x00), Offset = 1 },
                },
            },
        };
        art.Children.Add(_disc);
        // the mark fills a little less than the disc: 74 % about its centre
        var markHost = new Canvas { Width = 194, Height = 194, RenderTransformOrigin = new Point(96 / 194.0, 96 / 194.0), RenderTransform = new ScaleTransform { ScaleX = 0.74, ScaleY = 0.74 } };
        markHost.Children.Add(_glowLayer);
        markHost.Children.Add(_inkLayer);
        art.Children.Add(markHost);
        for (var i = 0; i < Particle.Length; i++)
        {
            var dot = new Ellipse { Width = 6.4 - i * 0.8, Height = 6.4 - i * 0.8, Fill = new SolidColorBrush(Particle[i]) };
            Canvas.SetLeft(dot, 97 - dot.Width / 2);
            Canvas.SetTop(dot, 6 + i * 3);
            var arm = new Canvas { Width = 194, Height = 194, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _spin[i] };
            _spin[i].Angle = i * 90;
            arm.Children.Add(dot);
            _particles.Children.Add(arm);
        }

        art.Children.Add(_particles);
        _root = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        _root.Children.Add(new Viewbox { Width = size, Height = size, Child = art });
        SetState(ServiceLevel.Off);
    }

    internal FrameworkElement Root => _root;

    // The logo as the tile draws it: three strokes round the centre (107, 96).
    void AddMark(Canvas layer, bool wide)
    {
        double extra = wide ? 16 : 0;
        layer.Children.Add(Arc(new Point(107, 27), new Point(107, 165), 69, 18 + extra, _ink, wide));
        layer.Children.Add(Arc(new Point(57, 96), new Point(157, 96), 50, 12 + extra, _ink, wide));
        var ring = new Ellipse { Width = 37, Height = 37, Stroke = _ink, StrokeThickness = 5 + extra, Opacity = wide ? 0.55 : 1 };
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
                _ink.Opacity = 1;
                _root.Opacity = 1;
                _disc.Opacity = 1;
                _particles.Opacity = 1;
                Turn();
                break;
            case ServiceLevel.Waiting:
                Still(0.5, 0.6, 1);
                break;
            default:
                Still(1, 1, 0.3);
                break;
        }
    }

    void Still(double ink, double disc, double whole)
    {
        _timer?.Stop();
        _ink.Opacity = ink;
        _disc.Opacity = disc;
        _glowLayer.Opacity = 0;
        _particles.Opacity = 0;
        _root.Opacity = whole;
    }

    /// <summary>The disc and halo breathe every four seconds, the particles orbit (10 to 16 s a turn). A timer on the UI thread drives it, so it stops the moment the agent does.</summary>
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
            var breath = 0.5 + 0.5 * Math.Sin(2 * Math.PI * seconds / 4.0);
            _glowLayer.Opacity = 0.25 + 0.45 * breath;
            _disc.Opacity = 0.92 + 0.08 * breath;
            for (var i = 0; i < _spin.Length; i++)
                _spin[i].Angle = i * 90 + 360 * seconds / (10 + i * 2);
        };
        return timer;
    }
}
