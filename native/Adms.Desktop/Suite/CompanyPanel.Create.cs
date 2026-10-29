using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Osas.Core.Suite;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Adms.Desktop;

/// <summary>
/// Making a new company. The apps cannot make or choose one themselves, so it is done here: the company's name, its first user (the owner),
/// where to keep the file, and the recovery code the owner must save.
/// </summary>
public sealed partial class CompanyPanel
{
    private readonly Button _create = new() { Content = "Create a company…" };

    private async Task CreateCompany()
    {
        if (!Osas.Core.Suite.Engine.IsAvailable())
        {
            Say(InfoBarSeverity.Warning, "The company-file engine is missing beside O-Studio, so a company cannot be created here.");
            return;
        }
        var name = new TextBox { Header = "Company name" };
        var fileName = new TextBox { Header = "File name" };
        var folderText = new TextBlock { Text = "No folder chosen", TextWrapping = TextWrapping.Wrap };
        string? folder = null;
        name.TextChanged += (_, _) =>
        {
            if (fileName.FocusState == FocusState.Unfocused)
                fileName.Text = string.Concat(name.Text.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
        };
        var choose = new Button { Content = "Choose folder…" };
        choose.Click += async (_, _) =>
        {
            var picked = await FileDialogs.PickFolder(WindowNative.GetWindowHandle(_window));
            if (picked == null)
                return;
            folder = picked;
            folderText.Text = picked;
        };
        var owner = new TextBox { Header = "Owner's user name", Text = "owner" };
        var password = new PasswordBox { Header = "Owner's password (12 or more characters)" };
        var confirm = new PasswordBox { Header = "Confirm the password" };
        var review = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };

        // Three steps in one dialog: the company and where it is kept, the owner, then a review before anything is made.
        var step = 0;
        var titles = new[] { "Step 1 of 3: the company", "Step 2 of 3: the owner", "Step 3 of 3: review and create" };
        var pages = new StackPanel[]
        {
            new() { Spacing = 8, Children = { new TextBlock { Text = "Name the company and choose the folder that will hold its file.", TextWrapping = TextWrapping.Wrap }, name, fileName, choose, folderText } },
            new() { Spacing = 8, Children = { new TextBlock { Text = "The owner is the first person who can sign in and manage the company's users.", TextWrapping = TextWrapping.Wrap }, owner, password, confirm } },
            new() { Spacing = 8, Children = { review, new TextBlock { Text = "A recovery code is shown once after the company is created. Save it: it is the only way back into the owner's account if the password is forgotten.", TextWrapping = TextWrapping.Wrap } } },
        };
        var form = new StackPanel { Spacing = 12, MinWidth = 420 };
        var heading = new TextBlock { Style = Resource<Style>("SectionTitleStyle") };
        form.Children.Add(heading);
        foreach (var page in pages)
            form.Children.Add(page);
        form.Children.Add(error);
        var dialog = new ContentDialog { Title = "Create a company", Content = form, PrimaryButtonText = "Next", SecondaryButtonText = "Back", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = _window.Content.XamlRoot };
        string FullName()
        {
            var file = fileName.Text.Trim();
            return file.EndsWith(".osas", StringComparison.OrdinalIgnoreCase) ? file : file + ".osas";
        }
        void Show()
        {
            for (var n = 0; n < pages.Length; n++)
                pages[n].Visibility = n == step ? Visibility.Visible : Visibility.Collapsed;
            heading.Text = titles[step];
            dialog.PrimaryButtonText = step == pages.Length - 1 ? "Create company" : "Next";
            dialog.IsSecondaryButtonEnabled = step > 0;
            error.Text = "";
            if (step == pages.Length - 1)
                review.Text = string.Join(Environment.NewLine, $"Company: {name.Text.Trim()}", $"File: {(folder == null ? "" : Path.Combine(folder, FullName()))}", $"Owner: {owner.Text.Trim()}");
        }
        Show();
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            if (step > 0)
                step--;
            Show();
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (step == 0)
            {
                args.Cancel = true;
                if (name.Text.Trim().Length == 0 || fileName.Text.Trim().Length == 0 || folder == null)
                    error.Text = "Enter the company's name and a file name, and choose a folder.";
                else
                {
                    step = 1;
                    Show();
                }
            }
            else if (step == 1)
            {
                args.Cancel = true;
                if (owner.Text.Trim().Length == 0)
                    error.Text = "Enter the owner's user name.";
                else if (password.Password.Length < 12)
                    error.Text = "The password needs 12 or more characters.";
                else if (password.Password != confirm.Password)
                    error.Text = "The two passwords do not match.";
                else
                {
                    step = 2;
                    Show();
                }
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        var (companyName, ownerName, secret, again) = (name.Text.Trim(), owner.Text.Trim(), password.Password, confirm.Password);
        var target = Path.Combine(folder!, FullName());
        if (File.Exists(target))
        {
            Say(InfoBarSeverity.Error, "A file with that name already exists in the folder. Choose another name or folder.");
            return;
        }
        SuiteCompany company;
        string recovery;
        try
        {
            (company, recovery) = await Task.Run(() => SuiteCompany.Create(EngineFolder, target, companyName, ownerName, secret, again));
        }
        catch (EngineException ex)
        {
            Say(InfoBarSeverity.Error, ex.Message);
            return;
        }
        _company?.Dispose();
        _company = company;
        _settings.CompanyPath = company.Path;
        _settings.LastUser = company.User;
        _notice.IsOpen = false;
        Paint();
        Changed?.Invoke();
        _ = LoadUsers();
        await ShowRecovery(recovery);
        Say(InfoBarSeverity.Success, $"{company.Name} was created and you are signed in as {company.User}. Open an app below and it opens this company.");
    }

    private async Task ShowRecovery(string code)
    {
        var box = new TextBox { Text = code, IsReadOnly = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), Header = "Recovery code" };
        var copy = new Button { Content = "Copy to the clipboard" };
        copy.Click += (_, _) =>
        {
            var data = new DataPackage();
            data.SetText(code);
            Clipboard.SetContent(data);
        };
        var panel = new StackPanel { Spacing = 10, MinWidth = 400 };
        panel.Children.Add(new TextBlock
        {
            Text = "Save this code somewhere safe. It is shown only this once. It is the only way back into the owner's account if the password is forgotten.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(box);
        panel.Children.Add(copy);
        var dialog = new ContentDialog { Title = "Save the recovery code", Content = panel, PrimaryButtonText = "I have saved it", DefaultButton = ContentDialogButton.Primary, XamlRoot = _window.Content.XamlRoot };
        await dialog.ShowAsync();
    }
}
