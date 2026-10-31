using System.Runtime.InteropServices;
using Velopack;
using Velopack.Sources;

namespace WinNewsReader.App;

/// <summary>
/// NetNewsWire's app updates (Sparkle's job on the Mac): Velopack checks the release feed on the website, downloads what's
/// new, and restarts into it. It works only for the installed app; a build run from a folder has nothing to update.
/// </summary>
public sealed class UpdateService
{
	public const string FeedUrl = "https://winnewsreader.com/releases/";

	private readonly UpdateManager _manager;

	public UpdateService(bool testBuilds)
	{
		// Test builds are a channel of their own beside the release one, as NetNewsWire's are.
		_manager = new UpdateManager(new SimpleWebSource(FeedUrl), new UpdateOptions { ExplicitChannel = testBuilds ? "test" : "release" });
	}

	public bool IsInstalled => _manager.IsInstalled;

	/// <summary>True when the app runs from its Microsoft Store (MSIX) package, which the Store updates, not Velopack.</summary>
	public static bool IsStorePackage
	{
		get
		{
			var length = 0;
			// APPMODEL_ERROR_NO_PACKAGE (15700) means there is no package identity; anything else, including the
			// buffer being too small, means there is one.
			return GetCurrentPackageFullName(ref length, null) != 15700;
		}
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

	/// <summary>The installed version, or the one this build says it is when run from a folder.</summary>
	public static string CurrentVersion => typeof(UpdateService).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

	/// <summary>The newer release, or null if this is the latest.</summary>
	public async Task<UpdateInfo?> CheckAsync() => await _manager.CheckForUpdatesAsync();

	/// <summary>Downloads <paramref name="update"/> and restarts the app into it.</summary>
	public async Task InstallAsync(UpdateInfo update, Action<int>? progress = null)
	{
		await _manager.DownloadUpdatesAsync(update, progress);
		_manager.ApplyUpdatesAndRestart(update);
	}
}
