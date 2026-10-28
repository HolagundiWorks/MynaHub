using System.Text.Json;
using Adms.Interop;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
    Task<bool> CanLeave() => Task.FromResult(true);
}

/// <summary>
/// A page that can show what another app has written, with nobody asking, when it has nothing unsaved. The workspace asks every
/// page that is showing; a page that is being edited says no and is left alone.
/// </summary>
internal interface ILiveRefresh
{
    bool CanRefreshLive { get; }

    void RefreshLive();
}

internal sealed class AppShell
{
    public required AdmsSession Session { get; init; }
    public required MainWindow Window { get; init; }
    public string Role { get; set; } = "";
    public IPageCommands? Page { get; set; }
    public Action<string>? Navigate { get; set; }
    public string? PendingCreate { get; set; }
    public long? PendingTemplateId { get; set; }
    /// <summary>Where a follow-through button wants the next page to land: a page, a record id, and what to do there.</summary>
    public (string Page, long Id, string Kind)? PendingOpen { get; set; }

    public bool CanEdit => Role != "Auditor";
    public bool CanIssue => Role is "Owner" or "Principal" or "Accounts";
    public bool CanPost => Role is "Owner" or "Accounts";
    public bool CanAudit => Role is "Owner" or "Auditor";
    public bool CanFinance => Role != "DocumentEditor";
    public bool CanManage => Role == "Owner";

    public JsonElement Call(string operation, object? args = null) => Session.Call(operation, args);

    public Task<bool> Run(string status, Action work) => Window.RunWork(status, work);

    public void Problem(string message) => Window.ShowProblem(message);

    public string LastProblem => Window.LastError;

    public Action? RefreshPaneLogo { get; set; }
}

internal sealed class WorkspaceView : UserControl
{
    private readonly AppShell _shell;
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly NavigationView _nav = new()
    {
        PaneDisplayMode = NavigationViewPaneDisplayMode.Auto,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsPaneToggleButtonVisible = true,
        IsPaneOpen = true,
        IsSettingsVisible = false,
        OpenPaneLength = 232,
        CompactModeThresholdWidth = 560,
        ExpandedModeThresholdWidth = 900,
    };
    private readonly Grid _host = new();
    private readonly Image _paneLogo = new()
    {
        Source = BrandArt.Mark(),
        Width = 48,
        Height = 48,
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Left,
    };
    private readonly List<(string Id, string Label)> _destinations = [];

    public IReadOnlyList<(string Id, string Label)> Destinations => _destinations;

    public void NewItem() => _shell.Page?.NewItem();
    public void Save() => _shell.Page?.Save();
    public void Print() => _shell.Page?.Print();
    public void FocusSearch() => _shell.Page?.FocusSearch();

    public Task<bool> ConfirmLeave() => _shell.Page?.CanLeave() ?? Task.FromResult(true);

    private long _lastSync = -1;
    private bool _syncing;
    private bool _stale;

    public WorkspaceView(AppShell shell)
    {
        _shell = shell;
        StartLiveSync();
        Hcw.Ui.SuiteNavigation.Apply(_nav, Hcw.Ui.SuiteApp.Studio);
        AutomationProperties.SetName(_paneLogo, "O-Studio");
        _nav.ItemInvoked += (_, args) =>
        {
            if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string id)
                Show(id);
        };
        _nav.PaneHeader = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(4, 8, 12, 8),
            Children =
            {
                _paneLogo,
            },
        };
        _shell.RefreshPaneLogo = LoadPaneLogo;
        LoadPaneLogo();
        _host.Background = Flow.Ink("SurfaceBackgroundBrush");
        var sheet = new Border
        {
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(8),
            BorderBrush = Flow.Ink("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            Background = Flow.Ink("SurfaceBackgroundBrush"),
            Child = _host,
        };
        _nav.Content = sheet;
        // In the narrow layout the pane's menu button sits over the top left of the page, so the page starts below it.
        _nav.DisplayModeChanged += (nav, _) => sheet.Margin = new Thickness(0, nav.DisplayMode == NavigationViewDisplayMode.Minimal ? 44 : 0, 0, 0);
        Content = _nav;
        _shell.Navigate = Show;
        BuildMenu();
        Show("dashboard");
    }

    private void LoadPaneLogo()
    {
        _ = _shell.Run("Loading the firm mark…", () =>
        {
            var stored = _shell.Call("company_logo");
            var square = MarkData(stored, "square");
            var wide = MarkData(stored, "wide");
            var useWide = square.Length == 0 && wide.Length > 0;
            var data = useWide ? wide : square;
            _shell.Window.DispatcherQueue.TryEnqueue(() => _ = ApplyPaneLogo(data, useWide));
        });
    }

    private async Task ApplyPaneLogo(string data, bool wide)
    {
        if (data.Length == 0)
        {
            _paneLogo.Source = BrandArt.Mark();
            _paneLogo.Width = 48;
            _paneLogo.Height = 48;
            return;
        }
        try
        {
            var source = await BrandArt.Decode(data);
            _paneLogo.Source = source ?? BrandArt.Mark();
            _paneLogo.Width = source != null && wide ? 180 : 48;
            _paneLogo.Height = 48;
        }
        catch
        {
            _paneLogo.Source = BrandArt.Mark();
            _paneLogo.Width = 48;
            _paneLogo.Height = 48;
        }
    }

    private static string MarkData(JsonElement stored, string shape)
    {
        if (stored.ValueKind != JsonValueKind.Object || !stored.TryGetProperty(shape, out var mark))
            return "";
        return Kit.Str(mark, "present") is "true" or "True" ? Kit.Str(mark, "data_base64") : "";
    }

    /// <summary>
    /// The company file can be open in another app (O-CoreOp) at the same time. Every two seconds this asks the engine whether anyone else
    /// has written; if so, the page being shown refreshes itself, unless it has unsaved changes, in which case it refreshes once they are gone.
    /// Asking does not count as activity, so an idle session still locks.
    /// </summary>
    private void StartLiveSync()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += async (_, _) =>
        {
            if (_syncing)
                return;
            _syncing = true;
            try
            {
                var version = await Task.Run(() =>
                {
                    try
                    {
                        return _shell.Session.Call("watch", new { }).GetProperty("version").GetInt64();
                    }
                    catch (Exception ex) when (ex is AdmsException or KeyNotFoundException or InvalidOperationException)
                    {
                        return -1L;
                    }
                });
                if (version < 0)
                    return;
                if (_lastSync >= 0 && version != _lastSync)
                    _stale = true;
                _lastSync = version;
                if (_stale && _host.Children.FirstOrDefault() is ILiveRefresh page && page.CanRefreshLive)
                {
                    _stale = false;
                    page.RefreshLive();
                }
            }
            finally
            {
                _syncing = false;
            }
        };
        timer.Start();
    }

    private string _shown = "";

    public void Show(string id) => _ = ShowAsync(id);

    private async Task ShowAsync(string id)
    {
        if (id == _shown && _host.Children.Count > 0)
            return;
        if (_shell.Page != null && !await _shell.Page.CanLeave())
            return;
        _shown = id;
        _host.Children.Clear();
        var page = PageFor(id);
        _shell.Page = page as IPageCommands;
        _host.Children.Add(page);
        var marked = id == "template-editor" ? "templates" : id;
        foreach (var item in _nav.MenuItems.OfType<NavigationViewItem>())
            item.IsSelected = item.Tag as string == marked;
    }

    private void BuildMenu()
    {
        // Grouped by the job being done, not by the accounting ledger they belong to: what a practice does every day sits first.
        AddGroup("01", "Practice",
            ("dashboard", "01", "Pulse", "\uE9D2"),
            ("clients", "02", "Clients", "\uE716"),
            ("projects", "03", "Projects", "\uE8F1"),
            ("time", "04", "Time", "\uE823"),
            ("enquiries", "05", "Enquiries", "\uE77B"),
            ("site", "06", "Site records", "\uE7C1"),
            ("permits", "07", "Permits and approvals", "\uE8D7"),
            ("proposals", "08", "Fee proposals", "\uE8A5"),
            ("meetings", "09", "Meetings and decisions", "\uE787"));
        AddGroup("02", "Documents",
            ("documents", "01", "Letters and agreements", "\uE8A5"),
            ("transmittals", "02", "Transmittals", "\uE724"),
            ("templates", "03", "Templates", "\uE943"),
            ("boards", "04", "Project reports and presentations", "\uE786"));
        if (_shell.Role != "DocumentEditor")
        {
            AddGroup("03", "Billing",
                ("invoices", "01", "Invoices and receipts", "\uE8A1"),
                ("bank", "02", "Bank reconciliation", "\uE825"));
            AddGroup("04", "Books",
                ("acc-expenses", "01", "Expenses", "\uE8C7"),
                ("acc-income", "02", "Income", "\uEAFD"),
                ("acc-salaries", "03", "Salaries", "\uE77B"),
                ("acc-assets", "04", "Assets", "\uE7C5"),
                ("acc-journals", "05", "Journals", "\uE8A5"));
            AddGroup("05", "Reports and tax",
                ("acc-reports", "01", "Financial reports", "\uE9F9"),
                ("acc-tax", "02", "Tax", "\uE9D9"),
                ("tds26as", "03", "26AS check", "\uE9D9"),
                ("acc-setup", "03", "Chart and financial years", "\uE9D2"));
        }
        AddGroup("06", "Office",
            ("company", "01", "Firm", "\uE731"),
            ("staff", "02", "Staff", "\uE716"),
            ("leave", "03", "Leave", "\uE787"),
            ("attendance", "03", "Attendance", "\uE823"),
            ("library", "04", "Library", "\uE82D"),
            ("settings", "05", "Security and backup", "\uE713"));
    }

    private void AddTop(string id, string sheet, string label, string glyph)
    {
        _destinations.Add((id, label));
        _nav.MenuItems.Add(Item(id, label, glyph));
    }

    private void AddGroup(string sheet, string title, params (string Id, string Item, string Label, string Glyph)[] items)
    {
        _nav.MenuItems.Add(new NavigationViewItemHeader { Content = title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var (id, _, label, glyph) in items)
        {
            _destinations.Add((id, label));
            _nav.MenuItems.Add(Item(id, label, glyph));
        }
    }

    private static NavigationViewItem Item(string id, string label, string glyph) => new()
    {
        Content = label,
        Tag = id,
        MinHeight = 32,
        FontSize = 13,
        Icon = new FontIcon { Glyph = glyph, FontFamily = IconFont, FontSize = 16 },
    };

    private UIElement PageFor(string id) => id switch
    {
        "dashboard" => new DashboardPage(_shell),
        "documents" => new LettersPage(_shell),
        "transmittals" => TransmittalLog.Page(_shell),
        "enquiries" => RecordPages.Enquiries(_shell),
        "site" => RecordPages.Site(_shell),
        "permits" => RecordPages.Permits(_shell),
        "staff" => RecordPages.Staff(_shell),
        "leave" => RecordPages.Leave(_shell),
        "library" => RecordPages.Library(_shell),
        "proposals" => RecordPages.Proposals(_shell),
        "meetings" => RecordPages.Meetings(_shell),
        "attendance" => new AttendancePage(_shell),
        "tds26as" => new TdsReconPage(_shell),
        "clients" => new ClientsPage(_shell),
        "projects" => new ProjectsPage(_shell),
        "time" => TimeSheet.Page(_shell),
        "invoices" => new InvoicesPage(_shell),
        "bank" => new BankWorkspace(_shell),
        "acc-reports" => Pages.Reports(_shell),
        "acc-journals" => Ledgers.Journals(_shell),
        "acc-expenses" => Ledgers.Expenses(_shell),
        "acc-income" => Ledgers.Income(_shell),
        "acc-salaries" => Ledgers.Salaries(_shell),
        "acc-assets" => Ledgers.Assets(_shell),
        "acc-tax" => Ledgers.Tax(_shell),
        "acc-setup" => Ledgers.Accounts(_shell),
        "boards" => new BoardsPage(_shell),
        "templates" => new TemplateListPage(_shell),
        "template-editor" => new StudioEditor(_shell),
        "company" => Ledgers.Company(_shell),
        "settings" => Ledgers.Settings(_shell),
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
    public StackPanel Extra { get; set; } = new();
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
                shell.Window.DispatcherQueue.TryEnqueue(async () => await Kit.PreviewPdf(shell, encoded, "document"));
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
                shell.Window.DispatcherQueue.TryEnqueue(async () => await Kit.PreviewPdf(shell, encoded, "invoice"));
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
            shell.Window.DispatcherQueue.TryEnqueue(() => Kit.ShowPairs(page.Extra, "Reconciliation", summary,
            [
                ("Unmatched", "unmatched"),
                ("Partly matched", "partial"),
                ("Matched", "matched"),
                ("Ignored", "ignored"),
                ("Duplicate", "duplicate"),
                ("Rejected rows", "rejected_rows"),
                ("Unallocated credits", "unallocated_credits_display"),
            ]));
        })));
    });

    public static UIElement Reports(AppShell shell) => new ReportsPage(shell);

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
                shell.Window.DispatcherQueue.TryEnqueue(() => Kit.ShowTable(page.Extra, lines, "Journal lines", ["Account", "Memo", "Debit", "Credit"], ["account", "memo", "debit_display", "credit_display"], [false, false, true, true]));
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
            shell.Window.DispatcherQueue.TryEnqueue(() => Kit.ShowTable(page.Extra, years, "Financial years", ["Year", "Start", "End", "Status", "Journals"], ["fy", "start_date", "end_date", "status", "journals"], [false, false, false, false, true]));
        })));
    });

    public static UIElement Templates(AppShell shell) => new TemplateListPage(shell);

    public static UIElement Company(AppShell shell)
    {
        var page = Kit.Page("Company profile");
        var fields = new Dictionary<string, TextBox>();
        foreach (var (key, label) in new[]
        {
            ("name", "Name"), ("address", "Address"), ("email", "Email"), ("phone", "Phone"), ("pan", "PAN"), ("gstin", "GSTIN"),
            ("coa_registration", "COA registration"), ("bank_details", "Bank details"), ("gst_scheme", "GST scheme"),
            ("upi_id", "UPI id"), ("architect_name", "Architect"), ("letterhead_style", "Letterhead"), ("accent_hex", "Document accent"),
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
        var output = new StackPanel { Spacing = 8 };
        page.Form.Children.Add(output);
        var admin = Kit.Field(page.Form, "Your password");
        page.Actions.Children.Add(Kit.Button("Audit", () => shell.Run("Loading audit…", () => Show(shell, output, "Audit", shell.Call("audit_list", new { limit = 100 }), ["When", "Person", "Event", "Detail"], ["at", "user", "event", "detail"], [false, false, false, false]))));
        page.Actions.Children.Add(Kit.Button("Backup", () =>
        {
            var password = admin.Text;
            shell.Run("Creating backup…", () => Show(shell, output, "Backup", shell.Call("backup_create", new { password }), ["File", "SHA-256", "Bytes", "Folder"], ["filename", "sha256", "bytes", "folder"], [false, false, true, false]));
        }));
        page.Actions.Children.Add(Kit.Button("Backups", () => shell.Run("Loading backups…", () => Show(shell, output, "Backups", shell.Call("backup_list"), ["File", "Bytes"], ["filename", "bytes"], [false, true]))));
        page.Reload = () => shell.Run("Loading security…", () =>
        {
            var minutes = shell.Call("security_get");
            shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                output.Children.Clear();
                output.Children.Add(new TextBlock { Text = $"Idle timeout: {minutes} minutes", Style = Flow.Look("BodyStyle") });
            });
        });
        page.Reload();
        return page;
    }

    public static void Show(AppShell shell, Panel output, string name, JsonElement value, string[] headers, string[] keys, bool[] alignEnd) =>
        shell.Window.DispatcherQueue.TryEnqueue(() => Kit.ShowTable(output, value, name, headers, keys, alignEnd));

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
        page.SearchBox = new TextBox { PlaceholderText = "Search", Header = "Search", Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(page.SearchBox, $"Search {title}");
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
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = Flow.Heading(title);
        heading.Margin = (Thickness)Application.Current.Resources["PageMargin"];
        heading.Margin = new Thickness(heading.Margin.Left, heading.Margin.Top, heading.Margin.Right, 8);
        root.Children.Add(heading);
        var split = new Grid { Margin = new Thickness(24, 0, 24, 24) };
        var list = new Grid();
        list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        list.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var form = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        var scroller = Flow.Scroll(form);
        split.Children.Add(list);
        split.Children.Add(scroller);
        Grid.SetRow(split, 1);
        root.Children.Add(split);
        page.ListPane = list;
        page.Form = form;
        page.List = new ListView { SelectionMode = ListViewSelectionMode.Single };
        AutomationProperties.SetName(page.List, title);
        Flow.UseTable(page.List);
        Grid.SetRow(page.List, 1);
        page.List.SelectionChanged += (_, _) =>
        {
            if (page.List.SelectedItem is ListViewItem item && item.Tag is Row row)
                page.OnSelect?.Invoke(row.Data);
        };
        list.Children.Add(page.List);
        page.Actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        form.Children.Add(page.Actions);
        page.Detail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Style = Flow.Look("BodyStyle") };
        form.Children.Add(page.Detail);
        page.Extra = new StackPanel { Spacing = 8 };
        form.Children.Add(page.Extra);
        page.SizeChanged += (_, _) => Arrange(split, list, scroller, form, page.ActualWidth);
        Arrange(split, list, scroller, form, 1200);
        page.Content = root;
        return page;
    }

    private static void Arrange(Grid split, Grid list, ScrollViewer formScroll, StackPanel form, double width)
    {
        var narrow = width > 0 && width < 960;
        split.ColumnDefinitions.Clear();
        split.RowDefinitions.Clear();
        if (narrow)
        {
            split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(list, 0);
            Grid.SetRow(list, 0);
            Grid.SetColumn(formScroll, 0);
            Grid.SetRow(formScroll, 1);
            form.Margin = new Thickness(0, 16, 0, 0);
            form.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star), MinWidth = 320 });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 280 });
            Grid.SetRow(list, 0);
            Grid.SetColumn(list, 0);
            Grid.SetRow(formScroll, 0);
            Grid.SetColumn(formScroll, 1);
            form.Margin = new Thickness(24, 0, 0, 0);
            form.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
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

    public static void ShowTable(Panel host, JsonElement value, string name, string[] headers, string[] keys, bool[] alignEnd)
    {
        host.Children.Clear();
        var rows = new List<string[]>();
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in value.EnumerateArray())
                rows.Add(Cells(row, keys));
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            rows.Add(Cells(value, keys));
        }
        if (rows.Count == 0)
        {
            host.Children.Add(Flow.Secondary($"No {name.ToLowerInvariant()} to show."));
            return;
        }
        host.Children.Add(Flow.Table(name, headers, rows, alignEnd));
    }

    public static UIElement TableOf(JsonElement value, string name, string[] headers, string[] keys, bool[] alignEnd)
    {
        var host = new StackPanel();
        ShowTable(host, value, name, headers, keys, alignEnd);
        return host;
    }

    public static void ShowPairs(Panel host, string name, JsonElement value, (string Label, string Key)[] pairs)
    {
        host.Children.Clear();
        var rows = new List<string[]>();
        foreach (var (label, key) in pairs)
            rows.Add([label, Cell(value, key)]);
        host.Children.Add(Flow.Table(name, ["Item", "Value"], rows, [false, true]));
    }

    private static string[] Cells(JsonElement row, string[] keys)
    {
        var cells = new string[keys.Length];
        for (var i = 0; i < keys.Length; i++)
            cells[i] = Cell(row, keys[i]);
        return cells;
    }

    private static string Cell(JsonElement row, string key)
    {
        if (!row.TryGetProperty(key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return "";
        return value.ValueKind switch
        {
            JsonValueKind.True => "Yes",
            JsonValueKind.False => "No",
            JsonValueKind.String => value.GetString() ?? "",
            _ => value.ToString(),
        };
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
            page.List.Items.Add(new ListViewItem { Content = Flow.Columns(label(row)), Tag = new Row { Label = label(row), Data = row.Clone() } });
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

    public static async Task PreviewPdf(AppShell shell, string base64, string name)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            shell.Problem("There is no PDF to preview.");
            return;
        }
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { shell.Problem("The engine did not return a PDF."); return; }
        await shell.Window.ShowPdf(bytes, name);
    }

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
