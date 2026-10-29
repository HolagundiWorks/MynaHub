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
        var owner = new TextBox { Header = "Owner's user name", Text = "owner" };
        var password = new PasswordBox { Header = "Owner's password (12 or more characters)" };
        var confirm = new PasswordBox { Header = "Confirm the password" };
        var panel = new StackPanel { Spacing = 10, MinWidth = 400 };
        panel.Children.Add(new TextBlock { Text = "The owner is the first person who can sign in and manage the company's users. Next you choose where the company file is kept.", TextWrapping = TextWrapping.Wrap });
        foreach (var field in new UIElement[] { name, owner, password, confirm })
            panel.Children.Add(field);
        var dialog = new ContentDialog { Title = "Create a company", Content = panel, PrimaryButtonText = "Choose where to keep it…", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = _window.Content.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        var (companyName, ownerName, secret, again) = (name.Text.Trim(), owner.Text.Trim(), password.Password, confirm.Password);
        if (companyName.Length == 0 || ownerName.Length == 0)
        {
            Say(InfoBarSeverity.Error, "Enter the company's name and the owner's user name.");
            return;
        }
        if (secret != again)
        {
            Say(InfoBarSeverity.Error, "The two passwords do not match.");
            return;
        }
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = companyName };
        picker.FileTypeChoices.Add("Company file", [".osas"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_window));
        var file = await picker.PickSaveFileAsync();
        if (file == null)
            return;
        // The picker makes an empty file to reserve the name; the engine refuses to overwrite, so take that placeholder away first.
        try
        {
            if (new FileInfo(file.Path).Length == 0)
                File.Delete(file.Path);
        }
        catch (IOException)
        {
        }
        SuiteCompany company;
        string recovery;
        try
        {
            (company, recovery) = await Task.Run(() => SuiteCompany.Create(EngineFolder, file.Path, companyName, ownerName, secret, again));
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
