using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

/// <summary>
/// "Import from Falcon (CSV)": read a Falcon export, show what it would do, then import it as a draft estimate version and a draft
/// running bill. The rules (strict checks, idempotency, never approving) are in office-core <c>falcon_import</c>; contract:
/// <c>docs/contracts/falcon-csv.md</c>. This file is only the dialogs.
/// </summary>
internal static class FalconImport
{
    /// <summary>The button for a page's command row.</summary>
    public static Button Button(AppShell shell)
    {
        var button = new Button { Content = "Import from Falcon (CSV)", MinHeight = 32 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "Import from Falcon (CSV)");
        button.Click += async (_, _) =>
        {
            try { await Start(shell); }
            catch (Exception ex) { shell.Problem(ex.Message); }
        };
        return button;
    }

    private static async Task Start(AppShell shell)
    {
        if (!shell.CanEdit)
        {
            shell.Problem("Your role cannot import estimates.");
            return;
        }
        var path = await FileDialogs.PickFile(shell.Window.Handle, ".csv");
        if (path == null) return;
        long? projectId = null;
        while (true)
        {
            JsonElement preview = default;
            if (!await shell.Run("Reading the Falcon file…", () => preview = shell.Call("falcon_csv_preview", new { path, project_id = projectId }).Clone()))
                return;
            if (Flag(preview, "refused"))
            {
                await Say(shell, "This file cannot be imported", Refusal(preview) + "\n\nNothing was imported. Fix the export in Falcon and try again.");
                return;
            }

            var panel = new StackPanel { Spacing = 8, MinWidth = 520 };
            panel.Children.Add(new TextBlock { Text = Summary(preview), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });

            ComboBox? projects = null;
            if (Flag(preview, "needs_project"))
            {
                panel.Children.Add(new TextBlock { Text = "Which Kivo project is this?", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                projects = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Choose a project" };
                JsonElement list = default;
                await shell.Run("Loading projects…", () => list = shell.Call("project_list", new { client_id = (long?)null, include_archived = false }).Clone());
                var suggested = Long(preview, "suggested_project_id");
                if (list.ValueKind == JsonValueKind.Array)
                    foreach (var row in list.EnumerateArray())
                    {
                        var item = new ComboBoxItem { Content = Kit.Str(row, "name"), Tag = Kit.Id(row) };
                        projects.Items.Add(item);
                        if (suggested != null && Kit.Id(row) == suggested) projects.SelectedItem = item;
                    }
                panel.Children.Add(projects);
            }

            var boxes = new List<(string Code, TextBox Box)>();
            if (preview.TryGetProperty("unmapped", out var unmapped) && unmapped.ValueKind == JsonValueKind.Array && unmapped.GetArrayLength() > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "These Falcon item codes are not mapped to a Kivo item yet. They import as they are and are listed as unmapped; nothing is guessed. Type the Kivo item for any you know and choose Save mapping.",
                    TextWrapping = TextWrapping.Wrap,
                });
                foreach (var code in unmapped.EnumerateArray())
                {
                    var name = code.GetString() ?? "";
                    var box = new TextBox { Header = name, PlaceholderText = "Kivo price-list item" };
                    boxes.Add((name, box));
                    panel.Children.Add(box);
                }
            }

            var dialog = new ContentDialog
            {
                Title = "Import from Falcon",
                Content = new ScrollViewer { Content = panel, MaxHeight = 520 },
                PrimaryButtonText = Flag(preview, "noop") ? "" : "Import as drafts",
                SecondaryButtonText = boxes.Count > 0 ? "Save mapping" : "",
                CloseButtonText = Flag(preview, "noop") ? "Close" : "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = shell.Window.Content.XamlRoot,
            };
            var choice = await dialog.ShowAsync();
            if (projects?.SelectedItem is ComboBoxItem { Tag: long picked }) projectId = picked;

            if (choice == ContentDialogResult.Secondary)
            {
                var entered = boxes.Where(b => !string.IsNullOrWhiteSpace(b.Box.Text)).Select(b => (b.Code, Item: b.Box.Text.Trim())).ToList();
                await shell.Run("Saving the mapping…", () =>
                {
                    foreach (var (code, item) in entered)
                        shell.Call("falcon_csv_map_set", new { falcon_code = code, kivo_item = item });
                });
                continue; // read the file again with the new mapping
            }
            if (choice != ContentDialogResult.Primary) return;
            if (Flag(preview, "needs_project") && projectId == null)
            {
                shell.Problem("Choose the Kivo project first.");
                continue;
            }

            JsonElement result = default;
            if (!await shell.Run("Importing…", () => result = shell.Call("falcon_csv_import", new { path, project_id = projectId }).Clone()))
                return;
            await Say(shell, Number(result, "refused") > 0 ? "Nothing was imported" : Flag(result, "noop") ? "Nothing changed" : "Imported as drafts", Outcome(result));
            return;
        }
    }

    private static async Task Say(AppShell shell, string title, string text)
    {
        await new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, MaxHeight = 480 },
            CloseButtonText = "Close",
            XamlRoot = shell.Window.Content.XamlRoot,
        }.ShowAsync();
    }

    private static bool Flag(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    private static long Number(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static long? Long(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    private static string Refusal(JsonElement e)
    {
        var text = new StringBuilder("The file was refused as a whole:\n");
        if (e.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array)
            foreach (var issue in issues.EnumerateArray())
            {
                var line = Number(issue, "line");
                text.Append(line > 0 ? $"\nLine {line}: " : "\n").Append(Kit.Str(issue, "message"));
            }
        return text.ToString();
    }

    private static string Summary(JsonElement p)
    {
        var rows = p.GetProperty("rows");
        var estimate = p.GetProperty("estimate");
        var bill = p.GetProperty("bill");
        var text = new StringBuilder();
        text.AppendLine($"{Kit.Str(p, "project_name")} · drawing revision {Number(p, "drawing_revision")} · exported {Kit.Str(p, "exported")} by Falcon {Kit.Str(p, "falcon_version")}");
        text.AppendLine($"File SHA-256: {Kit.Str(p, "file_sha256")}");
        text.AppendLine();
        text.AppendLine($"Rows: {Number(rows, "estimate")} estimate, {Number(rows, "measured")} measured, {Number(rows, "takeoff")} take-off.");
        var current = p.TryGetProperty("current_estimate_total", out var cur) && cur.ValueKind == JsonValueKind.Number ? $"; Kivo's current estimate v{Number(p, "current_estimate_version")} ({Kit.Str(p, "current_estimate_status")}) is {cur.GetDouble():N2}" : "";
        text.AppendLine($"Estimate total in the file: {p.GetProperty("file_estimate_total").GetDouble():N2} {Kit.Str(p, "currency")}{current}.");
        text.AppendLine();
        text.AppendLine("Estimate: " + Describe(estimate, "estimate"));
        text.AppendLine("Running bill: " + Describe(bill, "bill"));
        if (Flag(p, "noop"))
            text.AppendLine().AppendLine(Flag(p, "already_imported") ? "This file was imported before. Importing it again changes nothing." : "Everything in this file is already in Kivo. Importing it changes nothing.");
        if (estimate.TryGetProperty("differing", out var diffs) && diffs.ValueKind == JsonValueKind.Array && diffs.GetArrayLength() > 0)
        {
            text.AppendLine().AppendLine("Lines that differ from the current estimate:");
            var shown = 0;
            foreach (var d in diffs.EnumerateArray())
            {
                if (++shown > 25) { text.AppendLine($"… and {diffs.GetArrayLength() - 25} more"); break; }
                text.AppendLine($"  {Kit.Str(d, "change")}: {Kit.Str(d, "row_type")} {Kit.Str(d, "key")}");
            }
        }
        if (p.TryGetProperty("flags", out var flags) && flags.ValueKind == JsonValueKind.Array && flags.GetArrayLength() > 0)
        {
            text.AppendLine().AppendLine("To check (the bill is still created):");
            foreach (var f in flags.EnumerateArray()) text.AppendLine("  • " + f.GetString());
        }
        return text.ToString();
    }

    private static string Describe(JsonElement section, string what)
    {
        var counts = section.GetProperty("counts");
        var tail = $"{Number(counts, "added")} added, {Number(counts, "changed")} changed, {Number(counts, "unchanged")} unchanged" + (Number(counts, "removed") > 0 ? $", {Number(counts, "removed")} removed" : "");
        return Kit.Str(section, "action") switch
        {
            "new_version" => $"a new draft version ({tail})",
            "new_bill" => $"a new draft bill ({tail})",
            "update_draft" => $"update the open draft ({tail})",
            "unchanged" => $"already in Kivo ({tail})",
            _ => $"none in this file",
        };
    }

    private static string Outcome(JsonElement r)
    {
        if (Number(r, "refused") > 0) return Refusal(r);
        var text = new StringBuilder();
        text.AppendLine($"Added {Number(r, "added")}, changed {Number(r, "changed")}, unchanged {Number(r, "unchanged")}, refused {Number(r, "refused")}.");
        if (Flag(r, "noop")) text.AppendLine("Nothing was written: this file is already in Kivo.");
        if (Long(r, "estimate_version") is { } ev && Kit.Str(r, "estimate_action") is "new_version" or "update_draft")
            text.AppendLine($"Estimate version {ev} is a draft.");
        if (Long(r, "bill_version") is { } bv && Kit.Str(r, "bill_action") is "new_bill" or "update_draft")
            text.AppendLine($"Running bill {bv} is a draft.");
        text.AppendLine("Nothing was approved; approve the drafts in Practice → Approvals.");
        if (r.TryGetProperty("unmapped", out var un) && un.ValueKind == JsonValueKind.Array && un.GetArrayLength() > 0)
            text.AppendLine().AppendLine("Unmapped item codes: " + string.Join(", ", un.EnumerateArray().Select(u => u.GetString())));
        if (r.TryGetProperty("flags", out var flags) && flags.ValueKind == JsonValueKind.Array && flags.GetArrayLength() > 0)
        {
            text.AppendLine().AppendLine("To check:");
            foreach (var f in flags.EnumerateArray()) text.AppendLine("  • " + f.GetString());
        }
        text.AppendLine().AppendLine("Written to the activity log with SHA-256 " + Kit.Str(r, "file_sha256"));
        return text.ToString();
    }
}
