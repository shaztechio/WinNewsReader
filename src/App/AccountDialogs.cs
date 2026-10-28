using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsReader.App.ViewModels;
using WinNewsReader.Core;
using WinNewsReader.Sync.GoogleReader;

namespace WinNewsReader.App;

/// <summary>The account sheets, shared by the main window and the Preferences window, each showing them in its own content.</summary>
public static class AccountDialogs
{
	/// <summary>NetNewsWire's FreshRSS account sheet: server address, username and API password, checked before saving.</summary>
	public static Task ShowAddFreshRssAsync(XamlRoot xamlRoot, MainViewModel viewModel) => ShowFreshRssFormAsync(
		xamlRoot, "Add FreshRSS Account", "Add", name: null, server: "", username: "", passwordIsRequired: true,
		(_, server, username, password) => viewModel.AddFreshRssAccountAsync(server, username, password));

	/// <summary>
	/// NetNewsWire's credentials sheet for an account that exists: its name, server, username and password, checked
	/// before saving. The password can stay blank to keep the saved one.
	/// </summary>
	public static Task ShowEditFreshRssAsync(XamlRoot xamlRoot, MainViewModel viewModel, ReaderApiAccount account) => ShowFreshRssFormAsync(
		xamlRoot, "Edit Account", "Save", account.Name, account.Record.Endpoint, account.Record.Username, passwordIsRequired: false,
		(name, server, username, password) => viewModel.EditFreshRssAccountAsync(account, name, server, username, password));

	private static async Task ShowFreshRssFormAsync(
		XamlRoot xamlRoot, string title, string primaryText, string? name, string server, string username, bool passwordIsRequired,
		Func<string, string, string, string, Task> save)
	{
		var nameBox = new TextBox { Header = "Name", Text = name ?? "", Visibility = name is null ? Visibility.Collapsed : Visibility.Visible };
		var serverBox = new TextBox { Header = "Server", PlaceholderText = "https://freshrss.example.com", Text = server };
		var usernameBox = new TextBox { Header = "Username", Text = username };
		var passwordBox = new PasswordBox
		{
			Header = "API password",
			PlaceholderText = passwordIsRequired ? "" : "Leave blank to keep the saved password",
		};
		var help = new TextBlock
		{
			Text = "Use the API password from FreshRSS’s Settings → Profile, with “Allow API access” turned on in its authentication settings.",
			TextWrapping = TextWrapping.Wrap,
			Style = Style("SecondaryTextStyle"),
		};
		var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
		var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Style = Style("ErrorTextStyle") };
		var dialog = Dialog(xamlRoot, title, primaryText, new StackPanel { Spacing = 12, Width = 420, Children = { nameBox, serverBox, usernameBox, passwordBox, help, progress, error } });
		void UpdateEnabled() => dialog.IsPrimaryButtonEnabled =
			(name is null || nameBox.Text.Trim().Length > 0) && serverBox.Text.Trim().Length > 0 && usernameBox.Text.Trim().Length > 0
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
		await dialog.ShowAsync();
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
