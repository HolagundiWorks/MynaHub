using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Osas.Core.Suite;
using Windows.UI;

namespace Adms.LoginWidget;

/// <summary>
/// The symbol of the AI agent (docs/BRANDING.md): the ring of the suite mark, lit, on a Mirage disc. While the agent runs the ring and its glow turn through
/// the four colours of the suite (as glow tones, 2 s each) and the glow breathes; while it starts it is a steady amber; when it is not running the disc is
/// dim and the ring grey and still.
/// </summary>
internal sealed class AiOrb
{
    static readonly Color[] Tones =
    [
        Color.FromArgb(255, 0xFF, 0x5B, 0x04), // Flame Orange
        Color.FromArgb(255, 0xE4, 0xEE, 0xF0), // Wild Sand
        Color.FromArgb(255, 0x14, 0x96, 0xA0), // Deep Sea Green, lifted for a dark ground
        Color.FromArgb(255, 0x4F, 0x7C, 0x93), // Mirage, lifted
    ];

    readonly Grid _root;
    readonly Ellipse _glow;
    readonly Ellipse _ring;
    readonly SolidColorBrush _ringBrush = new(Tones[0]);
    readonly SolidColorBrush _glowBrush = new(Tones[0]);
    readonly System.Diagnostics.Stopwatch _clock = new();
    DispatcherQueueTimer? _timer;

    internal AiOrb(double size)
    {
        var disc = new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(Color.FromArgb(255, 0x16, 0x23, 0x2B)) };
        _glow = new Ellipse
        {
            Width = size * 0.74,
            Height = size * 0.74,
            Stroke = _glowBrush,
            StrokeThickness = size * 0.22,
            Opacity = 0,
        };
        _ring = new Ellipse
        {
            Width = size * 0.56,
            Height = size * 0.56,
            Stroke = _ringBrush,
            StrokeThickness = size * 0.085,
        };
        _root = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        _root.Children.Add(disc);
        _root.Children.Add(_glow);
        _root.Children.Add(_ring);
        SetState(ServiceLevel.Off);
    }

    internal FrameworkElement Root => _root;

    internal void SetState(ServiceLevel level)
    {
        switch (level)
        {
            case ServiceLevel.Running:
                _root.Opacity = 1;
                Turn();
                break;
            case ServiceLevel.Waiting:
                Still(Color.FromArgb(255, 0xD2, 0x99, 0x22), 0.22, 1);
                break;
            default:
                Still(Color.FromArgb(255, 0x8B, 0x94, 0x9E), 0, 0.5);
                break;
        }
    }

    void Still(Color colour, double glow, double opacity)
    {
        _timer?.Stop();
        _ringBrush.Color = colour;
        _glowBrush.Color = colour;
        _glow.Opacity = glow;
        _root.Opacity = opacity;
    }

    /// <summary>
    /// Turn the colours: a new colour every two seconds, eased from one tone to the next, and the glow breathing every four. Driven by a timer on the UI
    /// thread, so it needs no storyboard and stops the moment the agent does.
    /// </summary>
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
            var from = (int)phase;
            var t = phase - from;
            t = 0.5 - 0.5 * Math.Cos(Math.PI * t); // ease in and out
            var colour = Mix(Tones[from % Tones.Length], Tones[(from + 1) % Tones.Length], t);
            _ringBrush.Color = colour;
            _glowBrush.Color = colour;
            _glow.Opacity = 0.25 + 0.4 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * seconds / 4.0));
        };
        return timer;
    }

    static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        255,
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));
}
