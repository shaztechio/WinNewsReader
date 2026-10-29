using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinNewsReader.App.ViewModels;
using WinNewsReader.Core;
using WinNewsReader.Core.ArticleRendering;

namespace WinNewsReader.App;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		// Unpackaged, WebView2 would otherwise keep its profile next to the executable, which may not be writable.
		Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(DataDirectory, "WebView2"));
	}

	/// <summary>
	/// <c>%LOCALAPPDATA%\WinNewsReader</c>, or the folder after <c>--data</c>: settings, <c>accounts.json</c>, the local
	/// account in <c>OnThisPC\</c> (shared with the <c>wnr</c> CLI) and sync accounts in <c>Accounts\</c>.
	/// </summary>
	public static string DataDirectory { get; } = DataDirectoryFromCommandLine()
		?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinNewsReader");

	private static string? DataDirectoryFromCommandLine()
	{
		var commandLine = Environment.GetCommandLineArgs();
		var index = Array.IndexOf(commandLine, "--data");
		return index >= 0 && index + 1 < commandLine.Length ? Path.GetFullPath(commandLine[index + 1]) : null;
	}

	private FeedDownloader _downloader = null!;
	private FaviconDownloader _favicons = null!;
	private AccountManager _accounts = null!;
	private AppSettings _settings = null!;
	private string _settingsPath = "";
	private ArticleThemes _themes = null!;
	private ArticleThemeInstaller _themeInstaller = null!;
	private ArticleExtractor _extractor = null!;
	private readonly ActivityLog _activity = new();
	private readonly List<MainWindow> _windows = [];

	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		_downloader = new FeedDownloader();
		_favicons = new FaviconDownloader(_downloader, Path.Combine(DataDirectory, "Favicons"));
		_accounts = new AccountManager(DataDirectory, _downloader, new WindowsCredentialStore());
		_accounts.LoadAll();
		_settingsPath = Path.Combine(DataDirectory, "settings.json");
		// Settings used to sit next to the local database.
		var oldSettingsPath = Path.Combine(DataDirectory, LocalAccount.LocalAccountId, "settings.json");
		if (!File.Exists(_settingsPath) && File.Exists(oldSettingsPath))
		{
			File.Move(oldSettingsPath, _settingsPath);
		}
		_settings = AppSettings.Load(_settingsPath);
		AddDefaultFeedsOnFirstLaunch(_accounts.LocalAccount, _settings, _settingsPath);
		// NetNewsWire's themes ship with the app; the reader's own go in the Themes folder.
		_themes = new ArticleThemes(Path.Combine(AppContext.BaseDirectory, "ArticleRendering", "Resources"), Path.Combine(DataDirectory, "Themes"));
		_themeInstaller = new ArticleThemeInstaller(_themes, _downloader);
		_extractor = new ArticleExtractor(_downloader);
		var window = OpenWindow(isPrimary: true);
		StartNotifications(window.ViewModel, window);
		window.Activate();
	}

	/// <summary>A main window over the shared accounts. Windows after the first start empty, a little below and to the right of the last.</summary>
	private MainWindow OpenWindow(bool isPrimary)
	{
		var viewModel = new MainViewModel(_accounts, _favicons, _themes, _themeInstaller, _extractor, _settings, _settingsPath, _activity, isPrimary);
		var window = new MainWindow(viewModel);
		if (!isPrimary && _windows.LastOrDefault() is { } previous)
		{
			var bounds = previous.AppWindow;
			window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(bounds.Position.X + 32, bounds.Position.Y + 32, bounds.Size.Width, bounds.Size.Height));
		}
		_windows.Add(window);
		window.NewWindowRequested += (_, _) => OpenWindow(isPrimary: false).Activate();
		window.Closed += (_, _) => OnWindowClosed(window);
		return window;
	}

	private void OnWindowClosed(MainWindow window)
	{
		_windows.Remove(window);
		if (_windows.Count == 0)
		{
			_accounts.Dispose();
			_downloader.Dispose();
		}
		else if (window.ViewModel.IsPrimary)
		{
			// The window the app opened with is gone; another takes over the refresh timer and notifications.
			var next = _windows[0];
			next.BecomePrimary();
			StartNotifications(next.ViewModel, next);
		}
	}

	/// <summary>New-article notifications, cleared when the window closes, since their buttons work only while it's open.</summary>
	private static void StartNotifications(MainViewModel viewModel, MainWindow window)
	{
		var notifications = new NewArticleNotifications(viewModel, window.DispatcherQueue);
		notifications.Invoked += (action, accountId, articleId) =>
		{
			viewModel.HandleNotification(action, accountId, articleId);
			if (action == NotificationAction.Open)
			{
				window.BringToFront();
			}
		};
		viewModel.AccountsChanged += (_, _) => notifications.WatchAccounts();
		try
		{
			notifications.Start();
		}
		catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
		{
			// Notifications are a nicety; the reader works without them.
			return;
		}
		window.Closed += (_, _) => notifications.Stop();
	}

	/// <summary>
	/// NetNewsWire's first-run feeds (parity row AC-13), from its <c>DefaultFeeds.opml</c>. Only for an empty account,
	/// and only once, so someone who deletes them all doesn't get them back. The launch refresh then fetches them.
	/// </summary>
	private static void AddDefaultFeedsOnFirstLaunch(LocalAccount account, AppSettings settings, string settingsPath)
	{
		if (settings.AddedDefaultFeeds)
		{
			return;
		}
		if (account.Store.GetFeeds().Count == 0)
		{
			account.ImportOpml(Path.Combine(AppContext.BaseDirectory, "Assets", "DefaultFeeds.opml"));
		}
		settings.AddedDefaultFeeds = true;
		settings.Save(settingsPath);
	}
}
