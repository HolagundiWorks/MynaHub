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
using Office.Ai;
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
/// The AI assistant, floating over every page of O-Studio. Its orb glows with a halo that moves through the four app colours. Olmo 3 7B runs on this
/// computer from O-Studio (shared with O-CoreOp and O-Draft over loopback). Read-only tools only; nothing leaves the machine.
/// </summary>
internal sealed class AssistantBar : UserControl
{
    public const string ModelLabel = "Olmo 3 7B";

    private string? _model;
    private string? _problem;

    private readonly AppShell _shell;
    private readonly SuiteSettings _settings = new();
    private readonly StudioToolHost _tools;
    private readonly Border _panel = new();
    private Border _panelFrame = null!;
    private readonly List<Border> _frames = [];
    private readonly List<TextBlock> _captions = [];
    private readonly StackPanel _transcript = new() { Spacing = O.Ui.SuiteScale.StackGap };
    private readonly ScrollViewer _scroll = new() { MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _question = new() { PlaceholderText = "Ask about your practice", VerticalAlignment = VerticalAlignment.Center, MinWidth = 260 };
    private readonly Button _send = new() { Content = "Ask", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = O.Ui.SuiteScale.FontCaption };
    private Assistant? _assistant;
    private string? _assistantFor;
    private bool _busy;

    public AssistantBar(AppShell shell)
    {
        _shell = shell;
        _tools = new StudioToolHost(shell);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = O.Ui.SuiteAskChrome.BarMargin;
        MaxWidth = O.Ui.SuiteAskChrome.BarMaxWidth;

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
        var bar = new Grid { ColumnSpacing = O.Ui.SuiteScale.StackGap, Padding = new Thickness(O.Ui.SuiteScale.Space2, O.Ui.SuiteScale.Space1, O.Ui.SuiteScale.Space3, O.Ui.SuiteScale.Space1) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(logo);
        Grid.SetColumn(_question, 1);
        bar.Children.Add(_question);
        Grid.SetColumn(_send, 2);
        bar.Children.Add(_send);
        var barFrame = Frame(bar, O.Ui.SuiteAskChrome.BarCornerRadius);

        var newChat = new Button { Content = "New conversation" };
        newChat.Click += (_, _) =>
        {
            _transcript.Children.Clear();
            _assistant?.Reset();
        };
        var refresh = new Button { Content = "Check for Olmo" };
        var unload = new Button { Content = "Unload model" };
        unload.Click += (_, _) =>
        {
            LocalLlmHost.Shutdown();
            _assistant = null;
            Update();
        };
        refresh.Click += async (_, _) => await LoadModels();
        _scroll.Content = _transcript;
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { refresh, unload, newChat } };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _panel.Child = new StackPanel { Spacing = O.Ui.SuiteScale.StackGap, Padding = new Thickness(O.Ui.SuiteScale.Space3), Children = { controls, _status, _scroll } };
        _panelFrame = Frame(_panel, 12);
        _panelFrame.Margin = new Thickness(0, 0, 0, 8);
        // The whole frame hides, not just what is in it, so no empty outline is left above the bar.
        _panelFrame.Visibility = Visibility.Collapsed;

        Content = new StackPanel { Children = { _panelFrame, barFrame } };
        Update();
        Loaded += async (_, _) => await LoadModels();
    }

    private Border Frame(UIElement child, double radius)
    {
        var frame = new Border
        {
            Child = child,
            CornerRadius = new CornerRadius(radius),
            Background = Flow.Ink("SurfaceBackgroundBrush"),
            BorderBrush = Flow.Ink("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 24),
        };
        _frames.Add(frame);
        return frame;
    }

    /// <summary>Colours are looked up when a control is made, so after a change of theme the frames and captions are given the new ones.</summary>
    public void Retheme()
    {
        foreach (var frame in _frames)
        {
            frame.Background = Flow.Ink("SurfaceBackgroundBrush");
            frame.BorderBrush = Flow.Ink("BorderSubtleBrush");
        }
        foreach (var caption in _captions)
            caption.Foreground = Flow.Ink("TextSecondaryBrush");
    }

    /// <summary>The AI orb mark with a halo that slowly cycles through Ember, Dusk, Midnight and Grove.</summary>
    private static UIElement BuildGlowingLogo()
    {
        var halo = new SolidColorBrush(Color.FromArgb(255, 0xB8, 0x3E, 0x16));
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

        // 12 seconds round: Ember, Dusk, Midnight, Grove, easing between them (the colours of the AI orb).
        var colours = new[] { Color.FromArgb(255, 0xB8, 0x3E, 0x16), Color.FromArgb(255, 0x56, 0x30, 0xA0), Color.FromArgb(255, 0x10, 0x2C, 0x80), Color.FromArgb(255, 0x16, 0x60, 0x40) };
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

    private void TogglePanel() => _panelFrame.Visibility = _panelFrame.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private async Task LoadModels()
    {
        if (!LocalLlmHost.Installed)
        {
            _model = null;
            _problem = $"Olmo is not installed. Put {LocalModel.FileName} in {LocalModel.SharedDirectory} and unpack llama.cpp {LocalModel.RuntimeRelease} into {LocalModel.RuntimeDirectory} (see docs/LOCAL-MODEL.md in O-CoreOp).";
        }
        else
        {
            var (ready, problem) = await OlmoAiCoreApi.VerifyAsync();
            _model = ready && problem is null ? ModelLabel : null;
            _problem = problem;
        }
        _assistant = null;
        Update();
    }

    private void Update()
    {
        _status.Text = _problem != null
            ? _problem
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
        _panelFrame.Visibility = Visibility.Visible;
        _question.Text = "";
        Say("You", question);
        _status.Text = "Looking…";
        try
        {
            var identity = $"{_shell.CompanyPath}|{_shell.User}|{model}";
            if (_assistant == null || _assistantFor != identity)
            {
                _status.Text = "Looking…";
                _assistant = new Assistant(new OlmoAiCoreApi(), _tools, model, _shell.CompanyName, _shell.User, _shell.Role);
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
        var whoText = new TextBlock { Text = who, FontSize = O.Ui.SuiteScale.FontCaption, Foreground = Flow.Ink("TextSecondaryBrush") };
        _captions.Add(whoText);
        block.Children.Add(whoText);
        block.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        if (note != null)
            {
                var noteText = new TextBlock { Text = note, FontSize = O.Ui.SuiteScale.FontCaption, TextWrapping = TextWrapping.Wrap, Foreground = Flow.Ink("TextSecondaryBrush") };
                _captions.Add(noteText);
                block.Children.Add(noteText);
            }
        AutomationProperties.SetName(block, $"{who}: {text}");
        _transcript.Children.Add(block);
    }
}
