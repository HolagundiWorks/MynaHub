using Hcw.Ui.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Osas.Core.Agent;
using Osas.Core.Suite;
using Windows.ApplicationModel.DataTransfer;

namespace Adms.Desktop;

/// <summary>
/// The suite's AI assistant. It asks the chosen model a question and lets it look things up in the company through the engine's
/// read-only tools, as the person signed in here, so it sees only what their role allows. It cannot change anything. The person's
/// own API key is kept encrypted for their Windows account, and nothing is sent until they have agreed to it.
/// </summary>
public sealed class AgentPanel : StackPanel
{
    private readonly SuiteSettings _settings;
    private readonly ApiKeyStore _keys = new();
    private readonly Func<SuiteCompany?> _company;
    private readonly Func<string?> _companyPath;

    private readonly TextBlock _state = new() { Style = Resource<Style>("BodyStyle"), TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _model = new() { Header = "Model", MinWidth = 340 };
    private readonly TextBlock _modelWhy = new() { Style = Resource<Style>("CaptionStyle"), TextWrapping = TextWrapping.Wrap };
    private readonly PasswordBox _key = new() { Header = "Your Anthropic API key", PlaceholderText = "Paste the key, then Save key", MinWidth = 340 };
    private readonly TextBlock _keyState = new() { Style = Resource<Style>("CaptionStyle"), TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _consent = new()
    {
        Content = "I agree that my questions and the records the assistant looks up are sent to Anthropic to get an answer.",
    };
    private readonly StackPanel _transcript = new() { Spacing = 10 };
    private readonly ScrollViewer _scroll = new() { MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _question = new() { Header = "Ask about your practice", PlaceholderText = "For example: which projects have follow-ups that are overdue?", MinWidth = 420, AcceptsReturn = false };
    private readonly Button _send = new() { Content = "Ask", Style = Resource<Style>("AccentButtonStyle") };
    private readonly Button _new = new() { Content = "New conversation" };
    private readonly TextBlock _progress = new() { Style = Resource<Style>("CaptionStyle"), TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _config = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
        MinHeight = 140,
        MaxWidth = 760,
        HorizontalAlignment = HorizontalAlignment.Left,
        Header = "Settings to paste into the outside assistant",
    };

    private Assistant? _assistant;
    private string? _assistantFor;
    private bool _busy;
    private CancellationTokenSource? _cancel;

    public AgentPanel(SuiteSettings settings, Func<SuiteCompany?> company, Func<string?> companyPath)
    {
        _settings = settings;
        _company = company;
        _companyPath = companyPath;
        Spacing = 8;
        var heading = new TextBlock { Text = "AI assistant", Style = Resource<Style>("SectionTitleStyle") };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        Children.Add(heading);
        Children.Add(new TextBlock
        {
            Text = "Ask questions about your practice in plain words. The assistant looks things up in the company file through fixed read-only tools, as you: it sees only what your role allows, every lookup is written to the audit log, and it cannot change a record.",
            Style = Resource<Style>("SecondaryStyle"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        AutomationProperties.SetLiveSetting(_state, AutomationLiveSetting.Polite);
        Children.Add(_state);

        foreach (var m in AssistantModels.All)
            _model.Items.Add(new ComboBoxItem { Content = m.Label, Tag = m.Id });
        _model.SelectedItem = _model.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == AssistantModels.Find(_settings.Get("assistant_model")).Id);
        _model.SelectionChanged += (_, _) =>
        {
            _settings.Set("assistant_model", SelectedModel.Id);
            _modelWhy.Text = SelectedModel.Why;
            _assistant = null;
            Update();
        };
        _modelWhy.Text = SelectedModel.Why;
        Children.Add(_model);
        Children.Add(_modelWhy);

        var save = new Button { Content = "Save key" };
        var forget = new Button { Content = "Forget key" };
        save.Click += (_, _) => SaveKey();
        forget.Click += (_, _) =>
        {
            _keys.Forget();
            _assistant = null;
            Update();
        };
        Children.Add(_key);
        Children.Add(new FlexBar { Children = { save, forget } });
        Children.Add(_keyState);

        _consent.IsChecked = _settings.Get("assistant_consent") == "yes";
        _consent.Click += (_, _) =>
        {
            _settings.Set("assistant_consent", _consent.IsChecked == true ? "yes" : "no");
            _assistant = null;
            Update();
        };
        Children.Add(_consent);

        _scroll.Content = _transcript;
        Children.Add(_scroll);
        _question.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && _send.IsEnabled)
            {
                e.Handled = true;
                _ = Ask();
            }
        };
        _send.Click += async (_, _) => await Ask();
        _new.Click += (_, _) =>
        {
            _transcript.Children.Clear();
            _assistant?.Reset();
            _progress.Text = "";
        };
        Children.Add(_question);
        Children.Add(new FlexBar { Children = { _send, _new } });
        AutomationProperties.SetLiveSetting(_progress, AutomationLiveSetting.Polite);
        Children.Add(_progress);

        var tools = new StackPanel { Spacing = 2 };
        tools.Children.Add(new TextBlock { Text = "What it can look up", Style = Resource<Style>("SectionTitleStyle"), FontSize = 16 });
        foreach (var tool in AgentLink.Tools)
            tools.Children.Add(new TextBlock { Text = $"{tool.Name} · {tool.Purpose}", Style = Resource<Style>("BodyStyle"), TextWrapping = TextWrapping.Wrap });
        tools.Children.Add(new TextBlock { Text = "company.details · The company's details and a project's details (bank and UPI details are never sent)", Style = Resource<Style>("BodyStyle"), TextWrapping = TextWrapping.Wrap });
        Children.Add(tools);

        var outside = new Expander { Header = "Use an outside assistant instead (Claude Desktop or Claude Code)", HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 800 };
        var inner = new StackPanel { Spacing = 8 };
        inner.Children.Add(new TextBlock
        {
            Text = "An outside assistant starts office-mcp and signs in with its own user name and password, which it keeps in its own settings. Make a user with the lowest role that does the job, for example an Auditor named auditor.",
            Style = Resource<Style>("SecondaryStyle"),
            TextWrapping = TextWrapping.Wrap,
        });
        inner.Children.Add(_config);
        var copy = new Button { Content = "Copy the settings" };
        copy.Click += (_, _) =>
        {
            var data = new DataPackage();
            data.SetText(_config.Text);
            Clipboard.SetContent(data);
        };
        inner.Children.Add(copy);
        outside.Content = inner;
        Children.Add(outside);
        Refresh();
    }

    private static T Resource<T>(string key) => (T)Microsoft.UI.Xaml.Application.Current.Resources[key];

    private AssistantModel SelectedModel => AssistantModels.Find((_model.SelectedItem as ComboBoxItem)?.Tag as string);

    /// <summary>Redraws for the company and person now signed in here.</summary>
    public void Refresh()
    {
        _assistant = null;
        var company = _companyPath();
        var program = new AgentLink(_settings).FindProgram();
        _config.Text = company == null ? "" : AgentLink.ClientConfig(program ?? "office-mcp.exe", company, "auditor");
        Update();
    }

    private void Update()
    {
        var company = _company();
        _keyState.Text = _keys.IsSaved ? "A key is saved for this Windows account. It is not shown again." : _keys.HasKey ? "Using the ANTHROPIC_API_KEY set on this computer." : "No key yet. Create one in the Anthropic console and paste it here.";
        var consent = _consent.IsChecked == true;
        if (company == null)
            _state.Text = "Sign in under Company first. The assistant works as the person signed in.";
        else if (!_keys.HasKey)
            _state.Text = "Add your API key to start.";
        else if (!consent)
            _state.Text = "Agree to send your questions and the records it looks up to Anthropic to start.";
        else
            _state.Text = $"Ready. Working as {company.User} ({company.Role}) on {company.Name}, with {SelectedModel.Label}.";
        _send.IsEnabled = !_busy && company != null && _keys.HasKey && consent;
        _question.IsEnabled = !_busy;
    }

    private void SaveKey()
    {
        try
        {
            _keys.Save(_key.Password);
            _key.Password = "";
            _assistant = null;
        }
        catch (ArgumentException ex)
        {
            _progress.Text = ex.Message;
        }
        Update();
    }

    private async Task Ask()
    {
        var company = _company();
        var question = _question.Text.Trim();
        if (_busy || company == null || question.Length == 0)
            return;
        var key = _keys.Load();
        if (key == null)
            return;
        _busy = true;
        Update();
        _question.Text = "";
        Say("You", question);
        _progress.Text = "Looking…";
        _cancel = new CancellationTokenSource();
        try
        {
            var model = SelectedModel;
            var identity = $"{company.Path}|{company.User}|{model.Id}";
            if (_assistant == null || _assistantFor != identity)
            {
                _assistant = new Assistant(new AnthropicApi(key), new CompanyTools(company), model.Id, company.Name, company.User, company.Role, _consent.IsChecked == true);
                _assistantFor = identity;
            }
            var assistant = _assistant;
            var answer = await Task.Run(() => assistant.AskAsync(question, _cancel.Token));
            var looked = answer.Steps.Count == 0 ? "No lookups." : "Looked at: " + string.Join(", ", answer.Steps.Select(s => s.Refused ? $"{s.Tool} (refused)" : s.Tool)) + ".";
            Say("Assistant", answer.Text, $"{looked} {answer.InputTokens + answer.OutputTokens:N0} tokens.");
            _progress.Text = "";
        }
        catch (AssistantException ex)
        {
            _progress.Text = ex.Message;
        }
        catch (EngineException ex)
        {
            _progress.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            Update();
            _scroll.ChangeView(null, _scroll.ScrollableHeight, null);
        }
    }

    private void Say(string who, string text, string? note = null)
    {
        var block = new StackPanel { Spacing = 2 };
        block.Children.Add(new TextBlock { Text = who, Style = Resource<Style>("CaptionStyle") });
        block.Children.Add(new TextBlock { Text = text, Style = Resource<Style>("BodyStyle"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        if (note != null)
            block.Children.Add(new TextBlock { Text = note, Style = Resource<Style>("CaptionStyle"), TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetName(block, $"{who}: {text}");
        _transcript.Children.Add(block);
    }
}
