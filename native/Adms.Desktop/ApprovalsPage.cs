using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

/// <summary>
/// Studio inbox for estimates and running bills shared from CoreOp as drafts.
/// Approve locks; corrections require a revised version submitted by CoreOp.
/// </summary>
internal static class ApprovalsPage
{
    public static UIElement Build(AppShell shell)
    {
        var form = new DraftForm();
        var page = form.Body;
        page.Children.Add(Flow.SheetMark("01.10", "Practice", "Approvals"));
        page.Children.Add(Flow.Heading("Approvals"));
        page.Children.Add(Flow.Secondary("Estimates and running bills shared from CoreOp as drafts. Approve locks the values. Corrections need a revised version — the locked record is never edited in place."));

        var filter = Book.Choices("Show", ("Drafts", "draft"), ("Approved", "approved_locked"), ("Superseded", "superseded"), ("All", ""));
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
        long? selectedId = null;
        string? selectedStatus = null;

        void Reload()
        {
            var status = Book.Selected(filter, "draft");
            object args = string.IsNullOrEmpty(status) ? new { } : new { status };
            _ = shell.Run("Loading approvals…", () =>
            {
                var rows = shell.Call("approval_list", args);
                shell.Window.DispatcherQueue.TryEnqueue(() =>
                {
                    listHost.Children.Clear();
                    selectedId = null;
                    selectedStatus = null;
                    detail.Text = "";
                    if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
                    {
                        listHost.Children.Add(Flow.Secondary("No items in this list."));
                        return;
                    }
                    foreach (var row in rows.EnumerateArray())
                    {
                        var id = Kit.Id(row) ?? 0;
                        var title = Kit.Str(row, "title");
                        var kind = Kit.Str(row, "kind");
                        var st = Kit.Str(row, "status");
                        var ver = Kit.Str(row, "version");
                        var line = new TextBlock
                        {
                            Text = $"#{id} · {kind} v{ver} · {st} — {title}",
                            TextWrapping = TextWrapping.WrapWholeWords,
                        };
                        var open = new Button { Content = "Open", MinWidth = 72, Tag = id };
                        open.Click += (_, _) =>
                        {
                            _ = shell.Run("Opening…", () =>
                            {
                                var full = shell.Call("approval_get", new { id });
                                shell.Window.DispatcherQueue.TryEnqueue(() =>
                                {
                                    if (full.ValueKind != JsonValueKind.Object)
                                    {
                                        shell.Problem("That draft was not found.");
                                        return;
                                    }
                                    selectedId = id;
                                    selectedStatus = Kit.Str(full, "status");
                                    detail.Text = Kit.Str(full, "body");
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
                if (selectedId is not { } id || selectedStatus != "draft")
                {
                    shell.Problem("Open a draft first.");
                    return;
                }
                _ = shell.Run("Approving…", () => shell.Call("approval_approve", new { id })).ContinueWith(task =>
                    shell.Window.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (task.IsCompletedSuccessfully && task.Result)
                            Reload();
                    }));
            };
            page.Children.Add(approve);
        }

        if (shell.CanEdit)
        {
            var withdraw = new Button { Content = "Withdraw draft", MinHeight = 32 };
            withdraw.Click += (_, _) =>
            {
                if (selectedId is not { } id || selectedStatus != "draft")
                {
                    shell.Problem("Open a draft to withdraw.");
                    return;
                }
                _ = shell.Run("Withdrawing…", () => shell.Call("approval_withdraw", new { id })).ContinueWith(task =>
                    shell.Window.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (task.IsCompletedSuccessfully && task.Result)
                            Reload();
                    }));
            };
            page.Children.Add(withdraw);
        }

        page.Children.Add(Flow.Secondary("CoreOp shares drafts with approval_submit on office.dll. See docs/contracts/approvals.md."));
        Reload();
        return form;
    }
}
