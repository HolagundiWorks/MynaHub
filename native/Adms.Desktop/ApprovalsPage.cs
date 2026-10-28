using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using DraftForm = Adms.Desktop.Ledgers.DraftForm;

namespace Adms.Desktop;

/// <summary>
/// Studio inbox for CoreOp draft documents (<c>aqc.estimate.draft</c> / <c>aqc.ra.draft.*</c>).
/// Approve locks the envelope state; corrections require a new CoreOp share (revise-version).
/// Contract: O-CoreOp <c>docs/STUDIO-DRAFT-SHARE.md</c>; Studio <c>docs/contracts/approvals.md</c>.
/// </summary>
internal static class ApprovalsPage
{
    public static UIElement Build(AppShell shell)
    {
        var form = new DraftForm();
        var page = form.Body;
        page.Children.Add(Flow.SheetMark("01.10", "Practice", "Approvals"));
        page.Children.Add(Flow.Heading("Approvals"));
        page.Children.Add(Flow.Secondary("Estimates and running bills CoreOp shared into the company file. Approve locks the draft. Corrections need a revised CoreOp share — a locked body is never edited in place."));

        var filter = Book.Choices("Show", ("Drafts", "draft"), ("Pending", "pending_approval"), ("Approved", "approved_locked"), ("Superseded", "superseded"), ("All", ""));
        page.Children.Add(filter);
        var listHost = new StackPanel { Spacing = 8 };
        page.Children.Add(listHost);
        var detail = new TextBox
        {
            Header = "Selected body (read-only)",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true,
            MinHeight = 160,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
        };
        page.Children.Add(detail);
        long? selectedProject = null;
        string? selectedKind = null;
        string? selectedState = null;
        long selectedRevision = 0;

        void Reload()
        {
            var state = Book.Selected(filter, "draft");
            object args = string.IsNullOrEmpty(state) ? new { } : new { state };
            _ = shell.Run("Loading drafts…", () =>
            {
                var rows = shell.Call("studio_draft_list", args);
                shell.Window.DispatcherQueue.TryEnqueue(() =>
                {
                    listHost.Children.Clear();
                    selectedProject = null;
                    selectedKind = null;
                    selectedState = null;
                    selectedRevision = 0;
                    detail.Text = "";
                    if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
                    {
                        listHost.Children.Add(Flow.Secondary("No CoreOp drafts in this list."));
                        return;
                    }
                    foreach (var row in rows.EnumerateArray())
                    {
                        var projectId = row.TryGetProperty("project_id", out var pid) && pid.ValueKind == JsonValueKind.Number ? pid.GetInt64() : 0;
                        var kind = Kit.Str(row, "kind");
                        var st = Kit.Str(row, "state");
                        var title = Kit.Str(row, "title");
                        var artifact = Kit.Str(row, "artifact_kind");
                        var rev = row.TryGetProperty("revision", out var rv) && rv.ValueKind == JsonValueKind.Number ? rv.GetInt64() : 0;
                        var sharedBy = Kit.Str(row, "shared_by");
                        var line = new TextBlock
                        {
                            Text = $"project {projectId} · {artifact} · {kind} · rev {rev} · {st} — {title}" + (string.IsNullOrEmpty(sharedBy) ? "" : $" (by {sharedBy})"),
                            TextWrapping = TextWrapping.WrapWholeWords,
                        };
                        var open = new Button { Content = "Open", MinWidth = 72 };
                        var captureProject = projectId;
                        var captureKind = kind;
                        open.Click += (_, _) =>
                        {
                            _ = shell.Run("Opening…", () =>
                            {
                                var full = shell.Call("studio_draft_get", new { project_id = captureProject, kind = captureKind });
                                shell.Window.DispatcherQueue.TryEnqueue(() =>
                                {
                                    if (full.ValueKind != JsonValueKind.Object)
                                    {
                                        shell.Problem("That draft was not found.");
                                        return;
                                    }
                                    selectedProject = captureProject;
                                    selectedKind = captureKind;
                                    selectedState = Kit.Str(full, "state");
                                    selectedRevision = full.TryGetProperty("revision", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt64() : 0;
                                    detail.Text = full.TryGetProperty("body", out var body) ? body.ToString() : "";
                                });
                            });
                        };
                        var rowPanel = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            Children = { open, line },
                        };
                        listHost.Children.Add(rowPanel);
                    }
                });
            });
        }

        filter.SelectionChanged += (_, _) => Reload();

        if (shell.CanIssue)
        {
            var approve = new Button { Content = "Approve and lock", MinHeight = 32, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            approve.Click += (_, _) =>
            {
                if (selectedProject is not { } projectId || string.IsNullOrEmpty(selectedKind) || selectedState is not ("draft" or "pending_approval"))
                {
                    shell.Problem("Open a draft first.");
                    return;
                }
                _ = shell.Run("Approving…", () => shell.Call("studio_draft_approve", new { project_id = projectId, kind = selectedKind, expected_revision = selectedRevision })).ContinueWith(task =>
                    shell.Window.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (task.IsCompletedSuccessfully && task.Result)
                            Reload();
                    }));
            };
            page.Children.Add(approve);

            var revise = new Button { Content = "Mark superseded (revise via CoreOp)", MinHeight = 32 };
            revise.Click += (_, _) =>
            {
                if (selectedProject is not { } projectId || string.IsNullOrEmpty(selectedKind) || selectedState != "approved_locked")
                {
                    shell.Problem("Open an approved locked draft to start a revise cycle.");
                    return;
                }
                _ = shell.Run("Marking superseded…", () => shell.Call("studio_draft_mark_superseded", new { project_id = projectId, kind = selectedKind, expected_revision = selectedRevision })).ContinueWith(task =>
                    shell.Window.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (task.IsCompletedSuccessfully && task.Result)
                            Reload();
                    }));
            };
            page.Children.Add(revise);
        }

        page.Children.Add(Flow.Secondary("CoreOp writes drafts with doc_put kinds aqc.estimate.draft and aqc.ra.draft.<bill_id>. Studio uses studio_draft_* (not approval_*). See docs/contracts/approvals.md."));
        Reload();
        return form;
    }
}
