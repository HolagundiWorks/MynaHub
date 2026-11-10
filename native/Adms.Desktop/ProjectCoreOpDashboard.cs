using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

/// <summary>MynaOps BOQ, estimate and running-bill abstracts on the project dashboard.</summary>
internal static class ProjectCoreOpDashboard
{
    public static void PaintHeader(StackPanel host, AppShell shell, long? projectId, Action reload)
    {
        host.Children.Clear();
        if (projectId is not long id)
            return;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(Flow.Section("Project dashboard"));
        var refresh = new Button { Content = "Reload", MinHeight = O.Ui.SuiteScale.ControlMinHeight };
        refresh.Click += (_, _) => reload();
        row.Children.Add(refresh);
        host.Children.Add(row);
        host.Children.Add(Flow.Secondary("Reads MynaOps data saved in this company file (same ProjectRef). Reload after sharing from MynaOps."));
        host.Tag = id;
    }

    public static void LoadAbstracts(AppShell shell, StackPanel host, long projectId)
    {
        if (host.Tag is not long tagged || tagged != projectId)
            return;

        while (host.Children.Count > 2)
            host.Children.RemoveAt(host.Children.Count - 1);

        host.Children.Add(Flow.Secondary("Loading MynaOps abstracts…"));
        _ = shell.Run("Loading project dashboard…", () =>
        {
            JsonElement boq;
            JsonElement estimate;
            JsonElement ra;
            try { boq = shell.Call("project_boq_summary", new { project_id = projectId }); }
            catch { boq = default; }
            try { estimate = shell.Call("project_estimate_summary", new { project_id = projectId }); }
            catch { estimate = default; }
            try { ra = shell.Call("project_ra_summary", new { project_id = projectId }); }
            catch { ra = default; }

            shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                while (host.Children.Count > 2)
                    host.Children.RemoveAt(host.Children.Count - 1);

                host.Children.Add(Line("BOQ abstract", FormatBoq(boq)));
                host.Children.Add(Line("Estimate abstract", FormatEstimate(estimate)));
                host.Children.Add(Line("Running bill abstract", FormatRa(ra)));
            });
        });
    }

    static UIElement Line(string title, string body)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SectionTitleStyle"] });
        panel.Children.Add(Flow.Secondary(body));
        return panel;
    }

    static string FormatBoq(JsonElement summary)
    {
        if (summary.ValueKind != JsonValueKind.Object)
            return "Could not read the BOQ abstract.";
        if (summary.TryGetProperty("present", out var present) && present.ValueKind == JsonValueKind.True
            && summary.TryGetProperty("base_total_paise", out var paise) && paise.TryGetInt64(out var total))
        {
            var book = Kit.Str(summary, "rate_book_version_name");
            var rev = Kit.Str(summary, "document_revision");
            return $"Base at schedule rates {Flow.Money(total)}{(book.Length == 0 ? "" : $" · {book}")} · document revision {rev}.";
        }

        return Kit.Str(summary, "reason").Length > 0
            ? Kit.Str(summary, "reason")
            : "No BOQ base is saved for this project yet.";
    }

    static string FormatEstimate(JsonElement summary)
    {
        if (summary.ValueKind != JsonValueKind.Object)
            return "Could not read the estimate abstract.";
        if (summary.TryGetProperty("present", out var present) && present.ValueKind == JsonValueKind.True
            && summary.TryGetProperty("grand_total_paise", out var paise) && paise.TryGetInt64(out var total))
        {
            var book = Kit.Str(summary, "rate_book_version_name");
            var rev = Kit.Str(summary, "document_revision");
            return $"Grand total {Flow.Money(total)}{(book.Length == 0 ? "" : $" · {book}")} · document revision {rev}.";
        }

        return Kit.Str(summary, "reason").Length > 0
            ? Kit.Str(summary, "reason")
            : "No estimate is saved for this project yet.";
    }

    static string FormatRa(JsonElement summary)
    {
        if (summary.ValueKind != JsonValueKind.Object)
            return "Could not read the running bill abstract.";
        if (summary.TryGetProperty("present", out var present) && present.ValueKind == JsonValueKind.True
            && summary.TryGetProperty("last_net_paise", out var netNode) && netNode.TryGetInt64(out var net))
        {
            var number = Kit.Str(summary, "last_bill_number");
            var gross = summary.TryGetProperty("last_gross_paise", out var g) && g.TryGetInt64(out var gp) ? Flow.Money(gp) : "";
            return $"Last bill {(number.Length == 0 ? "(unnumbered)" : number)} · gross {gross} · net {Flow.Money(net)} · {Kit.Str(summary, "certified_count")} certified of {Kit.Str(summary, "bill_count")} bill(s).";
        }

        return Kit.Str(summary, "reason").Length > 0
            ? Kit.Str(summary, "reason")
            : "No running bills are saved for this project yet.";
    }
}
