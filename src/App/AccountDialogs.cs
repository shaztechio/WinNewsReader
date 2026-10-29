using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsReader.App.ViewModels;
using WinNewsReader.Core;
using Microsoft.UI.Xaml.Automation;

namespace WinNewsReader.App;

/// <summary>The account sheets, shared by the main window and the Preferences window, each showing them in its own content.</summary>
public static class AccountDialogs
{
	/// <summary>
	/// NetNewsWire's Add Account: first the service, from a list grouped as web and self-hosted, then that service's sign-in
	/// sheet, checked before saving.
	/// </summary>
	public static async Task ShowAddAsync(XamlRoot xamlRoot, MainViewModel viewModel)
	{
		while (await ChooseServiceAsync(xamlRoot) is { } kind)
		{
			if (await ShowFormAsync(xamlRoot, $"Add {AccountKinds.DisplayName(kind)} Account", "Add", kind, name: null, server: "", username: "", passwordIsRequired: true,
				(_, server, username, password) => viewModel.AddAccountAsync(kind, server, username, password)))
			{
				return;
			}
		}
	}

	/// <summary>
	/// NetNewsWire's credentials sheet for an account that exists: its name, server, username and password, checked
	/// before saving. The password can stay blank to keep the saved one.
	/// </summary>
	public static Task ShowEditAsync(XamlRoot xamlRoot, MainViewModel viewModel, ISyncAccount account) => ShowFormAsync(
		xamlRoot, "Edit Account", "Save", account.Record.Kind, account.Name, account.Record.Endpoint, account.Record.Username, passwordIsRequired: false,
		(name, server, username, password) => viewModel.EditAccountAsync(account, name, server, username, password));

	/// <summary>The list of services. Null if the reader cancelled.</summary>
	private static async Task<AccountKind?> ChooseServiceAsync(XamlRoot xamlRoot)
	{
		var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 360 };
		AutomationProperties.SetName(list, "Services");
		var kinds = new List<AccountKind>();
		foreach (var (group, groupKinds) in AccountKinds.Addable)
		{
			list.Items.Add(new ListViewItem { Content = new TextBlock { Text = group, Style = Style("SecondaryCaptionStyle") }, IsEnabled = false, IsTabStop = false });
			foreach (var kind in groupKinds)
			{
				list.Items.Add(new ListViewItem { Content = AccountKinds.DisplayName(kind), Tag = kind });
				kinds.Add(kind);
			}
		}
		var dialog = Dialog(xamlRoot, "Add Account", "Continue", list);
		dialog.IsPrimaryButtonEnabled = false;
		list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is ListViewItem { Tag: AccountKind };
		return await dialog.ShowAsync() == ContentDialogResult.Primary && list.SelectedItem is ListViewItem { Tag: AccountKind chosen } ? chosen : null;
	}

	/// <summary>What the sheet tells the reader about signing in to a service.</summary>
	private static string HelpFor(AccountKind kind) => kind switch
	{
		AccountKind.FreshRss => "Enter your FreshRSS server’s address; “/api/greader.php” is added for you. If your API is somewhere else, enter its full address ending in greader.php. Use the API password from FreshRSS’s Settings → Profile, with “Allow API access” turned on in its authentication settings.",
		AccountKind.TheOldReader => "Sign in with the email and password of your Old Reader account.",
		AccountKind.BazQux => "Sign in with the email and password of your BazQux account.",
		AccountKind.Feedbin => "Sign in with the email and password of your Feedbin account.",
		AccountKind.NewsBlur => "Sign in with your NewsBlur username and password.",
		_ => "",
	};

	/// <summary>The sign-in sheet; true once saved, false if the reader cancelled it.</summary>
	private static async Task<bool> ShowFormAsync(
		XamlRoot xamlRoot, string title, string primaryText, AccountKind kind, string? name, string server, string username, bool passwordIsRequired,
		Func<string, string, string, string, Task> save)
	{
		var needsServer = AccountKinds.NeedsServerAddress(kind);
		var nameBox = new TextBox { Header = "Name", Text = name ?? "", Visibility = name is null ? Visibility.Collapsed : Visibility.Visible };
		var serverBox = new TextBox { Header = "Server", PlaceholderText = "https://freshrss.example.com", Text = server, Visibility = needsServer ? Visibility.Visible : Visibility.Collapsed };
		var usernameBox = new TextBox { Header = AccountKinds.UsernameLabel(kind), Text = username };
		var passwordBox = new PasswordBox
		{
			Header = AccountKinds.PasswordLabel(kind),
			PlaceholderText = passwordIsRequired ? "" : "Leave blank to keep the saved password",
		};
		var help = new TextBlock { Text = HelpFor(kind), TextWrapping = TextWrapping.Wrap, Style = Style("SecondaryTextStyle") };
		var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
		var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Style = Style("ErrorTextStyle") };
		var dialog = Dialog(xamlRoot, title, primaryText, new StackPanel { Spacing = 12, Width = 420, Children = { nameBox, serverBox, usernameBox, passwordBox, help, progress, error } });
		void UpdateEnabled() => dialog.IsPrimaryButtonEnabled =
			(name is null || nameBox.Text.Trim().Length > 0) && (!needsServer || serverBox.Text.Trim().Length > 0) && usernameBox.Text.Trim().Length > 0
			&& (!passwordIsRequired || passwordBox.Password.Length > 0);
		UpdateEnabled();
		nameBox.TextChanged += (_, _) => UpdateEnabled();
		serverBox.TextChanged += (_, _) => UpdateEnabled();
		usernameBox.TextChanged += (_, _) => UpdateEnabled();
		passwordBox.PasswordChanged += (_, _) => UpdateEnabled();
		dialog.PrimaryButtonClick += async (_, args) =>
		{
			var deferral = args.GetDeferral();
			progress.Visibility = Visibility.Visible;
			error.Visibility = Visibility.Collapsed;
			dialog.IsPrimaryButtonEnabled = false;
			try
			{
				await save(nameBox.Text.Trim(), serverBox.Text.Trim(), usernameBox.Text.Trim(), passwordBox.Password);
			}
			catch (Exception ex)
			{
				error.Text = ex.Message;
				error.Visibility = Visibility.Visible;
				args.Cancel = true;
			}
			finally
			{
				progress.Visibility = Visibility.Collapsed;
				UpdateEnabled();
				deferral.Complete();
			}
		};
		return await dialog.ShowAsync() == ContentDialogResult.Primary;
	}

	/// <summary>Asks before removing a sync account; true once it's removed.</summary>
	public static async Task<bool> ConfirmRemoveAsync(XamlRoot xamlRoot, MainViewModel viewModel, IAccount account)
	{
		var dialog = Dialog(xamlRoot, "Remove Account", "Remove", new TextBlock
		{
			Text = $"Remove the “{account.Name}” account? Its feeds and articles stay on the server; only this PC’s copy goes.",
			TextWrapping = TextWrapping.Wrap,
		});
		if (await dialog.ShowAsync() != ContentDialogResult.Primary)
		{
			return false;
		}
		viewModel.RemoveAccount(account);
		return true;
	}

	private static ContentDialog Dialog(XamlRoot xamlRoot, string title, string primaryButtonText, object content) => new()
	{
		XamlRoot = xamlRoot,
		Title = title,
		Content = content,
		PrimaryButtonText = primaryButtonText,
		CloseButtonText = "Cancel",
		DefaultButton = ContentDialogButton.Primary,
		// A dialog doesn't take its window's Light or Dark on its own; it follows Windows'.
		RequestedTheme = (xamlRoot.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
	};

	private static Style Style(string key) => (Style)Application.Current.Resources[key];
}
