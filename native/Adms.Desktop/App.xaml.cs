using Microsoft.UI.Xaml;

namespace Adms.Desktop;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var colors = new ResourceDictionary { Source = new Uri("ms-appx:///Resources/Colors.xaml") };
        foreach (var themeKey in colors.ThemeDictionaries.Keys.Cast<object>().ToList())
        {
            if (colors.ThemeDictionaries[themeKey] is not ResourceDictionary incoming)
                continue;
            var target = new ResourceDictionary();
            foreach (var key in incoming.Keys.Cast<object>().ToList())
            {
                if (incoming[key] is Microsoft.UI.Xaml.Media.SolidColorBrush brush)
                    target[key] = new Microsoft.UI.Xaml.Media.SolidColorBrush(brush.Color);
            }
            Resources.ThemeDictionaries[themeKey] = target;
        }
        _window = new MainWindow();
        _window.Activate();
    }
}
