using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

/// <summary>
/// The path of a project from set-up to the last payment, with the one next action as a button. The engine decides what is
/// next (`project_path`); this only shows it and takes the person there. It never posts or bills anything by itself except when
/// the person presses "Bill ...", which creates the draft invoice for a stage that is already reached.
/// </summary>
internal static class ProjectPathView
{
    private static string StateLabel(string state) => state switch
    {
        "done" => "Done",
        "next" => "Next",
        "waiting" => "Waiting",
        "skipped" => "Not needed",
        _ => state,
    };

    /// <summary>Shows the steps and the next-action button. `local` handles actions that happen on the page already showing the project.</summary>
    public static void Paint(AppShell shell, StackPanel host, JsonElement path, Func<string, bool>? local = null)
    {
        host.Children.Clear();
        if (path.ValueKind != JsonValueKind.Object)
            return;
        var projectId = path.TryGetProperty("project_id", out var pid) && pid.ValueKind == JsonValueKind.Number ? pid.GetInt64() : 0;
        host.Children.Add(Flow.Section("Where this project stands"));
        host.Children.Add(Flow.Secondary(Kit.Str(path, "summary")));
        if (path.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in steps.EnumerateArray())
            {
                var state = Kit.Str(step, "state");
                var line = new Grid { ColumnSpacing = 12 };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var badge = new TextBlock
                {
                    Text = StateLabel(state),
                    Style = (Style)Application.Current.Resources[state == "next" ? "SectionTitleStyle" : "CaptionStyle"],
                    VerticalAlignment = VerticalAlignment.Top,
                };
                var body = new StackPanel { Spacing = 2 };
                body.Children.Add(new TextBlock { Text = Kit.Str(step, "label"), Style = (Style)Application.Current.Resources["BodyStyle"], TextWrapping = TextWrapping.WrapWholeWords });
                body.Children.Add(Flow.Secondary(Kit.Str(step, "detail")));
                Grid.SetColumn(body, 1);
                line.Children.Add(badge);
                line.Children.Add(body);
                AutomationProperties.SetName(line, $"{StateLabel(state)}: {Kit.Str(step, "label")}. {Kit.Str(step, "detail")}");
                host.Children.Add(line);
            }
        }
        if (path.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.Object && shell.CanEdit)
        {
            var captured = next.Clone();
            host.Children.Add(Flow.Command(Kit.Str(next, "label"), () => Go(shell, projectId, captured, local), primary: true));
        }
        else if (!path.TryGetProperty("next", out var none) || none.ValueKind == JsonValueKind.Null)
        {
            host.Children.Add(Flow.Secondary("Nothing is waiting on this project."));
        }
    }

    /// <summary>Takes the person to where the next action is done, or does the one safe thing (raising a draft invoice) itself.</summary>
    public static void Go(AppShell shell, long projectId, JsonElement next, Func<string, bool>? local = null)
    {
        var kind = Kit.Str(next, "kind");
        var page = Kit.Str(next, "page");
        var target = next.TryGetProperty("target_id", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : projectId;
        if (local != null && local(kind))
            return;
        if (kind == "bill_stage")
        {
            _ = shell.Run("Raising the invoice for this stage…", () =>
            {
                var invoice = shell.Call("doc_bill_milestone", new { milestone_id = target, invoice_date = Kit.Today() });
                var invoiceId = invoice.ValueKind == JsonValueKind.Number ? invoice.GetInt64() : 0;
                shell.Window.DispatcherQueue.TryEnqueue(() =>
                {
                    shell.PendingOpen = invoiceId > 0 ? ("invoices", invoiceId, "open") : null;
                    shell.Navigate?.Invoke("invoices");
                });
            });
            return;
        }
        var action = kind switch
        {
            "new_agreement" => "new_agreement",
            "record_payment" => "pay",
            "assign_stages" => "stages",
            "mark_stage" => "stages",
            _ => "open",
        };
        shell.PendingOpen = (page, target, action);
        shell.Navigate?.Invoke(page);
    }
}
