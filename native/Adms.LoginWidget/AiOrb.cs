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
/// The symbol of the AI agent (docs/BRANDING.md, design file brand/ai/ai-orb.html in o-ui): a dark sphere lit by four soft lobes, one in each app colour
/// (Ember upper left, Dusk upper right, Midnight lower right, Grove lower left), the suite mark in white with round ends, a halo of the same four colours
/// behind it, and four small particles orbiting. While the agent runs the sphere breathes (7.5 s), the halos swell (5 to 7 s), the mark glows (4.8 s) and the
/// particles orbit (11, 16, 20 and 26 s). Starting: still, at 60 %. Not running: still, at 30 %.
/// Drawn on a 300 unit scene like the design file, so every size scales the same way.
/// </summary>
internal sealed class AiOrb
{
    const double Scene = 300;
    const double OrbSize = 210;

    static Color C(string hex) => Color.FromArgb(255, Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));

    readonly Grid _root;
    readonly Ellipse[] _aura = new Ellipse[4];
    readonly double[] _auraPeriod = [5.2, 6.8, 7.4, 5.9];
    readonly double[] _auraPhase = [0, -2.1, -4.0, -1.2];
    readonly bool[] _auraReverse = [false, true, false, true];
    readonly ScaleTransform[] _auraScale = [new(), new(), new(), new()];
    readonly Canvas _orbHost = new() { Width = OrbSize, Height = OrbSize };
    readonly ScaleTransform _breathe = new() { ScaleX = 1, ScaleY = 1 };
    readonly Canvas _ringGlow = new() { Width = 192, Height = 192 };
    readonly Canvas _ring = new() { Width = 192, Height = 192 };
    readonly Canvas _particles = new() { Width = Scene, Height = Scene };
    readonly RotateTransform[] _spin = [new(), new(), new(), new()];
    readonly double[] _spinPeriod = [11, -16, 20, -26];
    readonly System.Diagnostics.Stopwatch _clock = new();
    DispatcherQueueTimer? _timer;

    internal AiOrb(double size)
    {
        var scene = new Canvas { Width = Scene, Height = Scene };

        // halos: a soft colour lobe each, behind the sphere
        var auraColours = new[] { "#B83E16", "#5630A0", "#102C80", "#166040" };
        var auraAt = new[] { new Point(0.38, 0.35), new Point(0.64, 0.33), new Point(0.62, 0.65), new Point(0.36, 0.66) };
        for (var i = 0; i < 4; i++)
        {
            var e = new Ellipse
            {
                Width = 240,
                Height = 240,
                Fill = Lobe(auraColours[i], 0.85, auraAt[i], 0.5),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _auraScale[i],
            };
            Canvas.SetLeft(e, (Scene - 240) / 2);
            Canvas.SetTop(e, (Scene - 240) / 2);
            _aura[i] = e;
            scene.Children.Add(e);
        }

        // the sphere: dark base, four lobes, the mark
        _orbHost.Children.Add(new Ellipse
        {
            Width = OrbSize,
            Height = OrbSize,
            Fill = new RadialGradientBrush
            {
                GradientStops = { new GradientStop { Color = C("#130F28"), Offset = 0 }, new GradientStop { Color = C("#060610"), Offset = 1 } },
            },
        });
        var lobeColours = new[] { "#D24C1A", "#5A34A8", "#122E84", "#186444" };
        var lobeAt = new[] { new Point(0.34, 0.30), new Point(0.68, 0.31), new Point(0.65, 0.70), new Point(0.32, 0.70) };
        for (var i = 0; i < 4; i++)
            _orbHost.Children.Add(new Ellipse { Width = OrbSize, Height = OrbSize, Fill = Lobe(lobeColours[i], 0.9, lobeAt[i], 0.6) });

        // the mark on the sphere: the design file's 192 grid scaled to the sphere
        AddMark(_ringGlow, glow: true);
        AddMark(_ring, glow: false);
        var markScale = new ScaleTransform { ScaleX = OrbSize / 192, ScaleY = OrbSize / 192 };
        foreach (var layer in new[] { _ringGlow, _ring })
        {
            layer.RenderTransform = markScale;
            _orbHost.Children.Add(layer);
        }

        _orbHost.RenderTransformOrigin = new Point(0.5, 0.5);
        _orbHost.RenderTransform = _breathe;
        Canvas.SetLeft(_orbHost, (Scene - OrbSize) / 2);
        Canvas.SetTop(_orbHost, (Scene - OrbSize) / 2);
        scene.Children.Add(_orbHost);

        // particles: one arm each turning about the centre, the dot at its radius
        var dotColours = new[] { "#E05A28", "#7858D0", "#2858B8", "#22A870" };
        var radii = new[] { 76.0, 68.0, 80.0, 72.0 };
        for (var i = 0; i < 4; i++)
        {
            var arm = new Canvas { Width = Scene, Height = Scene, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _spin[i] };
            var dot = new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(C(dotColours[i])) };
            Canvas.SetLeft(dot, Scene / 2 - 3.5);
            Canvas.SetTop(dot, Scene / 2 - radii[i] - 3.5);
            arm.Children.Add(dot);
            _particles.Children.Add(arm);
        }

        scene.Children.Add(_particles);
        _root = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        _root.Children.Add(new Viewbox { Width = size, Height = size, Child = scene });
        SetState(ServiceLevel.Off);
    }

    internal FrameworkElement Root => _root;

    /// <summary>A circle's fill: the colour at <paramref name="centre"/>, fading to nothing.</summary>
    static RadialGradientBrush Lobe(string hex, double alpha, Point centre, double radius)
    {
        var c = C(hex);
        return new RadialGradientBrush
        {
            Center = centre,
            GradientOrigin = centre,
            RadiusX = radius,
            RadiusY = radius,
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(0, c.R, c.G, c.B), Offset = 1 },
            },
        };
    }

    // The mark as the design file draws it (192 grid): the big C, the bowl under it and the ring, white with round ends.
    static void AddMark(Canvas layer, bool glow)
    {
        var white = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
        double extra = glow ? 10 : 0;
        layer.Opacity = glow ? 0.5 : 1;
        layer.Children.Add(Arc(new Point(96, 36), new Point(96, 156), 60, 16 + extra, white));
        layer.Children.Add(Arc(new Point(52, 96), new Point(140, 96), 44, 10 + extra, white));
        var ring = new Ellipse { Width = 32, Height = 32, Stroke = white, StrokeThickness = 4 + extra };
        Canvas.SetLeft(ring, 96 - 16);
        Canvas.SetTop(ring, 96 - 16);
        layer.Children.Add(ring);
    }

    static Path Arc(Point from, Point to, double radius, double thickness, Brush brush) => new()
    {
        Stroke = brush,
        StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
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
                _particles.Opacity = 1;
                Turn();
                break;
            case ServiceLevel.Waiting:
                Still(0.6);
                break;
            default:
                Still(0.3);
                break;
        }
    }

    void Still(double opacity)
    {
        _timer?.Stop();
        foreach (var a in _aura)
            a.Opacity = 0.4;
        foreach (var s in _auraScale)
            s.ScaleX = s.ScaleY = 1;
        _breathe.ScaleX = _breathe.ScaleY = 1;
        _ringGlow.Opacity = 0;
        _particles.Opacity = 0;
        _root.Opacity = opacity;
    }

    /// <summary>Everything breathes on its own period, as the design file has it. A timer on the UI thread drives it, so it stops the moment the agent does.</summary>
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
            var t = _clock.Elapsed.TotalSeconds;
            for (var i = 0; i < 4; i++)
            {
                var w = Wave(t, _auraPeriod[i], _auraPhase[i], _auraReverse[i]);
                _aura[i].Opacity = 0.55 + 0.45 * w;
                _auraScale[i].ScaleX = _auraScale[i].ScaleY = 1 + 0.16 * w;
                _spin[i].Angle = 360 * (t / _spinPeriod[i]);
            }

            _breathe.ScaleX = _breathe.ScaleY = 1 + 0.05 * Wave(t, 7.5, 0, false);
            _ringGlow.Opacity = 0.25 + 0.5 * Wave(t, 4.8, -1.4, false);
        };
        return timer;
    }

    /// <summary>0 to 1 and back over one period, eased; <paramref name="reverse"/> runs it the other way round, as the design file's animation-direction does.</summary>
    static double Wave(double seconds, double period, double phase, bool reverse)
    {
        var x = ((seconds + phase) % period + period) % period / period;
        if (reverse)
            x = 1 - x;
        return 0.5 - 0.5 * Math.Cos(2 * Math.PI * x);
    }
}
