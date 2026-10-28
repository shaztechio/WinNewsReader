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
		var store = new ArticlesStore(DatabasePath());
		var downloader = new FeedDownloader();
		_window = new MainWindow(new MainViewModel(new LocalAccount(store, downloader)));
		_window.Closed += (_, _) =>
		{
			downloader.Dispose();
			store.Dispose();
		};
		_window.Activate();
	}
}
