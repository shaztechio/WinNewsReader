using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinNewsReader.Core;

/// <summary>Where the main window was and how big its panes were, so the next launch looks the same.</summary>
public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool IsMaximized, double SidebarWidth, double TimelineWidth);

/// <summary>
/// View settings and window state, kept as JSON next to the account's data. NetNewsWire keeps the same things in
/// user defaults and its window state restoration.
/// </summary>
public sealed class AppSettings
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

	/// <summary>Sidebar rows whose timelines hide read articles. NetNewsWire keeps this choice per row.</summary>
	public HashSet<string> HideReadArticlesKeys { get; set; } = [];

	public bool HideReadFeeds { get; set; }

	/// <summary>Light, Dark or System, for the whole app, articles included.</summary>
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public AppAppearance Appearance { get; set; }

	/// <summary>NetNewsWire's "Refresh Feeds" preference, in minutes; 0 means manually. Hourly by default.</summary>
	public int RefreshIntervalMinutes { get; set; } = RefreshIntervals.DefaultMinutes;

	/// <summary>NetNewsWire's "Unread Counts" preference.</summary>
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public UnreadCountDisplay UnreadCounts { get; set; }

	/// <summary>The browser that opens links, or null for Windows' default.</summary>
	public string? BrowserPath { get; set; }

	/// <summary>Accounts the reader has turned off: hidden from the sidebar and skipped when refreshing.</summary>
	public HashSet<string> DisabledAccountIds { get; set; } = [];

	/// <summary>NetNewsWire's View > Use Column Layout: the timeline as a table with sortable columns.</summary>
	public bool UseColumnLayout { get; set; }

	/// <summary>The column the table is sorted by. Date, the default, is the plain timeline's order too.</summary>
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public ArticleSortField SortField { get; set; }

	/// <summary>Whether a sort by Feed, Title, Unread or Starred runs backwards. Date has its own, <see cref="SortOldestFirst"/>.</summary>
	public bool SortFieldReversed { get; set; }

	/// <summary><c>ShowDebugMenu</c>: adds NetNewsWire's debugging items to the +▾ menu.</summary>
	public bool ShowDebugMenu { get; set; }

	/// <summary>NetNewsWire's "Group by Feed": the timeline lists each feed's articles together, feeds in name order.</summary>
	public bool GroupByFeed { get; set; }

	/// <summary><c>CorreiaSeparators</c>: a line between the timeline's articles.</summary>
	public bool TimelineSeparators { get; set; }

	/// <summary><c>sidebarFontSize</c> and <c>timelineFontSize</c>: the size of the text in those panes, in points; 0 keeps the default.</summary>
	public double SidebarFontSize { get; set; }

	public double TimelineFontSize { get; set; }

	/// <summary>The account and folder the last feed was added to, which New Feed offers when nothing in the sidebar says otherwise.</summary>
	public string? LastFeedAccountId { get; set; }

	public string? LastFeedFolderName { get; set; }

	/// <summary>NetNewsWire's Advanced preferences: check for updates at launch, and whether to follow the test channel.</summary>
	public bool CheckForUpdatesAutomatically { get; set; } = true;

	public bool UpdateTestBuilds { get; set; }

	/// <summary>NetNewsWire's Hide Status Bar state.</summary>
	public bool StatusBarHidden { get; set; }

	/// <summary>NetNewsWire's Hide Toolbar state.</summary>
	public bool ToolbarHidden { get; set; }

	// NetNewsWire's hidden preferences, which its users set with `defaults write`; here they're edited in settings.json.

	/// <summary><c>DevroeSuppressSyncOnLaunch</c>: don't refresh when the app starts.</summary>
	public bool SuppressRefreshOnLaunch { get; set; }

	/// <summary><c>KafasisTitleMode</c>: the window's title is the selected article's title.</summary>
	public bool ArticleTitleInWindowTitle { get; set; }

	/// <summary><c>GruberFeedDoubleClickMarkAsRead</c>: double-clicking a feed in the sidebar marks it read instead of opening its home page.</summary>
	public bool DoubleClickFeedMarksRead { get; set; }

	/// <summary>NetNewsWire's Toggle Sidebar state.</summary>
	public bool SidebarHidden { get; set; }

	/// <summary>NetNewsWire's "Sort Articles By" setting. Newest first by default.</summary>
	public bool SortOldestFirst { get; set; }

	/// <summary>The article theme's name; null means NetNewsWire's Default.</summary>
	public string? ArticleThemeName { get; set; }

	/// <summary>NetNewsWire's "Enable JavaScript" preference for article content. On by default.</summary>
	public bool ArticleContentJavaScriptEnabled { get; set; } = true;

	/// <summary>Feeds with NetNewsWire's "Show Notifications for New Articles" on, as <c>accountID|feedID</c>.</summary>
	public HashSet<string> NotificationFeedKeys { get; set; } = [];

	/// <summary>Feeds with NetNewsWire's "Always Use Reader View" on, as <c>accountID|feedID</c>.</summary>
	public HashSet<string> ReaderViewFeedKeys { get; set; } = [];

	/// <summary>NetNewsWire's article text size, Large by default.</summary>
	public ArticleRendering.ArticleTextSize ArticleTextSize { get; set; } = ArticleRendering.ArticleTextSize.Large;

	public WindowPlacement? Window { get; set; }

	public List<string>? ExpandedSidebarKeys { get; set; }

	public string? SelectedSidebarKey { get; set; }

	public string? SelectedArticleId { get; set; }

	/// <summary>Set once the default feeds have been offered, so deleting them all doesn't bring them back.</summary>
	public bool AddedDefaultFeeds { get; set; }

	/// <summary>The settings in <paramref name="path"/>, or defaults when the file is missing or unreadable.</summary>
	public static AppSettings Load(string path)
	{
		try
		{
			return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new() : new();
		}
		catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
		{
			return new();
		}
	}

	/// <summary>Writes to a temporary file first, so a crash mid-write can't leave half a file.</summary>
	public void Save(string path)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		var temporary = path + ".tmp";
		File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
		File.Move(temporary, path, overwrite: true);
	}
}
