using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Osas.Core.Suite;

namespace Adms.LoginWidget;

/// <summary>
/// The symbol of a service: lit in the suite colours while the service runs, amber while it waits, grey and dim when it is not running or off.
/// </summary>
internal sealed class StatusIcon
{
    readonly FontIcon? _icon;
    readonly Border? _badge;
    readonly AiOrb? _orb;

    internal StatusIcon(string glyph, double size)
    {
        _icon = new FontIcon { Glyph = glyph, FontSize = size * 0.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _badge = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Child = _icon, VerticalAlignment = VerticalAlignment.Center };
        Set(ServiceLevel.Off, "checking…");
    }

    StatusIcon(AiOrb orb)
    {
        _orb = orb;
        Set(ServiceLevel.Off, "checking…");
    }

    /// <summary>The symbol of the AI agent: the ring of the mark, glowing in turning colours while it runs.</summary>
    internal static StatusIcon Orb(double size) => new(new AiOrb(size));

    internal FrameworkElement Root => _orb?.Root ?? _badge!;

    internal void Set(ServiceLevel level, string tip)
    {
        if (_orb != null)
        {
            _orb.SetState(level);
            AutomationProperties.SetName(_orb.Root, tip);
            ToolTipService.SetToolTip(_orb.Root, tip);
            return;
        }

        var _badge = this._badge!;
        var _icon = this._icon!;
        static Windows.UI.Color Res(string key) => (Windows.UI.Color)Application.Current.Resources[key];
        switch (level)
        {
            case ServiceLevel.Running:
                _badge.Background = new SolidColorBrush(Res("SuiteAccentWash"));
                _icon.Foreground = new SolidColorBrush(Res("SuiteAccentText"));
                _icon.Opacity = 1;
                break;
            case ServiceLevel.Waiting:
                _badge.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xFF, 0xF1, 0xD0));
                _icon.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x9A, 0x67, 0x00));
                _icon.Opacity = 1;
                break;
            default:
                _badge.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                _icon.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x8B, 0x94, 0x9E));
                _icon.Opacity = 0.55;
                break;
        }

        AutomationProperties.SetName(_badge, tip);
        ToolTipService.SetToolTip(_badge, tip);
    }
}
