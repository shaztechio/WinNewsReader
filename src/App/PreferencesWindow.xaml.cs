using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinNewsReader.App.ViewModels;
using WinNewsReader.Core;
using WinNewsReader.Core.ArticleRendering;
using Windows.Graphics;
using WinRT.Interop;

namespace WinNewsReader.App;

/// <summary>
/// NetNewsWire's Preferences window: General (appearance, unread counts, article text size and theme, JavaScript,
/// browser, refresh interval) and Accounts (turn on or off, add, remove). Changes apply as they're made.
/// </summary>
public sealed partial class PreferencesWindow : Window
{
	[DllImport("user32.dll")]
	private static extern uint GetDpiForWindow(IntPtr window);

	private readonly MainViewModel _viewModel;

	/// <summary>Set while the controls are being filled from the settings, so that doesn't count as the reader choosing.</summary>
	private bool _loading;

	public PreferencesWindow(MainViewModel viewModel)
	{
		_viewModel = viewModel;
		InitializeComponent();
		AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
		SystemBackdrop = MicaController.IsSupported() ? new MicaBackdrop() : new DesktopAcrylicBackdrop();
		SizeAndCenter();
		WindowAppearance.Track(this, Root);
		WindowAppearance.Apply(this, Root, _viewModel.Settings.Appearance);

		FillChoices();
		Tabs.SelectedItem = Tabs.MenuItems[0];
		Load();

		_viewModel.AppearanceChanged += OnAppearanceChanged;
		_viewModel.AccountsChanged += OnAccountsChanged;
		// What the reader changed elsewhere, such as the theme from the +▾ menu, shows when they come back.
		Activated += (_, args) =>
		{
			if (args.WindowActivationState != WindowActivationState.Deactivated)
			{
				Load();
			}
		};
		Closed += (_, _) =>
		{
			_viewModel.AppearanceChanged -= OnAppearanceChanged;
			_viewModel.AccountsChanged -= OnAccountsChanged;
		};
	}

	private void SizeAndCenter()
	{
		var window = WindowNative.GetWindowHandle(this);
		var scale = GetDpiForWindow(window) / 96.0;
		var size = new SizeInt32((int)(700 * scale), (int)(620 * scale));
		AppWindow.Resize(size);
		var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
		AppWindow.Move(new PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
	}

	private void OnAppearanceChanged(object? sender, EventArgs e) => WindowAppearance.Apply(this, Root, _viewModel.Settings.Appearance);

	private void OnAccountsChanged(object? sender, EventArgs e) => LoadAccounts();

	private void OnTabChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
	{
		var accounts = (args.SelectedItem as NavigationViewItem)?.Tag as string == "Accounts";
		GeneralPanel.Visibility = accounts ? Visibility.Collapsed : Visibility.Visible;
		AccountsPanel.Visibility = accounts ? Visibility.Visible : Visibility.Collapsed;
	}

	// General

	/// <summary>The choices that don't change while the window is open.</summary>
	private void FillChoices()
	{
		_loading = true;
		Add(AppearanceBox, [("Same as Windows", AppAppearance.System), ("Light", AppAppearance.Light), ("Dark", AppAppearance.Dark)]);
		Add(TextSizeBox,
		[
			("Small", ArticleTextSize.Small),
			("Medium", ArticleTextSize.Medium),
			("Large", ArticleTextSize.Large),
			("Extra Large", ArticleTextSize.ExtraLarge),
			("Extra Extra Large", ArticleTextSize.ExtraExtraLarge),
		]);
		Add(RefreshBox, [.. RefreshIntervals.All.Select(interval => (interval.Name, (object)interval.Minutes))]);
		_loading = false;
	}

	private static void Add(ComboBox box, IEnumerable<(string Name, object Value)> choices)
	{
		foreach (var (name, value) in choices)
		{
			box.Items.Add(new ComboBoxItem { Content = name, Tag = value });
		}
	}

	private void Load()
	{
		_loading = true;
		Select(AppearanceBox, _viewModel.Settings.Appearance);
		CountsButtons.SelectedIndex = (int)_viewModel.Settings.UnreadCounts;
		Select(TextSizeBox, _viewModel.Settings.ArticleTextSize);
		LoadThemes();
		JavaScriptBox.IsChecked = _viewModel.IsArticleJavaScriptEnabled;
		LoadBrowsers();
		Select(RefreshBox, RefreshIntervals.Normalized(_viewModel.Settings.RefreshIntervalMinutes));
		_loading = false;
		LoadAccounts();
	}

	private static void Select(ComboBox box, object value) =>
		box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, value));

	private static T? Chosen<T>(ComboBox box) => box.SelectedItem is ComboBoxItem { Tag: T value } ? value : default;

	private void LoadThemes()
	{
		_viewModel.Themes.Reload();
		ThemeBox.Items.Clear();
		foreach (var theme in _viewModel.Themes.All)
		{
			ThemeBox.Items.Add(new ComboBoxItem { Content = theme.Name, Tag = theme.Name });
		}
		Select(ThemeBox, _viewModel.CurrentTheme.Name);
	}

	/// <summary>Windows' default first, then the browsers installed; the picked one stays listed even if it's gone.</summary>
	private void LoadBrowsers()
	{
		BrowserBox.Items.Clear();
		BrowserBox.Items.Add(new ComboBoxItem { Content = "Windows default", Tag = "" });
		var browsers = InstalledBrowsers.Find();
		foreach (var browser in browsers)
		{
			BrowserBox.Items.Add(new ComboBoxItem { Content = browser.Name, Tag = browser.Path });
		}
		if (_viewModel.Settings.BrowserPath is { } picked && browsers.All(browser => !browser.Path.Equals(picked, StringComparison.OrdinalIgnoreCase)))
		{
			BrowserBox.Items.Add(new ComboBoxItem { Content = Path.GetFileNameWithoutExtension(picked), Tag = picked });
		}
		Select(BrowserBox, _viewModel.Settings.BrowserPath ?? "");
	}

	private void OnAppearanceChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_loading && Chosen<AppAppearance?>(AppearanceBox) is { } appearance)
		{
			_viewModel.SetAppearance(appearance);
		}
	}

	private void OnCountsChanged(object sender, SelectionChangedEventArgs args)
	{
		if (!_loading && CountsButtons.SelectedIndex >= 0)
		{
			_viewModel.SetUnreadCountDisplay((UnreadCountDisplay)CountsButtons.SelectedIndex);
		}
	}

	private void OnTextSizeChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_loading && Chosen<ArticleTextSize?>(TextSizeBox) is { } size)
		{
			_viewModel.SetTextSize(size);
		}
	}

	private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_loading && Chosen<string>(ThemeBox) is { } name)
		{
			_viewModel.SetTheme(name);
		}
	}

	private void OnOpenThemesFolder(object sender, RoutedEventArgs e) => _viewModel.OpenThemesFolder();

	private void OnJavaScriptChanged(object sender, RoutedEventArgs e)
	{
		if (!_loading)
		{
			_viewModel.SetArticleJavaScriptEnabled(JavaScriptBox.IsChecked == true);
		}
	}

	private void OnBrowserChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_loading && Chosen<string>(BrowserBox) is { } path)
		{
			_viewModel.SetBrowser(path.Length == 0 ? null : path);
		}
	}

	private void OnRefreshChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_loading && Chosen<int?>(RefreshBox) is { } minutes)
		{
			_viewModel.SetRefreshInterval(minutes);
		}
	}

	// Accounts

	private void LoadAccounts()
	{
		var selected = (AccountsList.SelectedItem as ListViewItem)?.Tag as IAccount;
		AccountsList.Items.Clear();
		foreach (var account in _viewModel.AllAccounts)
		{
			var on = new ToggleSwitch { IsOn = _viewModel.IsAccountOn(account), OnContent = "On", OffContent = "Off", MinWidth = 0 };
			AutomationProperties.SetName(on, $"{account.Name} on");
			on.Toggled += (_, _) => _viewModel.SetAccountOn(account, on.IsOn);
			var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
			text.Children.Add(new TextBlock { Text = account.Name });
			text.Children.Add(new TextBlock
			{
				Text = account.Kind switch
				{
					AccountKind.Local => "Stored on this PC",
					AccountKind.FreshRss => "Syncs with a FreshRSS server",
					_ => "Syncs with a server",
				},
				Style = (Style)Application.Current.Resources["SecondaryCaptionStyle"],
			});
			var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 6, 4, 6) };
			row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			row.Children.Add(new FontIcon { Glyph = account.Kind == AccountKind.Local ? "" : "", FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
			Grid.SetColumn(text, 1);
			row.Children.Add(text);
			Grid.SetColumn(on, 2);
			row.Children.Add(on);
			var item = new ListViewItem { Content = row, Tag = account, HorizontalContentAlignment = HorizontalAlignment.Stretch };
			AccountsList.Items.Add(item);
			if (account == selected)
			{
				AccountsList.SelectedItem = item;
			}
		}
		RemoveButton.IsEnabled = AccountsList.SelectedItem is ListViewItem { Tag: IAccount { Kind: not AccountKind.Local } };
	}

	private void OnAccountSelected(object sender, SelectionChangedEventArgs e) =>
		RemoveButton.IsEnabled = AccountsList.SelectedItem is ListViewItem { Tag: IAccount { Kind: not AccountKind.Local } };

	private async void OnAddAccount(object sender, RoutedEventArgs e) => await AccountDialogs.ShowAddFreshRssAsync(Content.XamlRoot, _viewModel);

	private async void OnRemoveAccount(object sender, RoutedEventArgs e)
	{
		if (AccountsList.SelectedItem is ListViewItem { Tag: IAccount { Kind: not AccountKind.Local } account })
		{
			await AccountDialogs.ConfirmRemoveAsync(Content.XamlRoot, _viewModel, account);
		}
	}
}
