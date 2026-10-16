using System.Text.Json;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

/// <summary>
/// Template editor. A template is written like a letter in a word processor: headings, emphasis, lists and fields.
/// Built-in templates stay as shipped; a copy is what can be changed.
/// </summary>
internal sealed class StudioEditor : UserControl, IPageCommands
{
    private static readonly HashSet<string> WordingFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "company.name", "company.address", "company.email", "company.phone", "company.pan", "company.gstin", "company.coa", "company.architect",
        "client.name", "client.address", "project.name", "project.code", "project.site",
        "document.number", "document.date", "document.subject", "document.reference",
        "party.name", "party.address", "agreement.fee", "company.bank", "company.upi",
    };

    private readonly AppShell _shell;
    private readonly TextBlock _heading = new() { Text = "Template", Style = (Style)Application.Current.Resources["PageTitleStyle"] };
    private readonly TextBlock _intro;
    private readonly TextBlock _status;
    private readonly TemplateSheet _sheet = new();
    private readonly WordRibbon _ribbon;
    private readonly ComboBox _fields = new() { Header = "Insert a field", PlaceholderText = "Insert a field", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _profile = new() { Header = "Preview sample", HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 220 };
    private readonly TextBox _copyName = new() { Header = "Name for a copy", PlaceholderText = "Our letter" };
    private readonly TextBox _note = new() { Header = "What changed (used when publishing)" };
    private readonly List<FieldChoice> _catalogue = new();
    private readonly Border _page;

    private bool _loading;
    private bool _sync;
    private bool _dirty;
    private bool _paperDirty;
    private long? _id;
    private bool _htmlFormat;
    private bool _builtIn;
    private bool _archived;
    private string _layout = "letter";
    private string _kind = "letter";
    private string _name = "";
    private string _sourceHtml = "";
    private string _sourceCss = "";
    private string _sourceBody = "";
    private bool _fieldsLoaded;

    public StudioEditor(AppShell shell)
    {
        _shell = shell;
        _intro = new TextBlock
        {
            Text = "Write the letter on the page. The template is saved as a Word document. The ribbon covers font, styles, alignment, tables, a horizontal line, and the header and footer. A built-in template stays as shipped; make a copy to change it.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["SecondaryStyle"],
        };
        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["SecondaryStyle"],
        };
        _page = new Border { Child = _sheet };
        _ribbon = new WordRibbon(
            Undo, Redo, ToggleBold, ToggleItalic, ToggleUnderline,
            () => Align(ParagraphAlignment.Left), () => Align(ParagraphAlignment.Center),
            () => Align(ParagraphAlignment.Right), () => Align(ParagraphAlignment.Justify),
            () => List(MarkerType.Bullet), () => List(MarkerType.Arabic),
            () => ApplyStyle(11, false), () => ApplyStyle(22, true), () => ApplyStyle(16, true), () => ApplyStyle(13, true),
            () => { _ = AddTableAsync(); }, () => _sheet.AddLine(), () => _sheet.AddHeader(), () => _sheet.AddFooter(),
            () => _sheet.RemoveSelected());

        _profile.Items.Add(new ComboBoxItem { Content = "Short sample", Tag = "short" });
        _profile.Items.Add(new ComboBoxItem { Content = "Long sample", Tag = "long" });
        _profile.Items.Add(new ComboBoxItem { Content = "This company", Tag = "company" });
        _profile.SelectedIndex = 0;

        var editor = new Grid { Margin = new Thickness(8) };
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editor.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(Kit.Button("Templates", () => _shell.Navigate?.Invoke("templates")));
        header.Children.Add(Flow.SheetMark("03.03", "Office", "Template"));
        header.Children.Add(_heading);
        header.Children.Add(_intro);
        editor.Children.Add(header);

        var save = new Button { Content = "Save", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        save.Click += (_, _) => Save();
        var actions = Flow.Bar(save, Kit.Button("Check", Validate), Kit.Button("Preview PDF", Print), Kit.Button("Publish", Publish), Kit.Button("Make a copy", Duplicate), Kit.Button("Delete", Delete), Kit.Button("New template", NewItem));
        actions.Margin = new Thickness(0, 8, 0, 8);
        Grid.SetRow(actions, 1);
        editor.Children.Add(actions);

        Grid.SetRow(_ribbon, 2);
        editor.Children.Add(_ribbon);

        Grid.SetRow(_page, 3);
        editor.Children.Add(_page);

        var footer = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        _profile.HorizontalAlignment = HorizontalAlignment.Stretch;
        footer.Children.Add(Flow.Pair(_fields, _profile));
        footer.Children.Add(Flow.Pair(_copyName, _note));
        footer.Children.Add(_status);
        Grid.SetRow(footer, 4);
        editor.Children.Add(footer);
        Content = editor;

        _sheet.Changed += () =>
        {
            if (!_loading)
                _dirty = true;
        };
        _sheet.SelectionChanged += () => ReflectSelection();
        _ribbon.Font.SelectionChanged += (_, _) =>
        {
            if (_sync || _loading || !_htmlFormat || !CanEdit())
                return;
            if (_ribbon.Font.SelectedItem is ComboBoxItem item && item.Tag is string face)
            {
                if (_sheet.ApplyFace(face))
                    return;
                var document = ActiveDocument();
                if (document != null)
                    document.Selection.CharacterFormat.Name = WordPaper.FaceName(face);
            }
        };
        _ribbon.Size.SelectionChanged += (_, _) =>
        {
            if (_sync || _loading || !_htmlFormat || !CanEdit())
                return;
            if (_ribbon.Size.SelectedItem is ComboBoxItem item && item.Tag is int size)
            {
                if (_sheet.ApplySize(size))
                    return;
                var document = ActiveDocument();
                if (document != null)
                    document.Selection.CharacterFormat.Size = size;
            }
        };
        _ribbon.Paper.SelectionChanged += (_, _) =>
        {
            if (_loading)
                return;
            _paperDirty = true;
            if (_htmlFormat)
                _sheet.SetPaper((_ribbon.Paper.SelectedItem as ComboBoxItem)?.Tag as string ?? "A4");
        };
        _fields.SelectionChanged += (_, _) =>
        {
            if (_fields.SelectedItem is ComboBoxItem item && item.Tag is string token && CanEdit())
            {
                if (_htmlFormat && _sheet.Insert(token))
                {
                    _fields.SelectedItem = null;
                    return;
                }
                var document = ActiveDocument();
                if (document == null)
                    return;
                document.Selection.SetText(TextSetOptions.None, token);
                var caret = document.Selection.EndPosition;
                document.Selection.SetRange(caret, caret);
                _fields.SelectedItem = null;
                FocusEditor?.Focus(FocusState.Programmatic);
            }
        };
        ApplyMode();
        if (_shell.PendingTemplateId is long pending)
        {
            _shell.PendingTemplateId = null;
            OpenById(pending);
        }
        else
            _status.Text = "Choose a template from the list.";
    }

    public void NewItem()
    {
        if (_shell.Role != "Owner")
        {
            _shell.Problem("Only the owner can create a template.");
            return;
        }
        var name = string.IsNullOrWhiteSpace(_copyName.Text) ? "Our letter" : _copyName.Text.Trim();
        _shell.Run("Creating template…", () =>
        {
            var id = _shell.Call("studio_create", new { name, family = "consultancy", kind = "letter" }).GetInt64();
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _status.Text = $"Created {name}.";
                _dirty = false;
                _paperDirty = false;
                OpenById(id);
            });
        });
    }

    public void Save()
    {
        if (_id is not long id)
            return;
        if (!CanEdit())
        {
            _shell.Problem(_builtIn
                ? "Built-in templates cannot be edited. Make a copy first."
                : "This template cannot be edited with your role.");
            return;
        }
        if (_htmlFormat)
        {
            var (html, css) = CurrentHtml();
            _shell.Run("Saving draft…", () =>
            {
                _shell.Call("studio_save_draft", new { template_id = id, html, css });
                _shell.Window.DispatcherQueue.TryEnqueue(() =>
                {
                    _sourceHtml = html;
                    _sourceCss = css;
                    _dirty = false;
                    _paperDirty = false;
                    _status.Text = "Draft saved as a Word document. Publish it when the letter is ready. Issued documents are not changed.";
                });
            });
            return;
        }
        if (!_dirty)
        {
            _status.Text = "There is nothing new to save.";
            return;
        }
        var body = _sheet.WriteWording();
        if (body.Length == 0)
            return;
        _shell.Run("Saving…", () =>
        {
            var version = _shell.Call("template_publish", new { template_id = id, body }).GetInt64();
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _sourceBody = body;
                _dirty = false;
                _status.Text = $"Saved as version {version}. Documents already issued are not changed.";
            });
        });
    }

    public void Print() => Preview();

    private async void Delete()
    {
        if (_id is not long id)
            return;
        if (_builtIn)
        {
            _shell.Problem("A built-in template stays as shipped. Make a copy, then delete the copy.");
            return;
        }
        if (!await Flow.Confirm(this, "Delete this template?", "It leaves the template list. Documents already issued stay as they were.", "Delete"))
            return;
        _dirty = false;
        _paperDirty = false;
        var removed = await _shell.Run("Deleting template…", () => _shell.Call("template_set_archived", new { template_id = id, archived = true }));
        if (removed)
            _shell.Navigate?.Invoke("templates");
    }

    public void FocusSearch() { }

    public Task<bool> CanLeave() => Flow.Discard(this, _dirty || _paperDirty);

    private void Validate()
    {
        if (!_htmlFormat || _id is null)
            return;
        var (html, css) = CurrentHtml();
        _shell.Run("Checking template…", () =>
        {
            var notes = _shell.Call("studio_validate", new { html, css });
            _shell.Window.DispatcherQueue.TryEnqueue(() => _status.Text = FormatNotes(notes));
        });
    }

    private void Preview()
    {
        if (_id is null)
            return;
        var profile = (_profile.SelectedItem as ComboBoxItem)?.Tag as string ?? "short";
        if (_htmlFormat)
        {
            var (html, css) = CurrentHtml();
            _shell.Run("Rendering preview…", () =>
            {
                var preview = _shell.Call("studio_preview", new { html, css, profile });
                var encoded = Kit.Str(preview, "pdf_base64");
                var notes = preview.TryGetProperty("diagnostics", out var diagnostics) ? FormatNotes(diagnostics) : "";
                _shell.Window.DispatcherQueue.TryEnqueue(async () =>
                {
                    _status.Text = string.IsNullOrWhiteSpace(notes) ? "Preview is ready." : notes;
                    if (encoded.Length > 0)
                        await Kit.PreviewPdf(_shell, encoded, "template-preview");
                });
            });
            return;
        }
        var body = _dirty ? _sheet.WriteWording() : _sourceBody;
        var layout = _layout;
        var kind = _kind;
        var name = _name;
        _shell.Run("Rendering preview…", () =>
        {
            var preview = _shell.Call("template_preview", new { body, layout, kind, name });
            var encoded = Kit.Str(preview, "pdf_base64");
            _shell.Window.DispatcherQueue.TryEnqueue(async () => await Kit.PreviewPdf(_shell, encoded, "template-preview"));
        });
    }

    private void Publish()
    {
        if (_id is not long id || !_htmlFormat || !CanEdit())
        {
            _shell.Problem(_htmlFormat
                ? "Make a copy of a built-in template before publishing."
                : "Save writes the next version of this wording.");
            return;
        }
        var summary = _note.Text.Trim();
        if (summary.Length == 0)
        {
            _shell.Problem("Write what changed in this version before publishing.");
            return;
        }
        var (html, css) = CurrentHtml();
        _shell.Run("Publishing…", () =>
        {
            _shell.Call("studio_save_draft", new { template_id = id, html, css });
            var version = _shell.Call("studio_publish", new { template_id = id, summary }).GetInt64();
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _status.Text = $"Published as version {version}. Documents already issued are not changed.";
                _note.Text = "";
                _dirty = false;
                _paperDirty = false;
                OpenId(id);
            });
        });
    }

    private void Duplicate()
    {
        if (_id is not long id)
            return;
        if (_shell.Role != "Owner")
        {
            _shell.Problem("Only the owner can copy a template.");
            return;
        }
        var name = _copyName.Text.Trim();
        if (name.Length == 0)
        {
            _shell.Problem("Enter a name for the copy.");
            return;
        }
        _shell.Run("Copying template…", () =>
        {
            var copy = _shell.Call("template_duplicate", new { template_id = id, name }).GetInt64();
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _dirty = false;
                _paperDirty = false;
                OpenById(copy);
            });
        });
    }

    private bool CanEdit() => _shell.Role == "Owner" && !_builtIn && !_archived && _id != null;

    private (string Html, string Css) CurrentHtml()
    {
        var paper = (_ribbon.Paper.SelectedItem as ComboBoxItem)?.Tag as string ?? "A4";
        if (!_dirty && !_paperDirty)
            return (_sourceHtml, _sourceCss);
        if (!_dirty && DocxFile.TryOpen(_sourceHtml, _sourceCss, out var inner, out var style))
            return (inner, WordPaper.WithPage(string.IsNullOrEmpty(style) ? WordPaper.DocumentCss(paper) : style, paper));
        if (!_dirty)
            return (_sourceHtml, WordPaper.WithPage(_sourceCss, paper));
        return (_sheet.Write(), WordPaper.DocumentCss(paper));
    }

    private void Open(JsonElement row)
    {
        _id = Kit.Id(row);
        _layout = Kit.Str(row, "layout");
        _kind = Kit.Str(row, "kind");
        _name = Kit.Str(row, "name");
        _builtIn = row.TryGetProperty("built_in", out var built) && built.ValueKind == JsonValueKind.True;
        _archived = row.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True;
        _htmlFormat = Kit.Str(row, "format") == "html";
        _heading.Text = _name.Length == 0 ? "Templates" : _name;
        _sourceBody = Kit.Str(row, "body");
        FillFieldList();
        ApplyMode();
        if (_htmlFormat && _id is long id)
        {
            OpenId(id);
            return;
        }
        ShowWording(_sourceBody);
        _status.Text = _builtIn
            ? "Built-in wording. Make a copy to edit it. A title, heading or subheading is kept; bold and italic are not stored in this kind of template."
            : "This wording is saved as the letter text. A title, heading or subheading is kept. Issued documents are not changed.";
    }

    private void OpenId(long id)
    {
        _shell.Run("Opening template…", () =>
        {
            var state = _shell.Call("studio_open", new { template_id = id });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (_id != id)
                    return;
                var html = Kit.Str(state, "html");
                var css = Kit.Str(state, "css");
                _sourceHtml = html;
                _sourceCss = css;
                _builtIn = state.TryGetProperty("built_in", out var built) && built.ValueKind == JsonValueKind.True;
                ApplyMode();
                ShowSheet(html, css);
                var published = Kit.Str(state, "published_version");
                var draft = state.TryGetProperty("has_draft", out var has) && has.ValueKind == JsonValueKind.True;
                _status.Text = _builtIn
                    ? "Built-in Word template. Make a copy to edit it."
                    : draft ? "This draft has not been published." : $"Published version {published}.";
            });
        });
    }

    private void ShowSheet(string html, string css)
    {
        _loading = true;
        _sheet.IsReadOnly = !CanEdit();
        var paper = _sheet.LoadDocument(html, css);
        SelectPaper(paper);
        _loading = false;
        _dirty = false;
        _paperDirty = false;
    }

    private void ShowWording(string body)
    {
        _loading = true;
        SelectPaper("A4");
        _sheet.SetPaper("A4");
        _sheet.IsReadOnly = !CanEdit();
        _sheet.LoadPlain(body);
        _loading = false;
        _dirty = false;
        _paperDirty = false;
    }

    private void SelectPaper(string paper)
    {
        foreach (var item in _ribbon.Paper.Items)
        {
            if (item is ComboBoxItem choice && choice.Tag is string tag && tag == paper)
            {
                _ribbon.Paper.SelectedItem = choice;
                return;
            }
        }
        _ribbon.Paper.SelectedIndex = 0;
    }

    private void OpenById(long id)
    {
        var loadFields = !_fieldsLoaded;
        _fieldsLoaded = true;
        _shell.Run("Opening template…", () =>
        {
            var rows = _shell.Call("template_list", new { kind = (string?)null, include_archived = false });
            var fields = loadFields ? _shell.Call("studio_fields") : default;
            JsonElement? match = null;
            if (rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    if (Kit.Id(row) == id)
                    {
                        match = row.Clone();
                        break;
                    }
                }
            }
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (loadFields)
                    RememberFields(fields);
                if (match is JsonElement row)
                    Open(row);
                else
                    _status.Text = "That template is no longer in the list.";
            });
        });
    }

    private void RememberFields(JsonElement fields)
    {
        _catalogue.Clear();
        if (fields.ValueKind != JsonValueKind.Array)
            return;
        foreach (var field in fields.EnumerateArray())
        {
            var path = Kit.Str(field, "path");
            var label = Kit.Str(field, "label");
            if (path.Length == 0)
                continue;
            var kind = Kit.Str(field, "kind");
            var token = kind == "list" ? "{{#each " + path + "}}\n{{/each}}" : "{{ " + path + " }}";
            _catalogue.Add(new FieldChoice
            {
                Label = string.IsNullOrEmpty(label) ? token : $"{label} — {token}",
                Token = token,
                Wording = WordingFields.Contains(path) && kind is not "list" and not "block",
            });
        }
        foreach (var (path, label) in new[]
        {
            ("company.coa", "COA registration"),
            ("company.architect", "Architect"),
            ("company.bank", "Bank details"),
            ("company.upi", "UPI"),
            ("project.site", "Site"),
            ("party.name", "Addressee"),
            ("party.address", "Addressee address"),
            ("agreement.fee", "Professional fee"),
        })
        {
            var token = "{{ " + path + " }}";
            if (_catalogue.Exists(field => field.Token == token))
                continue;
            _catalogue.Add(new FieldChoice { Label = $"{label} — {token}", Token = token, Wording = true });
        }
        FillFieldList();
    }

    private void FillFieldList()
    {
        _fields.Items.Clear();
        foreach (var field in _catalogue)
        {
            if (!_htmlFormat && !field.Wording)
                continue;
            _fields.Items.Add(new ComboBoxItem { Content = field.Label, Tag = field.Token });
        }
    }

    private void ApplyMode()
    {
        var edit = CanEdit();
        _sheet.IsReadOnly = !edit;
        _ribbon.SetEnabled(edit, _htmlFormat);
        _fields.IsEnabled = edit;
        _note.IsEnabled = edit && _htmlFormat;
        _page.Child = _sheet;
    }

    private RichEditBox? FocusEditor => _sheet.ActiveEditor;

    private RichEditTextDocument? ActiveDocument()
    {
        var editor = FocusEditor;
        return editor != null && editor.IsLoaded ? editor.Document : null;
    }

    private async Task AddTableAsync()
    {
        if (!CanEdit() || !_htmlFormat || XamlRoot == null)
            return;
        var rows = new NumberBox { Header = "Rows", Value = 3, Minimum = 1, Maximum = 12, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var columns = new NumberBox { Header = "Columns", Value = 3, Minimum = 1, Maximum = 8, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var header = new CheckBox { Content = "First row is a heading", IsChecked = true };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(rows);
        panel.Children.Add(columns);
        panel.Children.Add(header);
        var dialog = new ContentDialog
        {
            Title = "Add a table",
            Content = panel,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        _sheet.AddTable((int)rows.Value, (int)columns.Value, header.IsChecked == true);
    }

    private void ReflectSelection()
    {
        var document = ActiveDocument();
        if (_loading || document == null)
            return;
        _sync = true;
        try
        {
            var format = document.Selection.CharacterFormat;
            var face = WordPaper.FaceOf(format.Name);
            SelectTag(_ribbon.Font, face);
            var size = (int)Math.Round(format.Size);
            var matched = false;
            foreach (var item in _ribbon.Size.Items)
            {
                if (item is ComboBoxItem choice && choice.Tag is int value && value == size)
                {
                    _ribbon.Size.SelectedItem = choice;
                    matched = true;
                    break;
                }
            }
            if (!matched)
                _ribbon.Size.SelectedItem = null;
        }
        catch (Exception)
        {
            // The document is not ready yet.
        }
        finally
        {
            _sync = false;
        }
    }

    private static void SelectTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items)
        {
            if (item is ComboBoxItem choice && choice.Tag is string value && value == tag)
            {
                box.SelectedItem = choice;
                return;
            }
        }
    }

    private void Undo()
    {
        var document = ActiveDocument();
        if (document != null && document.CanUndo())
            document.Undo();
    }

    private void Redo()
    {
        var document = ActiveDocument();
        if (document != null && document.CanRedo())
            document.Redo();
    }

    private void ToggleBold() => SetEffect(format => format.Bold = format.Bold == FormatEffect.On ? FormatEffect.Off : FormatEffect.On);

    private void ToggleItalic() => SetEffect(format => format.Italic = format.Italic == FormatEffect.On ? FormatEffect.Off : FormatEffect.On);

    private void ToggleUnderline() => SetEffect(format => format.Underline = format.Underline == UnderlineType.None ? UnderlineType.Single : UnderlineType.None);

    private void SetEffect(Action<ITextCharacterFormat> apply)
    {
        var document = ActiveDocument();
        if (!_htmlFormat || !CanEdit() || document == null)
            return;
        apply(document.Selection.CharacterFormat);
        FocusEditor?.Focus(FocusState.Programmatic);
    }

    private void ApplyStyle(int size, bool bold)
    {
        var document = ActiveDocument();
        if (!CanEdit() || document == null)
            return;
        var selection = document.Selection;
        selection.Expand(TextRangeUnit.Paragraph);
        selection.CharacterFormat.Name = "Segoe UI";
        selection.CharacterFormat.Size = size;
        selection.CharacterFormat.Bold = bold ? FormatEffect.On : FormatEffect.Off;
        FocusEditor?.Focus(FocusState.Programmatic);
    }

    private void Align(ParagraphAlignment alignment)
    {
        var document = ActiveDocument();
        if (!_htmlFormat || !CanEdit() || document == null)
            return;
        document.Selection.ParagraphFormat.Alignment = alignment;
        FocusEditor?.Focus(FocusState.Programmatic);
    }

    private void List(MarkerType kind)
    {
        var document = ActiveDocument();
        if (!_htmlFormat || !CanEdit() || document == null)
            return;
        var paragraph = document.Selection.ParagraphFormat;
        paragraph.ListType = paragraph.ListType == kind ? MarkerType.None : kind;
        if (kind == MarkerType.Arabic)
            paragraph.ListStyle = MarkerStyle.Plain;
        FocusEditor?.Focus(FocusState.Programmatic);
    }

    private static string FormatNotes(JsonElement notes)
    {
        if (notes.ValueKind != JsonValueKind.Array || notes.GetArrayLength() == 0)
            return "No errors or warnings.";
        var lines = new List<string>();
        foreach (var note in notes.EnumerateArray())
        {
            var severity = Kit.Str(note, "severity");
            var source = Kit.Str(note, "source");
            var message = Kit.Str(note, "message");
            lines.Add($"{severity}: {message}" + (source.Length == 0 ? "" : $" ({source})"));
        }
        return string.Join("\n", lines);
    }

    private sealed class FieldChoice
    {
        public string Label = "";
        public string Token = "";
        public bool Wording;
    }
}

internal sealed class TemplateListPage : UserControl, IPageCommands
{
    private readonly AppShell _shell;
    private readonly TextBox _search = new() { PlaceholderText = "Name or family", Header = "Search" };
    private readonly TextBox _name = new() { Header = "Name for a new template", PlaceholderText = "Our letter", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
    private readonly TextBlock _empty = Flow.Secondary("No templates match.");
    private readonly List<JsonElement> _rows = [];

    public TemplateListPage(AppShell shell)
    {
        _shell = shell;
        var page = new Grid { Margin = (Thickness)Application.Current.Resources["PageMargin"], RowSpacing = 12 };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel { Spacing = 12 };
        header.Children.Add(Flow.SheetMark("03.02", "Office", "Templates"));
        header.Children.Add(Flow.Heading("Templates"));
        header.Children.Add(Flow.Secondary("Edit opens the template. Save is on that page. Delete removes a copy from the list. A built-in template stays as shipped."));
        header.Children.Add(_search);
        if (shell.Role == "Owner")
        {
            header.Children.Add(_name);
            header.Children.Add(Kit.Button("New template", NewItem));
        }
        page.Children.Add(header);

        _list.IsItemClickEnabled = false;
        var listHost = new Grid();
        listHost.Children.Add(_list);
        _empty.VerticalAlignment = VerticalAlignment.Top;
        _empty.Margin = new Thickness(0, 8, 0, 0);
        listHost.Children.Add(_empty);
        Grid.SetRow(listHost, 1);
        page.Children.Add(listHost);
        if (shell.Role == "Owner")
            Flow.Pin(page, ("New template", NewItem));
        Content = page;

        _search.TextChanged += (_, _) => Paint();
        Load();
    }

    public void NewItem()
    {
        if (_shell.Role != "Owner")
        {
            _shell.Problem("Only the owner can create a template.");
            return;
        }
        var name = string.IsNullOrWhiteSpace(_name.Text) ? "Our letter" : _name.Text.Trim();
        _shell.Run("Creating template…", () =>
        {
            var id = _shell.Call("studio_create", new { name, family = "consultancy", kind = "letter" }).GetInt64();
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _shell.PendingTemplateId = id;
                _shell.Navigate?.Invoke("template-editor");
            });
        });
    }

    public void Save() { }
    public void Print() { }
    public void FocusSearch() => _search.Focus(FocusState.Programmatic);

    private void Load()
    {
        _shell.Run("Loading templates…", () =>
        {
            var rows = _shell.Call("template_list", new { kind = (string?)null, include_archived = false });
            var copy = new List<JsonElement>();
            if (rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                    copy.Add(row.Clone());
            }
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _rows.Clear();
                _rows.AddRange(copy);
                Paint();
            });
        });
    }

    private void Paint()
    {
        var search = _search.Text.Trim();
        _list.Items.Clear();
        foreach (var row in _rows)
        {
            if (search.Length > 0 && !Kit.Line(row, "name", "family", "format", "kind").Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            if (Kit.Id(row) is not long id)
                continue;
            var name = Kit.Str(row, "name");
            var builtIn = row.TryGetProperty("built_in", out var built) && built.ValueKind == JsonValueKind.True;
            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = name, Style = Flow.Look("BodyStyle") });
            text.Children.Add(new TextBlock { Text = Detail(row), Style = Flow.Look("CaptionStyle") });
            var edit = new Button { Content = "Edit", MinWidth = 72 };
            edit.Click += (_, _) => OpenEditor(id);
            var delete = new Button { Content = "Delete", MinWidth = 72 };
            delete.Click += async (_, _) => await DeleteTemplate(id, name, builtIn);
            var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            commands.Children.Add(edit);
            commands.Children.Add(delete);
            var line = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 4, 0, 4) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(commands, 1);
            line.Children.Add(text);
            line.Children.Add(commands);
            _list.Items.Add(new ListViewItem { Content = line, Tag = id });
        }
        _empty.Visibility = _list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = _list.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenEditor(long id)
    {
        _shell.PendingTemplateId = id;
        _shell.Navigate?.Invoke("template-editor");
    }

    private async Task DeleteTemplate(long id, string name, bool builtIn)
    {
        if (_shell.Role != "Owner")
        {
            _shell.Problem("Only the admin can delete a template.");
            return;
        }
        if (builtIn)
        {
            _shell.Problem("A built-in template stays as shipped. Make a copy, then delete the copy.");
            return;
        }
        if (!await Flow.Confirm(this, "Delete this template?", $"{name} leaves the list. Documents already issued stay as they were.", "Delete"))
            return;
        var removed = await _shell.Run("Deleting template…", () => _shell.Call("template_set_archived", new { template_id = id, archived = true }));
        if (removed)
            Load();
    }

    private static string Detail(JsonElement row)
    {
        var kind = Kit.Str(row, "format") == "html" ? "Word document" : "Letter text";
        var family = Kit.Str(row, "family");
        var built = row.TryGetProperty("built_in", out var flag) && flag.ValueKind == JsonValueKind.True ? " · Built-in" : "";
        return string.IsNullOrEmpty(family) ? kind + built : $"{family} · {kind}{built}";
    }
}
