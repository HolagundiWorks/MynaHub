using System.Text.Json;
using Adms.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;

namespace Adms.Desktop;

internal interface IPageCommands
{
    void NewItem();
    void Save();
    void Print();
    void FocusSearch();
}

internal sealed class AppShell
{
    public required AdmsSession Session { get; init; }
    public required MainWindow Window { get; init; }
    public string Role { get; set; } = "";
    public IPageCommands? Page { get; set; }

    public JsonElement Call(string operation, object? args = null) => Session.Call(operation, args);

    public Task<bool> Run(string status, Action work) => Window.RunWork(status, work);

    public void Problem(string message) => Window.ShowProblem(message);
}

internal sealed class WorkspaceView : UserControl
{
    private readonly AppShell _shell;
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly NavigationView _nav = new()
    {
        PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsPaneToggleButtonVisible = false,
        IsPaneOpen = true,
        IsSettingsVisible = false,
        OpenPaneLength = 280,
        CompactModeThresholdWidth = 0,
        ExpandedModeThresholdWidth = 0,
    };
    private readonly Grid _host = new();
    private readonly List<(string Id, string Label)> _destinations = [];

    public IReadOnlyList<(string Id, string Label)> Destinations => _destinations;

    public void NewItem() => _shell.Page?.NewItem();
    public void Save() => _shell.Page?.Save();
    public void Print() => _shell.Page?.Print();
    public void FocusSearch() => _shell.Page?.FocusSearch();

    public WorkspaceView(AppShell shell)
    {
        _shell = shell;
        _nav.Content = _host;
        _nav.PaneClosing += (_, args) => args.Cancel = true;
        _nav.ItemInvoked += (_, args) =>
        {
            if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string id)
                Show(id);
        };
        Content = _nav;
        BuildMenu();
        Show("documents");
    }

    public void Show(string id)
    {
        _host.Children.Clear();
        var page = PageFor(id);
        _shell.Page = page as IPageCommands;
        _host.Children.Add(page);
        foreach (var item in _nav.MenuItems.OfType<NavigationViewItem>())
            item.IsSelected = item.Tag as string == id;
    }

    private void BuildMenu()
    {
        AddGroup("Work",
            ("documents", "Letters and agreements", "\uE8A5"),
            ("clients", "Clients", "\uE716"),
            ("projects", "Projects", "\uE8F1"));
        AddGroup("Billing",
            ("invoices", "Invoices and receipts", "\uE8A1"),
            ("bank", "Bank reconciliation", "\uE825"));
        if (_shell.Role != "DocumentEditor")
        {
            AddGroup("Accounting",
                ("acc-reports", "Reports", "\uE9F9"),
                ("acc-journals", "Journals", "\uE8A5"),
                ("acc-expenses", "Expenses", "\uE8C7"),
                ("acc-income", "Rent and interest", "\uEAFD"),
                ("acc-salaries", "Salaries", "\uE77B"),
                ("acc-assets", "Fixed assets", "\uE7C5"),
                ("acc-tax", "Tax payments", "\uE9D9"),
                ("acc-setup", "Chart and financial years", "\uE9D2"));
        }
        AddGroup("Settings",
            ("templates", "Templates", "\uE943"),
            ("company", "Company profile", "\uE731"),
            ("settings", "Security, users and backup", "\uE713"));
    }

    private void AddGroup(string title, params (string Id, string Label, string Glyph)[] items)
    {
        _nav.MenuItems.Add(new NavigationViewItemHeader { Content = title });
        foreach (var (id, label, glyph) in items)
        {
            _destinations.Add((id, label));
            _nav.MenuItems.Add(new NavigationViewItem
            {
                Content = label,
                Tag = id,
                Icon = new FontIcon { Glyph = glyph, FontFamily = IconFont, FontSize = 16 },
            });
        }
    }

    private UIElement PageFor(string id) => id switch
    {
        "documents" => Pages.Documents(_shell),
        "clients" => Pages.Clients(_shell),
        "projects" => Pages.Projects(_shell),
        "invoices" => Pages.Invoices(_shell),
        "bank" => Pages.Bank(_shell),
        "acc-reports" => Pages.Reports(_shell),
        "acc-journals" => Pages.Journals(_shell),
        "acc-expenses" => Pages.Expenses(_shell),
        "acc-income" => Pages.Income(_shell),
        "acc-salaries" => Pages.Salaries(_shell),
        "acc-assets" => Pages.Assets(_shell),
        "acc-tax" => Pages.Tax(_shell),
        "acc-setup" => Pages.Accounts(_shell),
        "templates" => Pages.Templates(_shell),
        "company" => Pages.Company(_shell),
        "settings" => Pages.Settings(_shell),
        _ => new TextBlock { Text = id, Margin = new Thickness(16) },
    };
}

internal sealed class WorkPage : UserControl, IPageCommands
{
    public Grid ListPane { get; set; } = new();
    public StackPanel Form { get; set; } = new();
    public StackPanel Actions { get; set; } = new();
    public ListView List { get; set; } = new();
    public TextBlock Detail { get; set; } = new();
    public TextBox? SearchBox { get; set; }
    public Action Reload { get; set; } = () => { };
    public Action<JsonElement>? OnSelect { get; set; }
    public Action? NewAction { get; set; }
    public Action? SaveAction { get; set; }
    public Action? PrintAction { get; set; }
    public void NewItem() => NewAction?.Invoke();
    public void Save() => SaveAction?.Invoke();
    public void Print() => PrintAction?.Invoke();
    public void FocusSearch() => SearchBox?.Focus(FocusState.Programmatic);
}

internal static class Pages
{
    public static UIElement Documents(AppShell shell) => Register(shell, "Letters and agreements", "doc_list", search => new { kind = (string?)null, q = Kit.Blank(search) }, row => Kit.Line(row, "number", "doc_date", "party_name", "subject", "status"), page =>
    {
        var kind = Kit.Field(page.Form, "Kind", "letter_out");
        var party = Kit.Field(page.Form, "Party");
        var address = Kit.Field(page.Form, "Address");
        var subject = Kit.Field(page.Form, "Subject");
        var reference = Kit.Field(page.Form, "Reference");
        var date = Kit.Field(page.Form, "Date", Kit.Today());
        var body = Kit.Field(page.Form, "Body");
        body.AcceptsReturn = true;
        body.Height = 160;
        long? id = null;
        page.OnSelect = row =>
        {
            id = Kit.Id(row);
            kind.Text = Kit.Str(row, "kind");
            party.Text = Kit.Str(row, "party_name");
            subject.Text = Kit.Str(row, "subject");
            reference.Text = Kit.Str(row, "reference");
            date.Text = Kit.Str(row, "doc_date");
        };
        page.NewAction = () => { id = null; party.Text = subject.Text = reference.Text = body.Text = ""; date.Text = Kit.Today(); };
        page.SaveAction = () =>
        {
            var input = new
            {
                id,
                kind = kind.Text.Trim(),
                project_id = (long?)null,
                party_name = party.Text,
                party_address = address.Text,
                subject = subject.Text,
                reference = reference.Text,
                doc_date = date.Text,
                body = body.Text,
                template_version_id = (long?)null,
                fee = "0",
                milestones = Array.Empty<object>(),
            };
            Commit(shell, page, "Saving draft…", () =>
            {
                var saved = shell.Call("doc_save_draft", new { input });
                id = saved.ValueKind == JsonValueKind.Number ? saved.GetInt64() : id;
            });
        };
        page.Actions.Children.Add(Kit.Button("Issue", () =>
        {
            if (id is not long docId) { shell.Problem("Save the draft before issuing it."); return; }
            Commit(shell, page, "Issuing document…", () => shell.Call("doc_issue", new { id = docId }));
        }));
        page.PrintAction = () =>
        {
            if (id is not long docId) return;
            shell.Run("Preparing PDF…", () =>
            {
                var encoded = shell.Call("doc_pdf", new { id = docId }).GetString() ?? "";
                shell.Window.DispatcherQueue.TryEnqueue(async () => await Kit.SavePdf(shell, encoded, "document"));
            });
        };
    });

    public static UIElement Clients(AppShell shell) => Register(shell, "Clients", "client_list", _ => new { include_archived = true }, row => Kit.Line(row, "name", "email", "phone"), page =>
    {
        var name = Kit.Field(page.Form, "Name");
        var contact = Kit.Field(page.Form, "Contact person");
        var email = Kit.Field(page.Form, "Email");
        var phone = Kit.Field(page.Form, "Phone");
        var address = Kit.Field(page.Form, "Address");
        var pan = Kit.Field(page.Form, "PAN");
        var gstin = Kit.Field(page.Form, "GSTIN");
        long? id = null;
        bool archived = false;
        void Clear() { id = null; archived = false; name.Text = contact.Text = email.Text = phone.Text = address.Text = pan.Text = gstin.Text = ""; }
        page.OnSelect = row =>
        {
            id = Kit.Id(row);
            archived = row.TryGetProperty("archived", out var flag) && flag.ValueKind == JsonValueKind.True;
            name.Text = Kit.Str(row, "name");
            contact.Text = Kit.Str(row, "contact_person");
            email.Text = Kit.Str(row, "email");
            phone.Text = Kit.Str(row, "phone");
            address.Text = Kit.Str(row, "address");
            pan.Text = Kit.Str(row, "pan");
            gstin.Text = Kit.Str(row, "gstin");
        };
        page.NewAction = Clear;
        page.SaveAction = () =>
        {
            var client = new { id, name = name.Text, contact_person = contact.Text, email = email.Text, phone = phone.Text, address = address.Text, pan = pan.Text, gstin = gstin.Text, archived };
            Commit(shell, page, "Saving client…", () => shell.Call("client_save", new { client }));
        };
        page.Actions.Children.Add(Kit.Button("Archive", () =>
        {
            if (id is not long clientId) return;
            Commit(shell, page, "Updating client…", () => shell.Call("client_set_archived", new { id = clientId, archived = !archived }));
        }));
    });

    public static UIElement Projects(AppShell shell) => Register(shell, "Projects", "project_list", _ => new { client_id = (long?)null, include_archived = true }, row => Kit.Line(row, "code", "name", "status", "client_name"), page =>
    {
        var client = Kit.Field(page.Form, "Client id");
        var code = Kit.Field(page.Form, "Code");
        var name = Kit.Field(page.Form, "Name");
        var site = Kit.Field(page.Form, "Site address");
        var status = Kit.Field(page.Form, "Status", "active");
        long? id = null;
        page.OnSelect = row =>
        {
            id = Kit.Id(row);
            client.Text = Kit.Str(row, "client_id");
            code.Text = Kit.Str(row, "code");
            name.Text = Kit.Str(row, "name");
            site.Text = Kit.Str(row, "site_address");
            status.Text = Kit.Str(row, "status");
        };
        page.NewAction = () => { id = null; client.Text = code.Text = name.Text = site.Text = ""; status.Text = "active"; };
        page.SaveAction = () =>
        {
            var project = new { id, client_id = Kit.Long(client.Text), code = code.Text, name = name.Text, site_address = site.Text, status = status.Text, archived = false, client_name = "" };
            Commit(shell, page, "Saving project…", () => shell.Call("project_save", new { project }));
        };
    });

    public static UIElement Invoices(AppShell shell) => Register(shell, "Invoices and receipts", "invoice_list", _ => new { }, row => Kit.Line(row, "number", "issue_date", "client_name", "status", "total_display", "balance_display"), page =>
    {
        var project = Kit.Field(page.Form, "Project id");
        var kind = Kit.Field(page.Form, "Kind", "tax_invoice");
        var issue = Kit.Field(page.Form, "Issue date", Kit.Today());
        var due = Kit.Field(page.Form, "Due date", Kit.Today());
        var mode = Kit.Field(page.Form, "GST mode", "intra");
        var notes = Kit.Field(page.Form, "Notes");
        var description = Kit.Field(page.Form, "Line description");
        var qty = Kit.Field(page.Form, "Quantity", "1");
        var rate = Kit.Field(page.Form, "Rate", "0");
        var receiptAmount = Kit.Field(page.Form, "Receipt amount", "0");
        long? id = null;
        page.OnSelect = row =>
        {
            id = Kit.Id(row);
            kind.Text = Kit.Str(row, "kind");
            issue.Text = Kit.Str(row, "issue_date");
            due.Text = Kit.Str(row, "due_date");
        };
        page.NewAction = () => { id = null; project.Text = description.Text = notes.Text = ""; qty.Text = "1"; rate.Text = "0"; kind.Text = "tax_invoice"; };
        object Input() => new
        {
            id,
            project_id = Kit.Long(project.Text),
            kind = kind.Text.Trim(),
            issue_date = issue.Text,
            due_date = due.Text,
            gst_mode = mode.Text.Trim(),
            gst_rate = "",
            notes = notes.Text,
            lines = new[] { new { description = description.Text, qty = qty.Text, rate = rate.Text } },
        };
        page.SaveAction = () =>
        {
            var input = Input();
            Commit(shell, page, "Saving draft…", () =>
            {
                var saved = shell.Call("invoice_save_draft", new { input });
                if (saved.ValueKind == JsonValueKind.Number) id = saved.GetInt64();
            });
        };
        page.Actions.Children.Add(Kit.Button("Issue", () =>
        {
            if (id is not long invoiceId) { shell.Problem("Save the draft before issuing it."); return; }
            Commit(shell, page, "Issuing invoice…", () => shell.Call("invoice_issue", new { id = invoiceId }));
        }));
        page.Actions.Children.Add(Kit.Button("Record receipt", () =>
        {
            if (id is not long invoiceId) return;
            var receipt = new
            {
                invoice_id = invoiceId,
                received_date = Kit.Today(),
                amount = receiptAmount.Text,
                tds = "0",
                tds_section = "",
                mode = "bank",
                reference = "",
                notes = "",
            };
            Commit(shell, page, "Recording receipt…", () => shell.Call("receipt_record", new { input = receipt }));
        }));
        page.PrintAction = () =>
        {
            if (id is not long invoiceId) return;
            shell.Run("Preparing PDF…", () =>
            {
                var encoded = shell.Call("invoice_pdf", new { id = invoiceId }).GetString() ?? "";
                shell.Window.DispatcherQueue.TryEnqueue(async () => await Kit.SavePdf(shell, encoded, "invoice"));
            });
        };
    });

    public static UIElement Bank(AppShell shell) => Register(shell, "Bank reconciliation", "bank_txn_list", _ => new { status = (string?)null, account_id = (long?)null }, row => Kit.Line(row, "txn_date", "narration", "amount_display", "status"), page =>
    {
        var name = Kit.Field(page.Form, "Account name");
        var bank = Kit.Field(page.Form, "Bank");
        var number = Kit.Field(page.Form, "Account number");
        var ifsc = Kit.Field(page.Form, "IFSC");
        page.Actions.Children.Add(Kit.Button("Save account", () =>
        {
            var account = new { id = (long?)null, name = name.Text, bank_name = bank.Text, account_holder = "", account_number = number.Text, ifsc = ifsc.Text, is_primary = false, active = true };
            shell.Run("Saving bank account…", () => shell.Call("bank_account_save", new { account }));
        }));
        page.Actions.Children.Add(Kit.Button("Import CSV", async () =>
        {
            try
            {
                var path = await FileDialogs.PickFile(shell.Window.Handle, ".csv", ".txt");
                if (path == null) return;
                var text = await File.ReadAllTextAsync(path);
                await shell.Run("Importing statement…", () =>
                {
                    var accounts = shell.Call("bank_account_list");
                    if (accounts.ValueKind != JsonValueKind.Array || accounts.GetArrayLength() == 0)
                        throw new AdmsException(1, "Save a bank account before importing a statement.");
                    var accountId = accounts[0].GetProperty("id").GetInt64();
                    var mapping = new { delimiter = ",", has_header = true, date_order = "dmy", date = 0, narration = 1, reference = (int?)null, debit = 2, credit = 3, amount = (int?)null, balance = (int?)null };
                    shell.Call("bank_import_commit", new { account_id = accountId, filename = Path.GetFileName(path), text, mapping });
                });
            }
            catch (Exception ex)
            {
                shell.Problem(ex.Message);
            }
        }));
        page.Actions.Children.Add(Kit.Button("Summary", () => shell.Run("Loading summary…", () =>
        {
            var summary = shell.Call("bank_summary", new { account_id = (long?)null });
            shell.Window.DispatcherQueue.TryEnqueue(() => page.Detail.Text = Kit.Pretty(summary));
        })));
    });

    public static UIElement Reports(AppShell shell)
    {
        var page = Kit.Page("Reports");
        var fromDate = Kit.Field(page.Form, "From", Kit.YearStart());
        var toDate = Kit.Field(page.Form, "To", Kit.Today());
        var output = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        page.Form.Children.Add(output);
        void Report(string op, object args) => shell.Run("Preparing report…", () =>
        {
            var result = shell.Call(op, args);
            shell.Window.DispatcherQueue.TryEnqueue(() => output.Text = Kit.Pretty(result));
        });
        Dictionary<string, string> Range() => new() { ["from"] = fromDate.Text, ["to"] = toDate.Text };
        page.Actions.Children.Add(Kit.Button("Trial balance", () => Report("report_trial_balance", new { as_of = toDate.Text })));
        page.Actions.Children.Add(Kit.Button("Profit and loss", () => Report("report_pnl", Range())));
        page.Actions.Children.Add(Kit.Button("Balance sheet", () => Report("report_balance_sheet", new { as_of = toDate.Text })));
        page.Actions.Children.Add(Kit.Button("GST and TDS", () => Report("report_tax_summary", Range())));
        page.Reload = () => Report("report_trial_balance", new { as_of = toDate.Text });
        page.Reload();
        return page;
    }

    public static UIElement Journals(AppShell shell) => Register(shell, "Journals", "journal_list", _ => new { from = Kit.YearStart(), to = Kit.Today(), limit = 200 }, row => Kit.Line(row, "number", "date", "narration", "total_display"), page =>
    {
        var date = Kit.Field(page.Form, "Date", Kit.Today());
        var narration = Kit.Field(page.Form, "Narration");
        var debit = Kit.Field(page.Form, "Debit account id");
        var credit = Kit.Field(page.Form, "Credit account id");
        var amount = Kit.Field(page.Form, "Amount", "0");
        page.OnSelect = row =>
        {
            var id = Kit.Id(row);
            if (id is not long journalId) return;
            shell.Run("Loading journal…", () =>
            {
                var lines = shell.Call("journal_lines", new { id = journalId });
                shell.Window.DispatcherQueue.TryEnqueue(() => page.Detail.Text = Kit.Pretty(lines));
            });
        };
        page.SaveAction = () =>
        {
            var posted = new
            {
                date = date.Text,
                narration = narration.Text,
                lines = new[]
                {
                    new { account_id = Kit.Long(debit.Text), debit = amount.Text, credit = "0", memo = "" },
                    new { account_id = Kit.Long(credit.Text), debit = "0", credit = amount.Text, memo = "" },
                }
            };
            Commit(shell, page, "Posting journal…", () => shell.Call("journal_post", posted));
        };
    });

    public static UIElement Expenses(AppShell shell) => Register(shell, "Expenses", "expense_list", _ => new { }, row => Kit.Line(row, "date", "vendor", "description", "total_display"), page =>
    {
        var date = Kit.Field(page.Form, "Date", Kit.Today());
        var description = Kit.Field(page.Form, "Description");
        var account = Kit.Field(page.Form, "Expense account id");
        var amount = Kit.Field(page.Form, "Amount", "0");
        var paid = Kit.Field(page.Form, "Paid via", "bank");
        page.SaveAction = () =>
        {
            var expense = new { date = date.Text, vendor_id = (long?)null, bill_ref = "", description = description.Text, expense_account_id = Kit.Long(account.Text), amount = amount.Text, gst = "0", tds = "0", paid_via = paid.Text };
            Commit(shell, page, "Recording expense…", () => shell.Call("expense_record", new { expense }));
        };
    });

    public static UIElement Income(AppShell shell) => Register(shell, "Rent and interest", "income_list", _ => new { }, row => Kit.Line(row, "date", "kind", "payer", "amount_display"), page =>
    {
        var date = Kit.Field(page.Form, "Date", Kit.Today());
        var kind = Kit.Field(page.Form, "Kind", "other");
        var payer = Kit.Field(page.Form, "Payer");
        var description = Kit.Field(page.Form, "Description");
        var amount = Kit.Field(page.Form, "Amount", "0");
        page.SaveAction = () =>
        {
            var income = new { date = date.Text, kind = kind.Text, property_id = (long?)null, payer = payer.Text, description = description.Text, amount = amount.Text, gst = "0", gst_mode = "intra", tds = "0", received_via = "bank" };
            Commit(shell, page, "Recording income…", () => shell.Call("income_record", new { income }));
        };
    });

    public static UIElement Salaries(AppShell shell) => Register(shell, "Salaries", "payroll_list", _ => new { }, row => Kit.Line(row, "date", "employee", "period_label", "net_display"), page =>
    {
        var date = Kit.Field(page.Form, "Date", Kit.Today());
        var employee = Kit.Field(page.Form, "Employee");
        var period = Kit.Field(page.Form, "Period");
        var gross = Kit.Field(page.Form, "Gross", "0");
        page.SaveAction = () =>
        {
            var payroll = new { date = date.Text, employee = employee.Text, period_label = period.Text, gross = gross.Text, tds = "0", paid_via = "bank", pf_employee = "0", pf_employer = "0", esi_employee = "0", esi_employer = "0", professional_tax = "0" };
            Commit(shell, page, "Recording salary…", () => shell.Call("payroll_record", new { payroll }));
        };
    });

    public static UIElement Assets(AppShell shell) => Register(shell, "Fixed assets", "asset_list", _ => new { }, row => Kit.Line(row, "name", "purchase_date", "cost_display", "book_value_display"), page =>
    {
        var name = Kit.Field(page.Form, "Name");
        var date = Kit.Field(page.Form, "Purchase date", Kit.Today());
        var cost = Kit.Field(page.Form, "Cost", "0");
        var rate = Kit.Field(page.Form, "Depreciation rate %", "10");
        page.SaveAction = () =>
        {
            var asset = new { name = name.Text, purchase_date = date.Text, cost = cost.Text, rate_percent = rate.Text, paid_via = "bank" };
            Commit(shell, page, "Adding asset…", () => shell.Call("asset_add", new { asset }));
        };
        page.Actions.Children.Add(Kit.Button("Depreciate this year", () => shell.Run("Posting depreciation…", () => shell.Call("asset_depreciate", new { fy = Kit.FinancialYear() }))));
    });

    public static UIElement Tax(AppShell shell)
    {
        var page = Kit.Page("Tax payments");
        var kind = Kit.Field(page.Form, "Kind", "gst");
        var date = Kit.Field(page.Form, "Date", Kit.Today());
        var amount = Kit.Field(page.Form, "Amount", "0");
        var narration = Kit.Field(page.Form, "Narration");
        page.SaveAction = () =>
        {
            var payment = new { kind = kind.Text, date = date.Text, amount = amount.Text, paid_via = "bank", narration = narration.Text };
            Commit(shell, page, "Recording tax payment…", () => shell.Call("tax_payment", payment));
        };
        return page;
    }

    public static UIElement Accounts(AppShell shell) => Register(shell, "Chart and financial years", "account_list", _ => new { as_of = Kit.Today() }, row => Kit.Line(row, "code", "name", "kind", "balance_display"), page =>
    {
        var code = Kit.Field(page.Form, "Code");
        var name = Kit.Field(page.Form, "Name");
        var kind = Kit.Field(page.Form, "Kind", "expense");
        page.SaveAction = () =>
        {
            var created = new { code = code.Text, name = name.Text, kind = kind.Text };
            Commit(shell, page, "Creating account…", () => shell.Call("account_create", created));
        };
        page.Actions.Children.Add(Kit.Button("Financial years", () => shell.Run("Loading years…", () =>
        {
            var years = shell.Call("period_list");
            shell.Window.DispatcherQueue.TryEnqueue(() => page.Detail.Text = Kit.Pretty(years));
        })));
    });

    public static UIElement Templates(AppShell shell) => Register(shell, "Templates", "template_list", _ => new { kind = (string?)null, include_archived = false }, row => Kit.Line(row, "family", "name", "format", "latest_version"), page =>
    {
        var body = Kit.Field(page.Form, "Body");
        body.AcceptsReturn = true;
        body.Height = 180;
        var layout = "letter";
        var kind = "letter_out";
        var name = "";
        page.OnSelect = row =>
        {
            body.Text = Kit.Str(row, "body");
            layout = Kit.Str(row, "layout");
            kind = Kit.Str(row, "kind");
            name = Kit.Str(row, "name");
            if (Kit.Str(row, "format") == "html" && Kit.Id(row) is long templateId)
            {
                shell.Run("Opening template…", () =>
                {
                    var state = shell.Call("studio_open", new { template_id = templateId });
                    shell.Window.DispatcherQueue.TryEnqueue(() => body.Text = state.TryGetProperty("html", out var html) ? html.GetString() ?? "" : body.Text);
                });
            }
        };
        page.PrintAction = () =>
        {
            var text = body.Text;
            shell.Run("Rendering preview…", () =>
            {
                var preview = shell.Call("template_preview", new { body = text, layout, kind, name });
                var encoded = preview.TryGetProperty("pdf_base64", out var pdf) ? pdf.GetString() ?? "" : "";
                shell.Window.DispatcherQueue.TryEnqueue(async () => await Kit.SavePdf(shell, encoded, "template-preview"));
            });
        };
    });

    public static UIElement Company(AppShell shell)
    {
        var page = Kit.Page("Company profile");
        var fields = new Dictionary<string, TextBox>();
        foreach (var (key, label) in new[]
        {
            ("name", "Name"), ("address", "Address"), ("email", "Email"), ("phone", "Phone"), ("pan", "PAN"), ("gstin", "GSTIN"),
            ("coa_registration", "COA registration"), ("bank_details", "Bank details"), ("gst_scheme", "GST scheme"),
            ("upi_id", "UPI id"), ("architect_name", "Architect"), ("letterhead_style", "Letterhead"),
        })
            fields[key] = Kit.Field(page.Form, label);
        page.Reload = () => shell.Run("Loading profile…", () =>
        {
            var profile = shell.Call("company_get_profile");
            shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var (key, box) in fields)
                    box.Text = Kit.Str(profile, key);
            });
        });
        page.SaveAction = () =>
        {
            var profile = fields.ToDictionary(pair => pair.Key, pair => pair.Value.Text);
            Commit(shell, page, "Saving profile…", () => shell.Call("company_save_profile", new { profile }));
        };
        page.Reload();
        return page;
    }

    public static UIElement Settings(AppShell shell)
    {
        var page = Kit.Page("Security, users and backup");
        var output = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        page.Form.Children.Add(output);
        var username = Kit.Field(page.Form, "New username");
        var password = Kit.Field(page.Form, "Password");
        var role = Kit.Field(page.Form, "Role", "accounts");
        var admin = Kit.Field(page.Form, "Your password");
        page.Actions.Children.Add(Kit.Button("Users", () => shell.Run("Loading users…", () => Show(shell, output, shell.Call("user_list")))));
        page.Actions.Children.Add(Kit.Button("Create user", () =>
        {
            var user = new { username = username.Text, password = password.Text, role = role.Text, admin_password = admin.Text };
            shell.Run("Creating user…", () => shell.Call("user_create", user));
        }));
        page.Actions.Children.Add(Kit.Button("Audit", () => shell.Run("Loading audit…", () => Show(shell, output, shell.Call("audit_list", new { limit = 100 })))));
        page.Actions.Children.Add(Kit.Button("Backup", () =>
        {
            var password = admin.Text;
            shell.Run("Creating backup…", () => Show(shell, output, shell.Call("backup_create", new { password })));
        }));
        page.Actions.Children.Add(Kit.Button("Backups", () => shell.Run("Loading backups…", () => Show(shell, output, shell.Call("backup_list")))));
        page.Reload = () => shell.Run("Loading security…", () =>
        {
            var minutes = shell.Call("security_get");
            shell.Window.DispatcherQueue.TryEnqueue(() => output.Text = $"Idle timeout: {minutes} minutes");
        });
        page.Reload();
        return page;
    }

    private static void Show(AppShell shell, TextBlock output, JsonElement value) =>
        shell.Window.DispatcherQueue.TryEnqueue(() => output.Text = Kit.Pretty(value));

    private static void Commit(AppShell shell, WorkPage page, string status, Action work)
    {
        _ = shell.Run(status, work).ContinueWith(task =>
        {
            if (task.Status == TaskStatus.RanToCompletion && task.Result)
                shell.Window.DispatcherQueue.TryEnqueue(() => page.Reload());
        });
    }

    private static WorkPage Register(AppShell shell, string title, string operation, Func<string, object> args, Func<JsonElement, string> label, Action<WorkPage> build)
    {
        var page = Kit.Page(title);
        page.SearchBox = new TextBox { PlaceholderText = "Search", Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(page.SearchBox, 0);
        page.ListPane.Children.Insert(0, page.SearchBox);
        page.Reload = () =>
        {
            var search = page.SearchBox?.Text.Trim() ?? "";
            shell.Run("Loading…", () =>
            {
                var rows = shell.Call(operation, args(search));
                shell.Window.DispatcherQueue.TryEnqueue(() => Kit.Fill(page, rows, label));
            });
        };
        page.SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
                page.Reload();
        };
        build(page);
        page.Reload();
        return page;
    }
}

internal static class Kit
{
    public static WorkPage Page(string title)
    {
        var page = new WorkPage();
        var root = new Grid { Margin = new Thickness(8) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var list = new Grid();
        list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        list.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var form = new StackPanel { Spacing = 8, Margin = new Thickness(16, 0, 0, 0) };
        var scroller = new ScrollViewer { Content = form };
        Grid.SetColumn(scroller, 1);
        root.Children.Add(list);
        root.Children.Add(scroller);
        form.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        page.ListPane = list;
        page.Form = form;
        page.List = new ListView { SelectionMode = ListViewSelectionMode.Single };
        Grid.SetRow(page.List, 1);
        page.List.SelectionChanged += (_, _) =>
        {
            if (page.List.SelectedItem is Row row)
                page.OnSelect?.Invoke(row.Data);
        };
        list.Children.Add(page.List);
        page.Actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        form.Children.Add(page.Actions);
        page.Detail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        form.Children.Add(page.Detail);
        page.Content = root;
        return page;
    }

    public static TextBox Field(Panel panel, string header, string value = "")
    {
        var box = new TextBox { Header = header, Text = value };
        panel.Children.Add(box);
        return box;
    }

    public static Button Button(string label, Action action)
    {
        var button = new Button { Content = label };
        button.Click += (_, _) => action();
        return button;
    }

    public static Button Button(string label, Func<Task> action)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) { /* the caller reports engine errors */ _ = ex; }
        };
        return button;
    }

    public static void Fill(WorkPage page, JsonElement rows, Func<JsonElement, string> label)
    {
        page.List.Items.Clear();
        if (rows.ValueKind != JsonValueKind.Array)
        {
            page.Detail.Text = Pretty(rows);
            return;
        }
        foreach (var row in rows.EnumerateArray())
            page.List.Items.Add(new Row { Label = label(row), Data = row });
    }

    public static string Line(JsonElement row, params string[] keys)
    {
        var parts = new List<string>();
        foreach (var key in keys)
        {
            var text = Str(row, key);
            if (text.Length > 0) parts.Add(text);
        }
        return parts.Count == 0 ? "Record" : string.Join(" · ", parts);
    }

    public static string Str(JsonElement row, string key)
    {
        if (!row.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
            return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    public static long? Id(JsonElement row)
    {
        if (!row.TryGetProperty("id", out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        return value.GetInt64();
    }

    public static long Long(string text) => long.TryParse(text.Trim(), out var value) ? value : 0;

    public static string Today() => DateTime.Today.ToString("yyyy-MM-dd");

    public static string YearStart()
    {
        var today = DateTime.Today;
        var year = today.Month >= 4 ? today.Year : today.Year - 1;
        return $"{year}-04-01";
    }

    public static string FinancialYear()
    {
        var today = DateTime.Today;
        var year = today.Month >= 4 ? today.Year : today.Year - 1;
        return $"{year}-{(year + 1) % 100:00}";
    }

    public static string Pretty(JsonElement value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });

    public static async Task SavePdf(AppShell shell, string base64, string name)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            shell.Problem("There is no PDF to save.");
            return;
        }
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { shell.Problem("The engine did not return a PDF."); return; }
        Windows.Storage.StorageFile? file;
        try { file = await FileDialogs.PickPdfDestination(shell.Window.Handle, name); }
        catch (Exception ex) { shell.Problem("The save picker could not be opened. " + ex.Message); return; }
        if (file == null) return;
        try { await FileIO.WriteBytesAsync(file, bytes); }
        catch (Exception ex) { shell.Problem("The PDF could not be saved. " + ex.Message); }
    }

    public static string? Blank(string search) => string.IsNullOrWhiteSpace(search) ? null : search;
}

internal sealed class Row
{
    public required string Label { get; init; }
    public required JsonElement Data { get; init; }
    public override string ToString() => Label;
}
