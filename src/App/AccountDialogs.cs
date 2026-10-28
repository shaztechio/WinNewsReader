using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsReader.App.ViewModels;
using WinNewsReader.Core;

namespace WinNewsReader.App;

/// <summary>The account sheets, shared by the main window and the Preferences window, each showing them in its own content.</summary>
public static class AccountDialogs
{
	/// <summary>NetNewsWire's FreshRSS account sheet: server address, username and API password, checked before saving.</summary>
	public static async Task ShowAddFreshRssAsync(XamlRoot xamlRoot, MainViewModel viewModel)
	{
		var server = new TextBox { Header = "Server", PlaceholderText = "https://freshrss.example.com" };
		var username = new TextBox { Header = "Username" };
		var password = new PasswordBox { Header = "API password" };
		var help = new TextBlock
		{
			Text = "Use the API password from FreshRSS’s Settings → Profile, with “Allow API access” turned on in its authentication settings.",
			TextWrapping = TextWrapping.Wrap,
			Style = Style("SecondaryTextStyle"),
		};
		var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
		var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Style = Style("ErrorTextStyle") };
		var dialog = Dialog(xamlRoot, "Add FreshRSS Account", "Add", new StackPanel { Spacing = 12, Width = 420, Children = { server, username, password, help, progress, error } });
		void UpdateEnabled() => dialog.IsPrimaryButtonEnabled = server.Text.Trim().Length > 0 && username.Text.Trim().Length > 0 && password.Password.Length > 0;
		UpdateEnabled();
		server.TextChanged += (_, _) => UpdateEnabled();
		username.TextChanged += (_, _) => UpdateEnabled();
		password.PasswordChanged += (_, _) => UpdateEnabled();
		dialog.PrimaryButtonClick += async (_, args) =>
		{
			var deferral = args.GetDeferral();
			progress.Visibility = Visibility.Visible;
			error.Visibility = Visibility.Collapsed;
			dialog.IsPrimaryButtonEnabled = false;
			try
			{
				await viewModel.AddFreshRssAccountAsync(server.Text.Trim(), username.Text.Trim(), password.Password);
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
