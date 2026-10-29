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
using WinNewsReader.Sync.GoogleReader;
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

	private readonly Func<Task> _checkForUpdates;

	private ScrollViewer AdvancedPanel { get; set; } = null!;

	public PreferencesWindow(MainViewModel viewModel, Func<Task> checkForUpdates)
	{
		_viewModel = viewModel;
		_checkForUpdates = checkForUpdates;
		InitializeComponent();
		AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
		SystemBackdrop = MicaController.IsSupported() ? new MicaBackdrop() : new DesktopAcrylicBackdrop();
		SizeAndCenter();
		WindowAppearance.Track(this, Root);
		WindowAppearance.Apply(this, Root, _viewModel.Settings.Appearance);

		FillChoices();
		AdvancedPanel = BuildAdvancedPanel();
		PanelHost.Children.Add(AdvancedPanel);
		Tabs.SelectedItem = Tabs.MenuItems[0];
		Load();

		_viewModel.AppearanceChanged += OnAppearanceChanged;
		_viewModel.AccountsChanged += OnAccountsChanged;
		// What the reader changed elsewhere, such as the theme from the View menu, shows when they come back.
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
		var tab = (args.SelectedItem as NavigationViewItem)?.Tag as string;
		GeneralPanel.Visibility = tab is "Accounts" or "Advanced" ? Visibility.Collapsed : Visibility.Visible;
		AccountsPanel.Visibility = tab == "Accounts" ? Visibility.Visible : Visibility.Collapsed;
		AdvancedPanel.Visibility = tab == "Advanced" ? Visibility.Visible : Visibility.Collapsed;
	}

	// Advanced: NetNewsWire's app updates, privacy policy link and crash reports, built here since there's little of it.

	private ScrollViewer BuildAdvancedPanel()
	{
		var settings = _viewModel.Settings;
		var panel = new StackPanel { Spacing = 14, Padding = new Thickness(32, 24, 32, 24), MaxWidth = 640 };

		panel.Children.Add(new TextBlock { Text = "Updates", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
		var automatic = new CheckBox { Content = "Check for updates automatically", IsChecked = settings.CheckForUpdatesAutomatically };
		automatic.Click += (_, _) =>
		{
			settings.CheckForUpdatesAutomatically = automatic.IsChecked == true;
			_viewModel.SaveSettings();
		};
		panel.Children.Add(automatic);
		var channel = new ComboBox { Header = "Update to", ItemsSource = new[] { "Release builds", "Test builds" }, SelectedIndex = settings.UpdateTestBuilds ? 1 : 0, MinWidth = 220 };
		channel.SelectionChanged += (_, _) =>
		{
			settings.UpdateTestBuilds = channel.SelectedIndex == 1;
			_viewModel.SaveSettings();
		};
		AutomationProperties.SetName(channel, "Update to");
		panel.Children.Add(channel);
		var check = new Button { Content = "Check for Updates Now" };
		check.Click += async (_, _) => await _checkForUpdates();
		panel.Children.Add(check);
		panel.Children.Add(new TextBlock
		{
			Text = $"You have WinNewsReader {UpdateService.CurrentVersion}.",
			Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
		});

		panel.Children.Add(new TextBlock { Text = "Privacy", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
		panel.Children.Add(new TextBlock
		{
			Text = "WinNewsReader collects nothing about you. It never sends crash reports on its own; one is saved on this PC, and the next start offers it to you.",
			TextWrapping = TextWrapping.Wrap,
		});
		panel.Children.Add(new HyperlinkButton { Content = "Privacy policy", NavigateUri = new Uri("https://winnewsreader.com/privacy/"), Padding = new Thickness(0) });
		var crashes = new Button { Content = "Open Crash Reports Folder" };
		crashes.Click += (_, _) =>
		{
			Directory.CreateDirectory(App.CrashDirectory);
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(App.CrashDirectory) { UseShellExecute = true });
		};
		panel.Children.Add(crashes);
		return new ScrollViewer { Content = panel, Visibility = Visibility.Collapsed, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
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
				Text = account is ReaderApiAccount sync ? $"{sync.Record.Username} on {ServerName(sync.Record.Endpoint)}" : "Stored on this PC",
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
		UpdateAccountButtons();
	}

	private static string ServerName(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : endpoint;

	private void OnAccountSelected(object sender, SelectionChangedEventArgs e) => UpdateAccountButtons();

	private void UpdateAccountButtons()
	{
		var sync = AccountsList.SelectedItem is ListViewItem { Tag: ReaderApiAccount };
		EditButton.IsEnabled = sync;
		RemoveButton.IsEnabled = sync;
	}

	private async void OnEditAccount(object sender, RoutedEventArgs e)
	{
		if (AccountsList.SelectedItem is ListViewItem { Tag: ReaderApiAccount account })
		{
			await AccountDialogs.ShowEditFreshRssAsync(Content.XamlRoot, _viewModel, account);
		}
	}

	private async void OnAddAccount(object sender, RoutedEventArgs e) => await AccountDialogs.ShowAddFreshRssAsync(Content.XamlRoot, _viewModel);

	private async void OnRemoveAccount(object sender, RoutedEventArgs e)
	{
		if (AccountsList.SelectedItem is ListViewItem { Tag: IAccount { Kind: not AccountKind.Local } account })
		{
			await AccountDialogs.ConfirmRemoveAsync(Content.XamlRoot, _viewModel, account);
		}
	}
}
