using Microsoft.UI.Xaml;
using WinNewsReader.App.ViewModels;
using WinNewsReader.Core;
using WinNewsReader.Core.ArticleRendering;

namespace WinNewsReader.App;

public partial class App : Application
{
	private MainWindow? _window;

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

	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		var downloader = new FeedDownloader();
		var favicons = new FaviconDownloader(downloader, Path.Combine(DataDirectory, "Favicons"));
		var accounts = new AccountManager(DataDirectory, downloader, new WindowsCredentialStore());
		accounts.LoadAll();
		var settingsPath = Path.Combine(DataDirectory, "settings.json");
		// Settings used to sit next to the local database.
		var oldSettingsPath = Path.Combine(DataDirectory, LocalAccount.LocalAccountId, "settings.json");
		if (!File.Exists(settingsPath) && File.Exists(oldSettingsPath))
		{
			File.Move(oldSettingsPath, settingsPath);
		}
		var settings = AppSettings.Load(settingsPath);
		AddDefaultFeedsOnFirstLaunch(accounts.LocalAccount, settings, settingsPath);
		// NetNewsWire's themes ship with the app; the reader's own go in the Themes folder.
		var themes = new ArticleThemes(Path.Combine(AppContext.BaseDirectory, "ArticleRendering", "Resources"), Path.Combine(DataDirectory, "Themes"));
		_window = new MainWindow(new MainViewModel(accounts, favicons, themes, new ArticleExtractor(downloader), settings, settingsPath));
		_window.Closed += (_, _) =>
		{
			accounts.Dispose();
			downloader.Dispose();
		};
		_window.Activate();
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
