using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using DraftForm = Adms.Desktop.Ledgers.DraftForm;

namespace Adms.Desktop;

/// <summary>
/// Suite assistant hosted in Studio (absorbed from the OSAS hub). Uses governed read-only
/// <c>agent_tools</c> / <c>agent_call</c>. Consent and model choice are stored under the O-Studio
/// settings folder (never in the company file). API keys stay in this Windows profile only.
/// </summary>
internal static class AssistantPage
{
    private static string PrefsPath => Path.Combine(AppFolder.Prepare(), "assistant.json");

    private sealed class Prefs
    {
        public bool Consent { get; set; }
        public string Model { get; set; } = "claude-sonnet-4-5";
    }

    private static Prefs LoadPrefs()
    {
        try
        {
            if (File.Exists(PrefsPath))
                return JsonSerializer.Deserialize<Prefs>(File.ReadAllText(PrefsPath)) ?? new Prefs();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new Prefs();
    }

    private static void SavePrefs(Prefs prefs)
    {
        try
        {
            File.WriteAllText(PrefsPath, JsonSerializer.Serialize(prefs));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static UIElement Build(AppShell shell)
    {
        var prefs = LoadPrefs();
        var form = new DraftForm();
        var page = form.Body;
        page.Children.Add(Flow.SheetMark("06.08", "Office", "Assistant"));
        page.Children.Add(Flow.Heading("Assistant"));
        page.Children.Add(Flow.Secondary("Read-only tools over this company, as the signed-in person. Bank and UPI details are withheld from company.details. The assistant cannot change records."));

        var consent = new CheckBox
        {
            Content = "I agree that if I use cloud chat, my questions and looked-up records may be sent to Anthropic.",
            IsChecked = prefs.Consent,
        };
        consent.Checked += (_, _) => { prefs.Consent = true; SavePrefs(prefs); };
        consent.Unchecked += (_, _) => { prefs.Consent = false; SavePrefs(prefs); };
        page.Children.Add(consent);

        var model = Book.Choices(
            "Preferred cloud model",
            ("Claude Sonnet 5.5", "claude-sonnet-4-5"),
            ("Claude Haiku 5.5", "claude-haiku-4-5"),
            ("Claude Opus 5.5", "claude-opus-4-5"));
        foreach (ComboBoxItem item in model.Items)
        {
            if (item.Tag as string == prefs.Model)
            {
                model.SelectedItem = item;
                break;
            }
        }
        model.SelectionChanged += (_, _) =>
        {
            prefs.Model = Book.Selected(model, "claude-sonnet-4-5");
            SavePrefs(prefs);
        };
        page.Children.Add(model);
        page.Children.Add(Flow.Secondary("Cloud chat with your own API key is planned next to this tool runner. Local tool runs never leave this computer."));

        page.Children.Add(Flow.Section("Local tools"));
        var toolList = new StackPanel { Spacing = 4 };
        page.Children.Add(toolList);
        var argsBox = new TextBox
        {
            Header = "Arguments (JSON object)",
            Text = "{}",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 72,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
        };
        page.Children.Add(argsBox);
        var output = new TextBox
        {
            Header = "Result",
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 160,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
        };
        page.Children.Add(output);

        string? selectedTool = null;
        void LoadTools()
        {
            _ = shell.Run("Loading tools…", () =>
            {
                var tools = shell.Call("agent_tools");
                shell.Window.DispatcherQueue.TryEnqueue(() =>
                {
                    toolList.Children.Clear();
                    if (tools.ValueKind != JsonValueKind.Array)
                    {
                        toolList.Children.Add(Flow.Secondary("No tools available."));
                        return;
                    }
                    foreach (var t in tools.EnumerateArray())
                    {
                        var name = Kit.Str(t, "name");
                        var desc = Kit.Str(t, "description");
                        var pick = new Button { Content = name, MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Left };
                        pick.Click += (_, _) =>
                        {
                            selectedTool = name;
                            output.Text = desc;
                        };
                        toolList.Children.Add(pick);
                    }
                });
            });
        }

        var run = new Button { Content = "Run selected tool locally", MinHeight = 32, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        run.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(selectedTool))
            {
                shell.Problem("Choose a tool first.");
                return;
            }
            object? arguments;
            try
            {
                arguments = JsonSerializer.Deserialize<object>(string.IsNullOrWhiteSpace(argsBox.Text) ? "{}" : argsBox.Text);
            }
            catch (JsonException ex)
            {
                shell.Problem("Arguments must be JSON: " + ex.Message);
                return;
            }
            var name = selectedTool;
            _ = shell.Run("Running tool…", () =>
            {
                var result = shell.Call("agent_call", new { name, arguments });
                shell.Window.DispatcherQueue.TryEnqueue(() => output.Text = result.GetRawText());
            });
        };
        page.Children.Add(run);
        LoadTools();
        return form;
    }
}
