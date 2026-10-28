using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinNewsReader.Core;
using WinNewsReader.Core.ArticleRendering;
using WinNewsReader.Store;

namespace WinNewsReader.App.ViewModels;

public enum ReaderViewState
{
	Off,
	Loading,
	On,
	Failed,
}

/// <summary>State and actions for the main window: sidebar, timeline and the selected article.</summary>
public sealed partial class MainViewModel : ObservableObject
{
	/// <summary>Favicon lookups at once; the downloader's per-host limits apply on top.</summary>
	private const int MaxConcurrentIconLookups = 6;

	private readonly AccountManager _accounts;
	private readonly FaviconDownloader _favicons;
	private readonly string _settingsPath;
	private readonly HashSet<string> _expandedKeys;
	private readonly Dictionary<string, ImageSource> _icons = [];
	private readonly Dictionary<string, string> _iconPaths = [];
	private bool _rebuildingSidebar;
	private bool _restoringSelection;

	public MainViewModel(AccountManager accounts, FaviconDownloader favicons, ArticleThemes themes, ArticleThemeInstaller themeInstaller, ArticleExtractor extractor, AppSettings settings, string settingsPath)
	{
		_accounts = accounts;
		_favicons = favicons;
		Themes = themes;
		ThemeInstaller = themeInstaller;
		_extractor = extractor;
		ReaderView = ReaderViewState.Off;
		Settings = settings;
		_settingsPath = settingsPath;
		_expandedKeys = settings.ExpandedSidebarKeys is { } keys
			? [.. keys]
			: [nameof(SidebarItemKind.SmartFeedsGroup), nameof(SidebarItemKind.AccountGroup)];
		Articles = [];
		StatusText = "";
		LoadSidebar();
	}

	public AppSettings Settings { get; }

	/// <summary>The made-up host the article page loads feed icons from, mapped to the favicon cache.</summary>
	public const string FeedIconsHost = "feed-icons.winnewsreader.example";

	public static string FeedIconsDirectory => Path.Combine(App.DataDirectory, "Favicons");

	/// <summary>Shown when a feed has no icon, so the header doesn't show a broken image.</summary>
	private const string TransparentPixel = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";

	public ArticleThemes Themes { get; }

	public ArticleTheme CurrentTheme => Themes.Named(Settings.ArticleThemeName);

	/// <summary>Raised when the theme or text size changes, so the article view renders the article again.</summary>
	public event EventHandler? ArticleAppearanceChanged;

	public void SetTheme(string name)
	{
		Settings.ArticleThemeName = name == ArticleTheme.DefaultName ? null : name;
		SaveSettings();
		ArticleAppearanceChanged?.Invoke(this, EventArgs.Empty);
	}

	public bool IsArticleJavaScriptEnabled => Settings.ArticleContentJavaScriptEnabled;

	/// <summary>NetNewsWire's "Enable JavaScript": applies from the next article shown, so the current one is shown again.</summary>
	public void SetArticleJavaScriptEnabled(bool enabled)
	{
		Settings.ArticleContentJavaScriptEnabled = enabled;
		SaveSettings();
		OnPropertyChanged(nameof(IsArticleJavaScriptEnabled));
		ArticleAppearanceChanged?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>
	/// NetNewsWire's <c>WebViewConfiguration.allowsContentJavaScript</c>: whether the article's own scripts may run. Off
	/// when the setting is off, and always for sites whose articles break with it (by link, feed URL or home page).
	/// The app's page scripts run either way.
	/// </summary>
	public bool AllowsContentJavaScript(ArticleItem? item)
	{
		if (!Settings.ArticleContentJavaScriptEnabled)
		{
			return false;
		}
		if (item is null)
		{
			return true;
		}
		var feed = FeedFor(item);
		return !ArticleRenderingSpecialCases.ShouldDisableJavaScript(item.LinkUrl, feed?.Url, feed?.HomePageUrl);
	}

	public void SetTextSize(ArticleTextSize size)
	{
		Settings.ArticleTextSize = size;
		SaveSettings();
		ArticleAppearanceChanged?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Picks up themes added to or removed from the themes folder.</summary>
	public ArticleThemeInstaller ThemeInstaller { get; }

	public void ReloadThemes()
	{
		Themes.Reload();
		ArticleAppearanceChanged?.Invoke(this, EventArgs.Empty);
	}

	// Reader View, as NetNewsWire's MainWindowController runs it: per selected article, off again when the selection
	// changes, unless the article's feed has Always Use Reader View on.

	private readonly ArticleExtractor _extractor;
	private CancellationTokenSource? _extraction;
	private ExtractedArticle? _extracted;
	private string? _extractedArticleId;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsShowingReaderView), nameof(ReaderViewLabel))]
	public partial ReaderViewState ReaderView { get; private set; }

	public bool IsShowingReaderView => ReaderView == ReaderViewState.On;

	public bool CanUseReaderView => SelectedArticle?.LinkUrl is not null;

	public string ReaderViewLabel => ReaderView switch
	{
		ReaderViewState.Loading => "Loading Reader View… (click to stop)",
		ReaderViewState.Failed => "Reader View Couldn’t Load: Try Again",
		ReaderViewState.On => "Hide Reader View",
		_ => "Show Reader View",
	};

	/// <summary>NetNewsWire's <c>toggleArticleExtractor</c>: stops a load, hides Reader View, or shows it, fetching it if needed.</summary>
	public void ToggleReaderView()
	{
		if (SelectedArticle is not { LinkUrl: not null } article)
		{
			return;
		}
		switch (ReaderView)
		{
			case ReaderViewState.Loading:
				CancelExtraction();
				ReaderView = ReaderViewState.Off;
				ArticleAppearanceChanged?.Invoke(this, EventArgs.Empty);
				break;
			case ReaderViewState.On:
				ReaderView = ReaderViewState.Off;
				ArticleAppearanceChanged?.Invoke(this, EventArgs.Empty);
				break;
			default:
				if (_extracted is not null && _extractedArticleId == article.ArticleId)
				{
					ReaderView = ReaderViewState.On;
					ArticleAppearanceChanged?.Invoke(this, EventArgs.Empty);
				}
				else
				{
					_ = ExtractAsync(article);
				}
				break;
		}
	}

	public bool IsReaderViewAlwaysOn(SidebarItem feedItem) => Settings.ReaderViewFeedKeys.Contains(ReaderViewKey(feedItem.Account!, feedItem.Feed!.FeedId));

	public void SetReaderViewAlwaysOn(SidebarItem feedItem, bool on)
	{
		var key = ReaderViewKey(feedItem.Account!, feedItem.Feed!.FeedId);
		if (on ? Settings.ReaderViewFeedKeys.Add(key) : Settings.ReaderViewFeedKeys.Remove(key))
		{
			SaveSettings();
		}
	}

	private static string ReaderViewKey(IAccount account, string feedId) => $"{account.AccountId}|{feedId}";

	private async Task ExtractAsync(ArticleItem article)
	{
		CancelExtraction();
		var extraction = _extraction = new CancellationTokenSource();
		_extracted = null;
		_extractedArticleId = article.ArticleId;
		ReaderView = ReaderViewState.Loading;
		ArticleAppearanceChanged?.Invoke(this, EventArgs.Empty);
		try
		{
			_extracted = await _extractor.ExtractAsync(article.LinkUrl!, extraction.Token);
			ReaderView = ReaderViewState.On;
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception ex) when (ex is ArticleExtractionException or HttpRequestException or IOException)
		{
			if (extraction.IsCancellationRequested)
			{
				return;
			}
			ReaderView = ReaderViewState.Failed;
			StatusText = ex.Message;
		}
		ArticleAppearanceChanged?.Invoke(this, EventArgs.Empty);
	}

	private void CancelExtraction()
	{
		_extraction?.Cancel();
		_extraction = null;
	}

	/// <summary>A different article turns Reader View off, or on again for a feed set to always use it. A reload of the same one leaves it be.</summary>
	partial void OnSelectedArticleChanged(ArticleItem? oldValue, ArticleItem? newValue)
	{
		OnPropertyChanged(nameof(CanUseReaderView));
		if (oldValue?.ArticleId == newValue?.ArticleId && oldValue?.Account == newValue?.Account)
		{
			return;
		}
		CancelExtraction();
		_extracted = null;
		_extractedArticleId = null;
		ReaderView = ReaderViewState.Off;
		if (newValue is { LinkUrl: not null } && Settings.ReaderViewFeedKeys.Contains(ReaderViewKey(newValue.Account, newValue.Article.FeedId)))
		{
			_ = ExtractAsync(newValue);
		}
	}

	/// <summary>The article page in the current theme, or the theme's empty page when nothing is selected.</summary>
	public string ArticlePageHtml(ArticleItem? item)
	{
		var theme = CurrentTheme;
		if (item is null)
		{
			return ArticleRenderer.PageHtml(ArticleRenderer.NoContent(theme), Themes.PageTemplate);
		}
		if (ReaderView == ReaderViewState.Loading && _extractedArticleId == item.ArticleId)
		{
			return ArticleRenderer.PageHtml(ArticleRenderer.Loading(theme), Themes.PageTemplate);
		}
		var extracted = ReaderView == ReaderViewState.On && _extractedArticleId == item.ArticleId ? _extracted : null;
		var avatar = _iconPaths.TryGetValue(IconKey(item.Account, item.Article.FeedId), out var path)
			? $"https://{FeedIconsHost}/{Uri.EscapeDataString(Path.GetFileName(path))}"
			: TransparentPixel;
		var context = new ArticleRenderContext(FeedFor(item), item.Account.Kind == AccountKind.FreshRss, Settings.ArticleTextSize.CssClass(), avatar);
		return ArticleRenderer.PageHtml(ArticleRenderer.Render(item.Article, context, theme, extracted), Themes.PageTemplate);
	}

	/// <summary>NetNewsWire's <c>todayCutoffDate</c>: 24 hours ago, so Today doesn't empty out at midnight.</summary>
	private static DateTimeOffset TodayCutoff => DateTimeOffset.Now.AddHours(-24);

	public IReadOnlyList<IAccount> Accounts => _accounts.Accounts;

	private LocalAccount LocalAccount => _accounts.LocalAccount;

	public System.Collections.ObjectModel.ObservableCollection<SidebarItem> SidebarItems { get; } = [];

	[ObservableProperty]
	public partial IReadOnlyList<ArticleItem> Articles { get; private set; }

	[ObservableProperty]
	public partial SidebarItem? SelectedSidebarItem { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSelectedArticle), nameof(ReadToggleLabel), nameof(StarToggleLabel))]
	public partial ArticleItem? SelectedArticle { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsNotRefreshing))]
	public partial bool IsRefreshing { get; private set; }

	[ObservableProperty]
	public partial string StatusText { get; private set; }

	public bool IsNotRefreshing => !IsRefreshing;

	public bool HasSelectedArticle => SelectedArticle is not null;

	public string ReadToggleLabel => SelectedArticle is { IsRead: false } ? "Mark as Read" : "Mark as Unread";

	public string StarToggleLabel => SelectedArticle is { IsStarred: true } ? "Mark as Unstarred" : "Mark as Starred";

	/// <summary>Whether the selected row's timeline hides read articles. All Unread always does, so it can't be switched.</summary>
	public bool IsHidingReadArticles => SelectedSidebarItem is { } item && HidesReadArticles(item);

	public bool CanHideReadArticles => SelectedSidebarItem is { IsGroup: false, Kind: not SidebarItemKind.AllUnread };

	public bool HideReadFeeds => Settings.HideReadFeeds;

	public bool SortNewestFirst => !Settings.SortOldestFirst;

	public bool SortOldestFirst => Settings.SortOldestFirst;

	public static IEnumerable<StoredFolder> FoldersOf(IAccount account) => account.Store.GetFolders().SortedByName();

	/// <summary>The account a new feed goes in by default: the selected row's, or On This PC.</summary>
	public IAccount DefaultAccountForNewFeed => SelectedSidebarItem?.Account ?? LocalAccount;

	/// <summary>The folder a new feed goes in by default: the selected folder, or the folder of the selected feed.</summary>
	public StoredFolder? DefaultFolderForNewFeed => SelectedSidebarItem?.Folder ?? SelectedSidebarItem?.ParentFolder;

	/// <summary>
	/// Rebuilds the sidebar from the stores, keeping expanded folders and the selection, or selecting the row
	/// <paramref name="select"/> instead.
	/// </summary>
	public void LoadSidebar(string? select = null)
	{
		var previousKey = SelectedSidebarItem?.Key;
		var selectedKey = select ?? previousKey;
		// Hide Read Feeds leaves out feeds, and folders, with nothing unread, except the selected row.
		var unreadCounts = Accounts.ToDictionary(account => account, account => account.Store.UnreadCounts());
		bool Shown(SidebarItem item) =>
			!Settings.HideReadFeeds || item.Key == selectedKey
			|| (item.Feed is { } feed ? unreadCounts[item.Account!].ContainsKey(feed.FeedId) : item.Children.Count > 0);

		_rebuildingSidebar = true;
		SidebarItems.Clear();
		SidebarItems.Add(SidebarItem.Group(SidebarItemKind.SmartFeedsGroup, "Smart Feeds",
		[
			SidebarItem.SmartFeed(SidebarItemKind.Today, "Today"),
			SidebarItem.SmartFeed(SidebarItemKind.AllUnread, "All Unread"),
			SidebarItem.SmartFeed(SidebarItemKind.Starred, "Starred"),
		]));
		// One group per account. NetNewsWire sorts feeds alphabetically with folders at the end.
		foreach (var account in Accounts)
		{
			var store = account.Store;
			SidebarItems.Add(SidebarItem.Group(SidebarItemKind.AccountGroup, account.Name,
			[
				.. store.GetTopLevelFeeds().SortedByName().Select(feed => SidebarItem.ForFeed(feed, null, account)).Where(Shown),
				.. store.GetFolders().SortedByName().Select(folder =>
					SidebarItem.ForFolder(folder, store.GetFeeds(folder.FolderId).SortedByName().Select(feed => SidebarItem.ForFeed(feed, folder, account)).Where(Shown), account))
					.Where(Shown),
			], account));
		}
		foreach (var item in AllSidebarItems())
		{
			item.IsExpanded = _expandedKeys.Contains(item.Key);
			item.Icon = item.Feed is null ? null : _icons.GetValueOrDefault(IconKey(item.Account!, item.Feed.FeedId));
		}
		UpdateUnreadCounts();
		SidebarExpansionChanged?.Invoke(this, EventArgs.Empty);
		SelectedSidebarItem = FindSidebarItem(selectedKey);
		_rebuildingSidebar = false;
		LoadArticles(keepSelection: selectedKey == previousKey);
		_ = LoadIconsAsync();
	}

	/// <summary>Fetches favicons for feeds that don't have one yet. Misses are remembered by the downloader, so this is cheap to repeat.</summary>
	public async Task LoadIconsAsync()
	{
		var feeds = AllSidebarItems().Where(item => item.Feed is not null).Select(item => (Key: IconKey(item.Account!, item.Feed!.FeedId), Feed: item.Feed!))
			.DistinctBy(pair => pair.Key).Where(pair => !_icons.ContainsKey(pair.Key)).ToList();
		using var gate = new SemaphoreSlim(MaxConcurrentIconLookups);
		await Task.WhenAll(feeds.Select(async pair =>
		{
			var (key, feed) = pair;
			if ((feed.HomePageUrl ?? FaviconDownloader.SiteRoot(feed.Url)) is not { } site)
			{
				return;
			}
			string? path;
			await gate.WaitAsync();
			try
			{
				path = await Task.Run(() => _favicons.FaviconPathAsync(site));
			}
			finally
			{
				gate.Release();
			}
			if (path is not null)
			{
				SetIcon(key, path);
			}
		}));
	}

	private void SetIcon(string key, string path)
	{
		// Decoded at 48 px so 16–24 px icons stay sharp at 200% scaling.
		var icon = new BitmapImage { DecodePixelWidth = 48, UriSource = new Uri(path) };
		_icons[key] = icon;
		_iconPaths[key] = path;
		foreach (var item in AllSidebarItems().Where(item => item.Feed is not null && IconKey(item.Account!, item.Feed.FeedId) == key))
		{
			item.Icon = icon;
		}
		foreach (var article in Articles.Where(article => IconKey(article.Account, article.Article.FeedId) == key))
		{
			article.Icon = icon;
		}
	}

	/// <summary>Feed IDs are only unique within an account.</summary>
	private static string IconKey(IAccount account, string feedId) => $"{account.AccountId}|{feedId}";

	public SidebarItem? FindSidebarItem(string? key) => key is null ? null : AllSidebarItems().FirstOrDefault(item => item.Key == key);

	public void SelectSmartFeed(SidebarItemKind kind) => SelectedSidebarItem = FindSidebarItem(kind.ToString());

	public async Task RefreshAsync()
	{
		if (IsRefreshing)
		{
			return;
		}
		IsRefreshing = true;
		StatusText = "Refreshing…";
		try
		{
			// Parsing is CPU-bound, so keep the whole refresh off the UI thread. Accounts refresh side by side.
			var results = await Task.WhenAll(Accounts.ToList().Select(account => Task.Run(() => account.RefreshAllAsync())));
			if (Settings.HideReadFeeds || Accounts.Any(account => account.Kind != AccountKind.Local))
			{
				// Feeds with new articles may need to come back, and a sync account's feed list may have changed.
				LoadSidebar();
			}
			else
			{
				var feeds = LocalAccount.Store.GetFeeds().ToDictionary(feed => feed.FeedId);
				foreach (var item in AllSidebarItems().Where(item => item.Feed is not null))
				{
					if (feeds.TryGetValue(item.Feed!.FeedId, out var feed))
					{
						item.Update(feed);
					}
				}
				UpdateUnreadCounts();
				LoadArticles(keepSelection: true);
				// A first refresh can teach a feed its home page, which is where its favicon is found.
				_ = LoadIconsAsync();
			}

			var failed = results.Sum(result => result.FailedFeeds);
			var newArticles = results.Sum(result => result.NewArticles);
			string?[] parts =
			[
				newArticles == 1 ? "1 new article" : $"{newArticles:N0} new articles",
				failed == 0 ? null : failed == 1 ? "1 feed failed" : $"{failed} feeds failed",
				.. results.Select(result => result.Error),
				$"Updated {DateTime.Now:t}",
			];
			StatusText = string.Join(" · ", parts.Where(part => part is not null));
		}
		finally
		{
			IsRefreshing = false;
		}
	}

	/// <summary>Throws <see cref="SubscribeException"/> when no feed is found.</summary>
	public async Task SubscribeAsync(string url, IAccount account, StoredFolder? folder)
	{
		var feed = await Task.Run(() => account.SubscribeAsync(url, folder?.Name));
		// A sync account may have made the folder itself, so look it up again by name.
		var placedFolder = folder is null ? null : account.Store.GetFolders().FirstOrDefault(f => f.Name == folder.Name);
		if (placedFolder is not null)
		{
			_expandedKeys.Add(SidebarItem.FolderKey(account, placedFolder));
		}
		LoadSidebar(select: SidebarItem.FeedKey(account, feed.FeedId, placedFolder));
	}

	/// <summary>New folders are for On This PC; a sync account's folders come from its server.</summary>
	public void AddFolder(string name)
	{
		var folder = LocalAccount.Store.EnsureFolder(name);
		LoadSidebar(select: SidebarItem.FolderKey(LocalAccount, folder));
	}

	/// <summary>Checks the login, adds the account, and refreshes so its feeds arrive.</summary>
	public async Task AddFreshRssAccountAsync(string serverUrl, string username, string password)
	{
		var account = await _accounts.AddFreshRssAccountAsync(serverUrl, username, password);
		_expandedKeys.Add(SidebarItem.Group(SidebarItemKind.AccountGroup, "", [], account).Key);
		LoadSidebar();
		_ = RefreshAsync();
	}

	/// <summary>Removes a sync account and its local copy; nothing changes on the server.</summary>
	public void RemoveAccount(IAccount account)
	{
		_accounts.RemoveAccount(account);
		LoadSidebar();
	}

	/// <summary>Shows or hides read articles in the selected row's timeline; NetNewsWire remembers this per row.</summary>
	public void ToggleHideReadArticles()
	{
		if (!CanHideReadArticles)
		{
			return;
		}
		if (!Settings.HideReadArticlesKeys.Remove(SelectedSidebarItem!.Key))
		{
			Settings.HideReadArticlesKeys.Add(SelectedSidebarItem.Key);
		}
		SaveSettings();
		OnPropertyChanged(nameof(IsHidingReadArticles));
		LoadArticles(keepSelection: true);
	}

	public void ToggleHideReadFeeds()
	{
		Settings.HideReadFeeds = !Settings.HideReadFeeds;
		SaveSettings();
		OnPropertyChanged(nameof(HideReadFeeds));
		LoadSidebar();
	}

	public void SetSortOldestFirst(bool oldestFirst)
	{
		Settings.SortOldestFirst = oldestFirst;
		SaveSettings();
		OnPropertyChanged(nameof(SortNewestFirst));
		OnPropertyChanged(nameof(SortOldestFirst));
		LoadArticles(keepSelection: true);
	}

	/// <summary>
	/// NetNewsWire's Clean Up: with read articles hidden, drops the ones read since the timeline loaded.
	/// The article being read stays.
	/// </summary>
	public void CleanUp()
	{
		if (IsHidingReadArticles)
		{
			LoadArticles(keepSelection: true);
		}
	}

	/// <summary>Selects what was selected when the app last closed, without marking the article read again.</summary>
	public void RestoreSelection()
	{
		_restoringSelection = true;
		try
		{
			SelectedSidebarItem = FindSidebarItem(Settings.SelectedSidebarKey);
			SelectedArticle = Articles.FirstOrDefault(article => article.ArticleId == Settings.SelectedArticleId);
		}
		finally
		{
			_restoringSelection = false;
		}
	}

	/// <summary>Records the window and the sidebar's state for the next launch.</summary>
	public void SaveState(WindowPlacement? window)
	{
		Settings.Window = window ?? Settings.Window;
		Settings.ExpandedSidebarKeys = [.. _expandedKeys];
		Settings.SelectedSidebarKey = SelectedSidebarItem?.Key;
		Settings.SelectedArticleId = SelectedArticle?.ArticleId;
		SaveSettings();
	}

	private void SaveSettings()
	{
		try
		{
			Settings.Save(_settingsPath);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Settings are a convenience; failing to save them shouldn't interrupt reading.
		}
	}

	private bool HidesReadArticles(SidebarItem item) =>
		item.Kind == SidebarItemKind.AllUnread || Settings.HideReadArticlesKeys.Contains(item.Key);

	/// <summary>Where a feed row can move to in its own account: the top level when the account allows it, then the other folders.</summary>
	public static IReadOnlyList<(string Name, StoredFolder? Folder)> MoveDestinations(SidebarItem feedItem)
	{
		var account = feedItem.Account!;
		var destinations = new List<(string Name, StoredFolder? Folder)>();
		if (account.FeedsCanBeAtTopLevel && feedItem.ParentFolder is not null)
		{
			destinations.Add((account.Name, null));
		}
		destinations.AddRange(FoldersOf(account).Where(folder => folder.FolderId != feedItem.ParentFolder?.FolderId).Select(folder => (folder.Name, (StoredFolder?)folder)));
		return destinations;
	}

	/// <summary>
	/// Where a feed row can go in each other account: its top level, then its folders. For FreshRSS the top level means
	/// the server's default category.
	/// </summary>
	public IEnumerable<(IAccount Account, IReadOnlyList<(string Name, StoredFolder? Folder)> Destinations)> OtherAccountDestinations(SidebarItem feedItem) =>
		Accounts.Where(account => account != feedItem.Account).Select(account =>
			(account, (IReadOnlyList<(string, StoredFolder?)>)[(account.Name, null), .. FoldersOf(account).Select(folder => (folder.Name, (StoredFolder?)folder))]));

	/// <summary>
	/// Whether a dragged feed can be dropped on <paramref name="target"/>, a folder or an account's group. Within an account
	/// the account's rules apply; dropping on another account subscribes there, as in NetNewsWire.
	/// </summary>
	public static bool CanDrop(SidebarItem feedItem, SidebarItem target, bool copy)
	{
		if (feedItem is not { Kind: SidebarItemKind.Feed, Account: { } account } || target.Kind is not (SidebarItemKind.Folder or SidebarItemKind.AccountGroup))
		{
			return false;
		}
		if (target.Account != account)
		{
			return target.Account is not null;
		}
		return (!copy || account.FeedsCanBeInSeveralFolders) && target.Kind switch
		{
			SidebarItemKind.Folder => target.Folder!.FolderId != feedItem.ParentFolder?.FolderId,
			_ => account.FeedsCanBeAtTopLevel && feedItem.ParentFolder is not null,
		};
	}

	/// <summary>
	/// Moves or copies a feed row to a folder, or the top level when <paramref name="to"/> is null, in its own account or
	/// another one. Another account subscribes to the feed and, for a move, it's then removed here. The feed ends up selected
	/// if it was, or always when it went to another account.
	/// </summary>
	public async Task MoveFeedAsync(SidebarItem feedItem, IAccount toAccount, StoredFolder? to, bool copy)
	{
		var account = feedItem.Account!;
		var feed = feedItem.Feed!;
		if (toAccount == account)
		{
			await account.MoveFeedAsync(feed.FeedId, feedItem.ParentFolder?.FolderId, to?.FolderId, copy);
			if (to is not null)
			{
				_expandedKeys.Add(SidebarItem.FolderKey(account, to));
			}
			LoadSidebar(select: SelectedSidebarItem == feedItem ? SidebarItem.FeedKey(account, feed.FeedId, to) : null);
			return;
		}

		var added = await Task.Run(() => FeedTransfer.TransferAsync(account, feed, feedItem.ParentFolder?.FolderId, toAccount, to?.Name, copy));
		LoadSidebar();
		// The server may have put it in a folder of its own choosing, so find where it landed.
		if (AllSidebarItems().FirstOrDefault(item => item.Account == toAccount && item.Feed?.FeedId == added.FeedId) is { } row)
		{
			if (row.ParentFolder is { } folder && FindSidebarItem(SidebarItem.FolderKey(toAccount, folder)) is { } folderRow)
			{
				SetExpanded(folderRow, true);
			}
			SelectedSidebarItem = row;
		}
	}

	/// <summary>NetNewsWire's <c>renameFeedIfNecessary</c>: ignores a blank name or one that's already the feed's.</summary>
	public async Task RenameFeedAsync(SidebarItem item, string name)
	{
		name = name.Trim();
		if (name.Length == 0 || item.Feed is not { } feed || name == NameSorting.DisplayName(feed))
		{
			return;
		}
		await item.Account!.RenameFeedAsync(feed.FeedId, name);
		LoadSidebar();
		LoadArticles(keepSelection: true);
	}

	public async Task RenameFolderAsync(SidebarItem item, string name)
	{
		await item.Account!.RenameFolderAsync(item.Folder!.FolderId, name);
		LoadSidebar();
	}

	/// <summary>
	/// A local feed is removed from the folder or top level it's shown in; a sync feed is unsubscribed, as in
	/// NetNewsWire. A folder goes with its feeds.
	/// </summary>
	public async Task DeleteAsync(SidebarItem item)
	{
		if (item.Feed is { } feed)
		{
			await item.Account!.RemoveFeedAsync(feed.FeedId, item.ParentFolder?.FolderId);
		}
		else if (item.Folder is { } folder)
		{
			await item.Account!.RemoveFolderAsync(folder.FolderId);
		}
		LoadSidebar();
	}

	/// <summary>Imports into On This PC, then refreshes so the new feeds fill in, as NetNewsWire does.</summary>
	public async Task<OpmlImportResult> ImportOpmlAsync(string path)
	{
		var result = await Task.Run(() => LocalAccount.ImportOpml(path));
		LoadSidebar();
		_ = RefreshAsync();
		return result;
	}

	public void ExportOpml(string path) =>
		File.WriteAllText(path, OpmlExporter.OpmlString(LocalAccount.Store, Path.GetFileNameWithoutExtension(path)));

	/// <summary>Marks every article in <paramref name="item"/> read: the selected sidebar item by default.</summary>
	public void MarkAllRead(SidebarItem? item = null)
	{
		item ??= SelectedSidebarItem;
		if (item is null)
		{
			return;
		}
		var unread = item == SelectedSidebarItem
			? Articles.Where(article => !article.IsRead).Select(article => (article.Account, article.ArticleId))
			: ArticlesFor(item).Where(pair => !pair.Article.Read).Select(pair => (pair.Account, pair.Article.ArticleId));
		foreach (var group in unread.ToList().GroupBy(pair => pair.Account, pair => pair.ArticleId))
		{
			var ids = group.ToHashSet();
			group.Key.SetArticleStatus(ids, StatusKey.Read, true);
			foreach (var article in Articles.Where(article => article.Account == group.Key && ids.Contains(article.ArticleId)))
			{
				article.IsRead = true;
			}
		}
		ArticleStatusChanged();
	}

	public void ToggleRead(ArticleItem? article = null)
	{
		if ((article ?? SelectedArticle) is { } target)
		{
			SetRead(target, !target.IsRead);
		}
	}

	public void ToggleStarred(ArticleItem? article = null)
	{
		if ((article ?? SelectedArticle) is { } target)
		{
			target.Account.SetArticleStatus([target.ArticleId], StatusKey.Starred, !target.IsStarred);
			target.IsStarred = !target.IsStarred;
			ArticleStatusChanged();
		}
	}

	/// <summary>
	/// Selects the next unread article below the selection. When there's none, moves on to the next feed in the sidebar
	/// with unread articles, wrapping around, and selects its first unread article, as NetNewsWire does.
	/// </summary>
	public void SelectNextUnread() => SelectUnread(forward: true);

	/// <summary>The same as <see cref="SelectNextUnread"/>, upwards: ending on the previous feed's last unread article.</summary>
	public void SelectPreviousUnread() => SelectUnread(forward: false);

	/// <summary>The <c>m</c> key: marks the selected article unread and goes to the next unread one.</summary>
	public void MarkUnreadAndSelectNextUnread()
	{
		if (SelectedArticle is { } article)
		{
			SetRead(article, false);
		}
		SelectNextUnread();
	}

	/// <summary>The <c>l</c> key: marks the whole timeline read and goes to the next unread article.</summary>
	public void MarkAllReadAndSelectNextUnread()
	{
		MarkAllRead();
		SelectNextUnread();
	}

	/// <summary>Marks articles in the timeline older than the selected one as read.</summary>
	public void MarkOlderRead()
	{
		if (SelectedArticle is not { } selected)
		{
			return;
		}
		var cutoff = selected.Article.LogicalDatePublished;
		var older = Articles.Where(article => !article.IsRead && article.Article.LogicalDatePublished < cutoff).ToList();
		foreach (var group in older.GroupBy(article => article.Account))
		{
			group.Key.SetArticleStatus([.. group.Select(article => article.ArticleId)], StatusKey.Read, true);
		}
		foreach (var article in older)
		{
			article.IsRead = true;
		}
		ArticleStatusChanged();
	}

	/// <summary>Moves the sidebar selection to the next or previous visible row, skipping group headers (the <c>z</c> and <c>a</c> keys).</summary>
	public void SelectAdjacentSidebarItem(int offset)
	{
		var rows = VisibleSidebarItems().Where(item => !item.IsGroup).ToList();
		var index = SelectedSidebarItem is null ? -1 : rows.IndexOf(SelectedSidebarItem);
		var target = index < 0 ? (offset > 0 ? 0 : rows.Count - 1) : index + offset;
		if (target >= 0 && target < rows.Count)
		{
			SelectedSidebarItem = rows[target];
		}
	}

	public void SetSelectedExpanded(bool expanded)
	{
		if (SelectedSidebarItem is { Children.Count: > 0 } item)
		{
			SetExpanded(item, expanded);
		}
	}

	/// <summary>
	/// The one place a row's expanded state changes, so it survives sidebar rebuilds. The tree reports clicks on its
	/// chevrons here too; its own state isn't read back, since rebuilding reuses row controls.
	/// </summary>
	public void SetExpanded(SidebarItem item, bool expanded)
	{
		item.IsExpanded = expanded;
		SidebarExpansionChanged?.Invoke(this, EventArgs.Empty);
		if (expanded)
		{
			_expandedKeys.Add(item.Key);
		}
		else
		{
			_expandedKeys.Remove(item.Key);
		}
	}

	/// <summary>Expands or collapses every folder; the groups stay open so the selection stays visible.</summary>
	public void SetAllExpanded(bool expanded)
	{
		foreach (var item in AllSidebarItems().Where(item => item.Kind == SidebarItemKind.Folder))
		{
			SetExpanded(item, expanded);
		}
		if (!expanded && SelectedSidebarItem is { ParentFolder: { } parent, Account: { } account })
		{
			SelectedSidebarItem = FindSidebarItem(SidebarItem.FolderKey(account, parent));
		}
	}

	private void SelectUnread(bool forward)
	{
		var index = SelectedArticle is null ? (forward ? -1 : Articles.Count) : IndexOf(SelectedArticle);
		var inTimeline = forward
			? Articles.Skip(index + 1).FirstOrDefault(article => !article.IsRead)
			: Articles.Take(Math.Max(index, 0)).LastOrDefault(article => !article.IsRead);
		if (inTimeline is not null)
		{
			SelectedArticle = inTimeline;
			return;
		}

		var rows = AllSidebarItems().ToList();
		var position = SelectedSidebarItem is null ? -1 : rows.IndexOf(SelectedSidebarItem);
		var ordered = forward
			? rows.Skip(position + 1).Concat(rows.Take(position + 1))
			: rows.Take(Math.Max(position, 0)).Reverse().Concat(rows.Skip(Math.Max(position, 0)).Reverse());
		if (ordered.FirstOrDefault(item => item.Kind == SidebarItemKind.Feed && item.UnreadCount > 0) is not { } feed)
		{
			return;
		}
		if (feed.ParentFolder is { } folder)
		{
			SetExpanded(FindSidebarItem(SidebarItem.FolderKey(feed.Account!, folder))!, true);
		}
		SelectedSidebarItem = feed;
		SelectedArticle = forward ? Articles.FirstOrDefault(article => !article.IsRead) : Articles.LastOrDefault(article => !article.IsRead);
	}

	public static void OpenInBrowser(string? url)
	{
		if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
		{
			Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
		}
	}

	public static StoredFeed? FeedFor(ArticleItem article) => article.Account.Store.GetFeed(article.Article.FeedId);

	partial void OnSelectedSidebarItemChanged(SidebarItem? value)
	{
		OnPropertyChanged(nameof(IsHidingReadArticles));
		OnPropertyChanged(nameof(CanHideReadArticles));
		if (!_rebuildingSidebar)
		{
			LoadArticles(keepSelection: false);
		}
	}

	/// <summary>Selecting an article marks it read, as in NetNewsWire.</summary>
	partial void OnSelectedArticleChanged(ArticleItem? value)
	{
		if (value is { IsRead: false } && !_restoringSelection)
		{
			SetRead(value, true);
		}
	}

	private void SetRead(ArticleItem article, bool read)
	{
		article.Account.SetArticleStatus([article.ArticleId], StatusKey.Read, read);
		article.IsRead = read;
		ArticleStatusChanged();
	}

	private void ArticleStatusChanged()
	{
		OnPropertyChanged(nameof(ReadToggleLabel));
		OnPropertyChanged(nameof(StarToggleLabel));
		UpdateUnreadCounts();
	}

	private int IndexOf(ArticleItem article)
	{
		for (var i = 0; i < Articles.Count; i++)
		{
			if (Articles[i] == article)
			{
				return i;
			}
		}
		return -1;
	}

	private void LoadArticles(bool keepSelection)
	{
		var selected = keepSelection ? SelectedArticle : null;
		var pairs = ArticlesFor(SelectedSidebarItem);
		if (SelectedSidebarItem is { } item && HidesReadArticles(item))
		{
			pairs = pairs.Where(pair => !pair.Article.Read);
		}
		var list = pairs.ToList();
		// Keep the article being read in view even when it no longer matches, as when All Unread refreshes.
		if (selected is not null && !list.Any(pair => pair.Account == selected.Account && pair.Article.ArticleId == selected.ArticleId)
			&& selected.Account.Store.GetArticle(selected.ArticleId) is { } current)
		{
			list.Add((selected.Account, current));
		}
		var newestFirst = list.OrderByDescending(pair => pair.Article.LogicalDatePublished).ThenBy(pair => pair.Article.ArticleId, StringComparer.Ordinal);
		var ordered = Settings.SortOldestFirst ? newestFirst.Reverse() : newestFirst;
		var feedNames = list.Select(pair => pair.Account).Distinct()
			.ToDictionary(account => account, account => account.Store.GetFeeds().ToDictionary(feed => feed.FeedId, NameSorting.DisplayName));
		Articles = [.. ordered.Select(pair => new ArticleItem(pair.Account, pair.Article,
			feedNames[pair.Account].GetValueOrDefault(pair.Article.FeedId, ""), _icons.GetValueOrDefault(IconKey(pair.Account, pair.Article.FeedId))))];
		// Keeping the selection across a reload isn't the reader choosing the article again, so it doesn't mark it
		// read; otherwise an article marked unread would turn read at the next refresh.
		_restoringSelection = true;
		try
		{
			SelectedArticle = selected is null ? null
				: Articles.FirstOrDefault(article => article.Account == selected.Account && article.ArticleId == selected.ArticleId);
		}
		finally
		{
			_restoringSelection = false;
		}
	}

	/// <summary>The articles a row shows, each with its account. Smart feeds gather from every account.</summary>
	private IEnumerable<(IAccount Account, StoredArticle Article)> ArticlesFor(SidebarItem? item)
	{
		return item?.Kind switch
		{
			SidebarItemKind.Today => FromAll(store => store.GetArticlesSince(TodayCutoff)),
			SidebarItemKind.AllUnread => FromAll(store => store.GetUnreadArticles()),
			SidebarItemKind.Starred => FromAll(store => store.GetStarredArticles()),
			SidebarItemKind.Folder => From(item.Account!, item.Account!.Store.GetArticles(item.Children.Select(child => child.Feed!.FeedId))),
			SidebarItemKind.Feed => From(item.Account!, item.Account!.Store.GetArticles(item.Feed!.FeedId)),
			_ => [],
		};

		IEnumerable<(IAccount, StoredArticle)> FromAll(Func<ArticlesStore, IReadOnlyList<StoredArticle>> query) =>
			Accounts.ToList().SelectMany(account => From(account, query(account.Store)));

		static IEnumerable<(IAccount, StoredArticle)> From(IAccount account, IEnumerable<StoredArticle> articles) =>
			articles.Select(article => (account, article));
	}

	private void UpdateUnreadCounts()
	{
		var counts = Accounts.ToDictionary(account => account, account => account.Store.UnreadCounts());
		var allUnread = counts.Values.Sum(accountCounts => accountCounts.Values.Sum());
		foreach (var group in SidebarItems)
		{
			Update(group);
		}

		int Update(SidebarItem item)
		{
			var childTotal = item.Children.Sum(Update);
			item.UnreadCount = item.Kind switch
			{
				SidebarItemKind.Feed => counts[item.Account!].GetValueOrDefault(item.Feed!.FeedId),
				SidebarItemKind.Folder => childTotal,
				SidebarItemKind.AccountGroup => counts[item.Account!].Values.Sum(),
				SidebarItemKind.AllUnread => allUnread,
				SidebarItemKind.Today => Accounts.Sum(account => account.Store.UnreadCountSince(TodayCutoff)),
				SidebarItemKind.Starred => Accounts.Sum(account => account.Store.StarredUnreadCount()),
				_ => 0,
			};
			return item.UnreadCount;
		}
	}

	/// <summary>
	/// Raised when rows were rebuilt or expanded or collapsed. The tree keeps expansion in its own nodes, which a
	/// rebuild resets, so the window re-applies <see cref="SidebarItem.IsExpanded"/> to the rows.
	/// </summary>
	public event EventHandler? SidebarExpansionChanged;

	public IEnumerable<SidebarItem> AllSidebarItems() => SidebarItems.SelectMany(item => item.SelfAndDescendants());

	/// <summary>Rows in the order shown, leaving out the children of collapsed rows.</summary>
	private IEnumerable<SidebarItem> VisibleSidebarItems()
	{
		return SidebarItems.SelectMany(Visible);

		static IEnumerable<SidebarItem> Visible(SidebarItem item) =>
			item.IsExpanded ? item.Children.SelectMany(Visible).Prepend(item) : [item];
	}
}
