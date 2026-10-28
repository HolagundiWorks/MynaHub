using Hcw.Ui.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Osas.Core.Suite;

namespace Adms.Desktop;

/// <summary>
/// The company's users. They belong to the company file, so they are the same in every app; this is the one place they are managed.
/// Every change asks for the signed-in person's own password again, and the last active owner can never be removed.
/// </summary>
public sealed partial class CompanyPanel
{
    private readonly StackPanel _usersSection = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly StackPanel _userRows = new() { Spacing = 6 };
    private readonly TextBlock _usersNote = new() { Style = Resource<Style>("SecondaryStyle"), TextWrapping = TextWrapping.Wrap };

    private void BuildUsers()
    {
        var heading = new TextBlock { Text = "Users", Style = Resource<Style>("SectionTitleStyle"), FontSize = 16 };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level3);
        var add = new Button { Content = "Add a user…" };
        add.Click += async (_, _) => await AddUser();
        _usersSection.Children.Add(heading);
        _usersSection.Children.Add(new TextBlock
        {
            Text = "Who can sign in, with which role. These people are the same in every app. Each change asks for your own password again.",
            Style = Resource<Style>("SecondaryStyle"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        _usersSection.Children.Add(_usersNote);
        _usersSection.Children.Add(_userRows);
        _usersSection.Children.Add(add);
        Children.Add(_usersSection);
    }

    private void ClearUsers()
    {
        _userRows.Children.Clear();
        _usersSection.Visibility = Visibility.Collapsed;
    }

    private async Task LoadUsers()
    {
        var company = _company;
        if (company == null)
            return;
        IReadOnlyList<UserInfo> users;
        try
        {
            users = await Task.Run(company.Users);
        }
        catch (EngineException ex)
        {
            // Someone without the right to manage users simply does not see this section.
            _usersSection.Visibility = Visibility.Collapsed;
            _usersNote.Text = ex.Message;
            return;
        }
        _usersSection.Visibility = Visibility.Visible;
        _usersNote.Text = "";
        _userRows.Children.Clear();
        foreach (var user in users)
            _userRows.Children.Add(Row(user));
    }

    private UIElement Row(UserInfo user)
    {
        var line = new Grid { ColumnSpacing = 12 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock
        {
            Text = $"{user.Username} · {user.RoleLabel}{(user.Disabled ? " · cannot sign in" : "")}",
            Style = Resource<Style>("BodyStyle"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        var role = new Button { Content = "Change role…" };
        AutomationProperties.SetName(role, $"Change the role of {user.Username}");
        role.Click += async (_, _) => await ChangeRole(user);
        var password = new Button { Content = "Set password…" };
        AutomationProperties.SetName(password, $"Set a new password for {user.Username}");
        password.Click += async (_, _) => await SetPassword(user);
        var toggle = new Button { Content = user.Disabled ? "Allow sign-in" : "Stop sign-in" };
        AutomationProperties.SetName(toggle, $"{(user.Disabled ? "Allow" : "Stop")} sign-in for {user.Username}");
        toggle.Click += async (_, _) => await Toggle(user);
        var buttons = new FlexBar { Children = { role, password, toggle } };
        Grid.SetColumn(buttons, 1);
        line.Children.Add(label);
        line.Children.Add(buttons);
        return line;
    }

    private static ComboBox Roles(string header, string selected)
    {
        var box = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (key, label) in UserInfo.RoleLabels)
            box.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected) ?? box.Items[0];
        return box;
    }

    private async Task<bool> Ask(string title, string primary, params UIElement[] fields)
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        foreach (var field in fields)
            panel.Children.Add(field);
        var dialog = new ContentDialog { Title = title, Content = panel, PrimaryButtonText = primary, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = _window.Content.XamlRoot };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task Do(Action change, string success)
    {
        try
        {
            await Task.Run(change);
            Say(InfoBarSeverity.Success, success);
        }
        catch (EngineException ex)
        {
            Say(InfoBarSeverity.Error, ex.Message);
        }
        await LoadUsers();
    }

    private async Task AddUser()
    {
        var company = _company;
        if (company == null)
            return;
        var name = new TextBox { Header = "User name" };
        var role = Roles("Role", "accounts");
        var password = new PasswordBox { Header = "Password (12 or more characters)" };
        var confirm = new PasswordBox { Header = "Confirm the password" };
        var admin = new PasswordBox { Header = "Your password (to confirm)" };
        if (!await Ask("Add a user", "Add", name, role, password, confirm, admin))
            return;
        if (password.Password != confirm.Password)
        {
            Say(InfoBarSeverity.Error, "The two passwords do not match.");
            return;
        }
        var (user, secret, key, mine) = (name.Text, password.Password, (string)((ComboBoxItem)role.SelectedItem).Tag, admin.Password);
        await Do(() => company.CreateUser(user, secret, key, mine), $"{user.Trim()} was added.");
    }

    private async Task ChangeRole(UserInfo user)
    {
        var company = _company;
        if (company == null)
            return;
        var role = Roles($"New role for {user.Username}", user.Role);
        var admin = new PasswordBox { Header = "Your password (to confirm)" };
        if (!await Ask("Change role", "Change", role, admin))
            return;
        var (key, mine) = ((string)((ComboBoxItem)role.SelectedItem).Tag, admin.Password);
        await Do(() => company.UpdateUser(user.Id, key, user.Disabled, mine), $"{user.Username} is now {UserInfo.RoleLabels[key]}.");
    }

    private async Task SetPassword(UserInfo user)
    {
        var company = _company;
        if (company == null)
            return;
        var password = new PasswordBox { Header = $"New password for {user.Username} (12 or more characters)" };
        var confirm = new PasswordBox { Header = "Confirm the new password" };
        var admin = new PasswordBox { Header = "Your password (to confirm)" };
        if (!await Ask("Set a password", "Set password", password, confirm, admin))
            return;
        if (password.Password != confirm.Password)
        {
            Say(InfoBarSeverity.Error, "The two passwords do not match.");
            return;
        }
        var (secret, mine) = (password.Password, admin.Password);
        await Do(() => company.SetPassword(user.Id, secret, mine), $"{user.Username} has a new password. Tell them; it is not shown again.");
    }

    private async Task Toggle(UserInfo user)
    {
        var company = _company;
        if (company == null)
            return;
        var admin = new PasswordBox { Header = "Your password (to confirm)" };
        var verb = user.Disabled ? "Allow" : "Stop";
        if (!await Ask($"{verb} sign-in for {user.Username}?", verb, admin))
            return;
        var mine = admin.Password;
        await Do(() => company.UpdateUser(user.Id, user.Role, !user.Disabled, mine), user.Disabled ? $"{user.Username} can sign in again." : $"{user.Username} can no longer sign in.");
    }
}
