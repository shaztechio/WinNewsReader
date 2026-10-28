using Microsoft.UI.Xaml;
using WinNewsReader.App.ViewModels;
using WinNewsReader.Core;
using WinNewsReader.Store;

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

	/// <summary><c>%LOCALAPPDATA%\WinNewsReader</c>, shared with the <c>wnr</c> CLI.</summary>
	public static string DataDirectory { get; } =
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinNewsReader");

	/// <summary><c>--db &lt;path&gt;</c> opens another database, as with <c>wnr</c>.</summary>
	private static string DatabasePath()
	{
		var commandLine = Environment.GetCommandLineArgs();
		var index = Array.IndexOf(commandLine, "--db");
		return index >= 0 && index + 1 < commandLine.Length
			? commandLine[index + 1]
			: Path.Combine(DataDirectory, "OnThisPC", "articles.db");
	}

	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		var databasePath = DatabasePath();
		var store = new ArticlesStore(databasePath);
		var downloader = new FeedDownloader();
		var favicons = new FaviconDownloader(downloader, Path.Combine(DataDirectory, "Favicons"));
		var account = new LocalAccount(store, downloader);
		// Settings sit next to the database, so --db runs don't disturb the everyday ones.
		var settingsPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "settings.json");
		var settings = AppSettings.Load(settingsPath);
		AddDefaultFeedsOnFirstLaunch(account, settings, settingsPath);
		_window = new MainWindow(new MainViewModel(account, favicons, settings, settingsPath));
		_window.Closed += (_, _) =>
		{
			downloader.Dispose();
			store.Dispose();
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
