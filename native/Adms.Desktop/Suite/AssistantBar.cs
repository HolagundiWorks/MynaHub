using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Osas.Core.Agent;
using Osas.Core.Suite;
using Windows.UI;

namespace Adms.Desktop;

/// <summary>
/// The tools the assistant may use, run through the session this person is signed in to in O-Studio. The engine checks their role for
/// every call, writes the audit entry, and only reads: the assistant sees what this person can see and changes nothing.
/// </summary>
internal sealed class StudioToolHost(AppShell shell) : IToolHost
{
    private IReadOnlyList<ToolSpec>? _tools;

    public IReadOnlyList<ToolSpec> Tools() => _tools ??= shell.Call("agent_tools").EnumerateArray()
        .Select(t => new ToolSpec(t.GetProperty("name").GetString()!, t.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "", JsonNode.Parse(t.GetProperty("input_schema").GetRawText())!))
        .ToList();

    public string Call(string name, JsonObject arguments)
    {
        try
        {
            return shell.Call("agent_call", new { name, arguments = JsonNode.Parse(arguments.ToJsonString()) }).GetRawText();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AssistantException(ex.Message);
        }
    }
}

/// <summary>
/// The AI assistant, floating over every page of O-Studio. Its logo glows and shifts between the suite's colours: black, blue, orange
/// and green. It runs a model on this computer through Ollama: no key, no account, and nothing leaves the machine. It looks things up
/// in the company file through the read-only tools, as the person signed in, and cannot change a record.
/// </summary>
internal sealed class AssistantBar : UserControl
{
    /// <summary>
    /// O-Studio's assistant is Tulu 3 from the Allen Institute for AI (built on OLMo's open training recipe), run locally by Ollama.
    /// O-CoreOp and O-Draft run their own models. Any installed tag of the family is used (for example tulu3:8b).
    /// </summary>
    public const string Family = "tulu3";

    private string? _model;

    private readonly AppShell _shell;
    private readonly SuiteSettings _settings = new();
    private readonly StudioToolHost _tools;
    private readonly Border _panel = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _transcript = new() { Spacing = Hcw.Ui.SuiteScale.StackGap };
    private readonly ScrollViewer _scroll = new() { MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _question = new() { PlaceholderText = "Ask Tulu about your practice", VerticalAlignment = VerticalAlignment.Center, MinWidth = 260 };
    private readonly Button _send = new() { Content = "Ask", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = Hcw.Ui.SuiteScale.FontCaption };
    private readonly OllamaApi _ollama = new();
    private Assistant? _assistant;
    private string? _assistantFor;
    private bool _busy;

    public AssistantBar(AppShell shell)
    {
        _shell = shell;
        _tools = new StudioToolHost(shell);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(16, 0, 16, 18);
        MaxWidth = 720;

        var logo = new Button
        {
            Content = BuildGlowingLogo(),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(logo, "Assistant");
        ToolTipService.SetToolTip(logo, "Show or hide the assistant");
        logo.Click += (_, _) => TogglePanel();

        _question.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && _send.IsEnabled)
            {
                e.Handled = true;
                _ = Ask();
            }
        };
        _send.Click += async (_, _) => await Ask();
        var bar = new Grid { ColumnSpacing = Hcw.Ui.SuiteScale.StackGap, Padding = new Thickness(Hcw.Ui.SuiteScale.Space2, Hcw.Ui.SuiteScale.Space1, Hcw.Ui.SuiteScale.Space3, Hcw.Ui.SuiteScale.Space1) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(logo);
        Grid.SetColumn(_question, 1);
        bar.Children.Add(_question);
        Grid.SetColumn(_send, 2);
        bar.Children.Add(_send);
        var barFrame = Frame(bar, 28);

        var newChat = new Button { Content = "New conversation" };
        newChat.Click += (_, _) =>
        {
            _transcript.Children.Clear();
            _assistant?.Reset();
        };
        var refresh = new Button { Content = "Check for Tulu" };
        refresh.Click += async (_, _) => await LoadModels();
        _scroll.Content = _transcript;
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { refresh, newChat } };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _panel.Child = new StackPanel { Spacing = Hcw.Ui.SuiteScale.StackGap, Padding = new Thickness(Hcw.Ui.SuiteScale.Space3), Children = { controls, _status, _scroll } };
        var panelFrame = Frame(_panel, 12);
        panelFrame.Margin = new Thickness(0, 0, 0, 8);

        Content = new StackPanel { Children = { panelFrame, barFrame } };
        Update();
        Loaded += async (_, _) => await LoadModels();
    }

    private static Border Frame(UIElement child, double radius) => new()
    {
        Child = child,
        CornerRadius = new CornerRadius(radius),
        Background = Flow.Ink("SurfaceBackgroundBrush"),
        BorderBrush = Flow.Ink("BorderSubtleBrush"),
        BorderThickness = new Thickness(1),
        Shadow = new ThemeShadow(),
        Translation = new System.Numerics.Vector3(0, 0, 24),
    };

    /// <summary>The O-Studio mark with a soft halo behind it that slowly changes colour: black, blue, orange, green.</summary>
    private static UIElement BuildGlowingLogo()
    {
        var halo = new SolidColorBrush(Color.FromArgb(255, 0x1B, 0x1C, 0x20));
        var grid = new Grid { Width = 52, Height = 52 };
        foreach (var (size, opacity) in new[] { (52.0, 0.14), (44.0, 0.22), (36.0, 0.34) })
            grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = size, Height = size, Fill = halo, Opacity = opacity, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var mark = new Image { Width = 28, Height = 28, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        try
        {
            mark.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "mark-studio.png")));
        }
        catch (Exception ex) when (ex is UriFormatException or IOException)
        {
        }
        AutomationProperties.SetAccessibilityView(mark, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        grid.Children.Add(mark);

        // 12 seconds round: each colour holds a moment and eases into the next.
        var colours = new[] { Color.FromArgb(255, 0x1B, 0x1C, 0x20), Color.FromArgb(255, 0x1F, 0x5F, 0xD1), Color.FromArgb(255, 0xF2, 0x8C, 0x28), Color.FromArgb(255, 0x2E, 0x9E, 0x5B) };
        var animation = new ColorAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(12), RepeatBehavior = RepeatBehavior.Forever };
        for (var i = 0; i <= colours.Length; i++)
            animation.KeyFrames.Add(new EasingColorKeyFrame { KeyTime = TimeSpan.FromSeconds(i * 3), Value = colours[i % colours.Length], EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        Storyboard.SetTarget(animation, halo);
        Storyboard.SetTargetProperty(animation, "Color");
        var story = new Storyboard();
        story.Children.Add(animation);
        grid.Loaded += (_, _) => story.Begin();
        grid.Unloaded += (_, _) => story.Stop();
        return grid;
    }

    private void TogglePanel() => _panel.Visibility = _panel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private async Task LoadModels()
    {
        var installed = await _ollama.InstalledModelsAsync();
        _model = installed.FirstOrDefault(n => n.StartsWith(Family, StringComparison.OrdinalIgnoreCase));
        _assistant = null;
        Update();
    }

    private void Update()
    {
        _status.Text = _model == null
            ? "The Tulu model is not available. Install Ollama on this computer, start it, and run: ollama pull tulu3. Then press Check for Tulu. Everything stays on this computer."
            : $"Working as {_shell.User} ({_shell.Role}) on {_shell.CompanyName}, with {_model} on this computer. Nothing is sent anywhere.";
        _send.IsEnabled = !_busy && _model != null;
        _question.IsEnabled = !_busy;
    }

    private async Task Ask()
    {
        var question = _question.Text.Trim();
        if (_busy || question.Length == 0 || _model is not { } model)
            return;
        _busy = true;
        Update();
        _panel.Visibility = Visibility.Visible;
        _question.Text = "";
        Say("You", question);
        _status.Text = "Looking…";
        try
        {
            var identity = $"{_shell.CompanyPath}|{_shell.User}|{model}";
            if (_assistant == null || _assistantFor != identity)
            {
                _assistant = new Assistant(_ollama, _tools, model, _shell.CompanyName, _shell.User, _shell.Role);
                _assistantFor = identity;
            }
            var assistant = _assistant;
            var answer = await Task.Run(() => assistant.AskAsync(question));
            var looked = answer.Steps.Count == 0 ? "No lookups." : "Looked at: " + string.Join(", ", answer.Steps.Select(s => s.Refused ? $"{s.Tool} (refused)" : s.Tool)) + ".";
            Say("Assistant", answer.Text, looked);
        }
        catch (AssistantException ex)
        {
            Say("Assistant", ex.Message);
        }
        catch (JsonException ex)
        {
            Say("Assistant", "The answer could not be read. " + ex.Message);
        }
        finally
        {
            _busy = false;
            Update();
            _scroll.UpdateLayout();
            _scroll.ChangeView(null, _scroll.ScrollableHeight, null);
        }
    }

    private void Say(string who, string text, string? note = null)
    {
        var block = new StackPanel { Spacing = 2 };
        block.Children.Add(new TextBlock { Text = who, FontSize = Hcw.Ui.SuiteScale.FontCaption, Foreground = Flow.Ink("TextSecondaryBrush") });
        block.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        if (note != null)
            block.Children.Add(new TextBlock { Text = note, FontSize = Hcw.Ui.SuiteScale.FontCaption, TextWrapping = TextWrapping.Wrap, Foreground = Flow.Ink("TextSecondaryBrush") });
        AutomationProperties.SetName(block, $"{who}: {text}");
        _transcript.Children.Add(block);
    }
}
