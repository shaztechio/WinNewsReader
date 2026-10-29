using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinNewsReader.Core;
using WinNewsReader.Core.ArticleRendering;
using WinNewsReader.Store;
using WinNewsReader.Sync.GoogleReader;

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

	/// <summary>What refreshes have done, for the Current Activity, Activity Log and Error Log windows.</summary>
	public ActivityLog Activity { get; } = new();

	public MainViewModel(AccountManager accounts, FaviconDownloader favicons, ArticleThemes themes, ArticleThemeInstaller themeInstaller, ArticleExtractor extractor, AppSettings settings, string settingsPath)
	{
		_accounts = accounts;
		accounts.LocalAccount.Activity = Activity;
		_favicons = favicons;
		Themes = themes;
		ThemeInstaller = themeInstaller;
		_extractor = extractor;
		ReaderView = ReaderViewState.Off;
		Settings = settings;
		_settingsPath = settingsPath;
		SidebarItem.CountDisplay = settings.UnreadCounts;
		BrowserPath = settings.BrowserPath;
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

	// NetNewsWire's per-feed "Show Notifications for New Articles" (newArticleNotificationsEnabled).

	public bool NotifiesNewArticles(IAccount account, string feedId) => Settings.NotificationFeedKeys.Contains(ReaderViewKey(account, feedId));

	public bool IsNotifyingNewArticles(SidebarItem feedItem) => NotifiesNewArticles(feedItem.Account!, feedItem.Feed!.FeedId);

	public void SetNotifiesNewArticles(SidebarItem feedItem, bool on)
	{
		var key = ReaderViewKey(feedItem.Account!, feedItem.Feed!.FeedId);
		if (on ? Settings.NotificationFeedKeys.Add(key) : Settings.NotificationFeedKeys.Remove(key))
		{
			SaveSettings();
		}
	}

	/// <summary>The feed's cached icon file, if it's a kind a notification can show.</summary>
	public string? IconPath(IAccount account, string feedId) =>
		_iconPaths.GetValueOrDefault(IconKey(account, feedId)) is { } path
		&& Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" ? path : null;

	/// <summary>
	/// A notification's Open, Mark as Read or Mark as Starred. Open selects the article in its feed, as NetNewsWire does.
	/// </summary>
	public void HandleNotification(NotificationAction action, string accountId, string articleId)
	{
		if (Accounts.FirstOrDefault(account => account.AccountId == accountId) is not { } account
			|| account.Store.GetArticle(articleId) is not { } article)
		{
			return;
		}
		switch (action)
		{
			case NotificationAction.MarkRead:
				account.SetArticleStatus([articleId], StatusKey.Read, true);
				break;
			case NotificationAction.MarkStarred:
				account.SetArticleStatus([articleId], StatusKey.Starred, true);
				break;
			default:
				EndSearch();
				if (AllSidebarItems().FirstOrDefault(item => item.Account == account && item.Feed?.FeedId == article.FeedId) is { } feedRow)
				{
					SelectedSidebarItem = feedRow;
				}
				SelectedArticle = Articles.FirstOrDefault(item => item.Account == account && item.ArticleId == articleId);
				return;
		}
		// The timeline and counts follow the status the notification changed.
		if (Articles.FirstOrDefault(item => item.Account == account && item.ArticleId == articleId) is { } shown)
		{
			shown.IsRead = account.Store.GetArticle(articleId)?.Read ?? shown.IsRead;
			shown.IsStarred = account.Store.GetArticle(articleId)?.Starred ?? shown.IsStarred;
		}
		ArticleStatusChanged();
	}

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
		// Choosing one article elsewhere, as Next Unread does, makes it the whole selection.
		if (!_settingSelection)
		{
			SelectedArticles = newValue is null ? [] : [newValue];
			SelectionChanged();
		}
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

	/// <summary>The article page in the current theme; with no article selected, or several, a page that says so.</summary>
	public string ArticlePageHtml(ArticleItem? item)
	{
		var theme = CurrentTheme;
		if (item is null && HasMultipleSelection)
		{
			return ArticleRenderer.PageHtml(ArticleRenderer.MultipleSelection(theme, [.. SelectedArticles.Select(article => (article.FeedName, article.Title))]), Themes.PageTemplate);
		}
		if (item is null)
		{
			return ArticleRenderer.PageHtml(ArticleRenderer.NoSelection(theme), Themes.PageTemplate);
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

	/// <summary>The accounts the reader has on: the sidebar, search, refresh and notifications leave the others alone.</summary>
	public IReadOnlyList<IAccount> Accounts => [.. _accounts.Accounts.Where(account => !Settings.DisabledAccountIds.Contains(account.AccountId))];

	/// <summary>Every account, on or off, for the Accounts preferences.</summary>
	public IReadOnlyList<IAccount> AllAccounts => _accounts.Accounts;

	public bool IsAccountOn(IAccount account) => !Settings.DisabledAccountIds.Contains(account.AccountId);

	/// <summary>NetNewsWire's account Active setting: an account that's off disappears from the sidebar and isn't refreshed.</summary>
	public void SetAccountOn(IAccount account, bool on)
	{
		if (on ? !Settings.DisabledAccountIds.Remove(account.AccountId) : !Settings.DisabledAccountIds.Add(account.AccountId))
		{
			return;
		}
		SaveSettings();
		AccountsChanged?.Invoke(this, EventArgs.Empty);
		LoadSidebar();
		LoadArticles(keepSelection: false);
		if (on)
		{
			_ = RefreshAsync();
		}
	}

	private LocalAccount LocalAccount => _accounts.LocalAccount;

	public System.Collections.ObjectModel.ObservableCollection<SidebarItem> SidebarItems { get; } = [];

	[ObservableProperty]
	public partial IReadOnlyList<ArticleItem> Articles { get; private set; }

	[ObservableProperty]
	public partial SidebarItem? SelectedSidebarItem { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSelectedArticle))]
	public partial ArticleItem? SelectedArticle { get; set; }

	/// <summary>
	/// NetNewsWire's <c>selectedArticles</c>. With one article selected, it's <see cref="SelectedArticle"/>; with
	/// several, <see cref="SelectedArticle"/> is null and the article pane says so, and the read and star commands act
	/// on them all.
	/// </summary>
	public IReadOnlyList<ArticleItem> SelectedArticles { get; private set; } = [];

	public bool HasSelection => SelectedArticles.Count > 0;

	public bool HasMultipleSelection => SelectedArticles.Count > 1;

	/// <summary>Set while the selection comes from the timeline, so the single selection doesn't reset the list.</summary>
	private bool _settingSelection;

	/// <summary>The timeline's selection, one article or several.</summary>
	public void SetSelectedArticles(IReadOnlyList<ArticleItem> articles)
	{
		_settingSelection = true;
		try
		{
			SelectedArticles = articles;
			SelectedArticle = articles.Count == 1 ? articles[0] : null;
		}
		finally
		{
			_settingSelection = false;
		}
		SelectionChanged();
	}

	private void SelectionChanged()
	{
		OnPropertyChanged(nameof(SelectedArticles));
		OnPropertyChanged(nameof(HasSelection));
		OnPropertyChanged(nameof(HasMultipleSelection));
		OnPropertyChanged(nameof(ReadToggleLabel));
		OnPropertyChanged(nameof(StarToggleLabel));
		OnPropertyChanged(nameof(ReadToggleGlyph));
		OnPropertyChanged(nameof(StarToggleGlyph));
	}

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsNotRefreshing))]
	public partial bool IsRefreshing { get; private set; }

	[ObservableProperty]
	public partial string StatusText { get; private set; }

	public bool IsNotRefreshing => !IsRefreshing;

	public bool HasSelectedArticle => SelectedArticle is not null;

	// NetNewsWire's toggles over a selection: if any article is unread, they all become read, otherwise unread; if any
	// isn't starred, they all become starred, otherwise unstarred.

	public string ReadToggleLabel => MarksRead(SelectedArticles) ? "Mark as Read" : "Mark as Unread";

	public string StarToggleLabel => MarksStarred(SelectedArticles) ? "Mark as Starred" : "Mark as Unstarred";

	/// <summary>The toolbar's read button shows the selection's state, as NetNewsWire's does: a closed envelope while unread.</summary>
	public string ReadToggleGlyph => SelectedArticles.Any(article => !article.IsRead) ? "\uE715" : "\uE8C3";

	/// <summary>A filled star while everything selected is starred.</summary>
	public string StarToggleGlyph => SelectedArticles.Count > 0 && !MarksStarred(SelectedArticles) ? "\uE735" : "\uE734";

	public static bool MarksRead(IReadOnlyList<ArticleItem> articles) => articles.Any(article => !article.IsRead);

	public static bool MarksStarred(IReadOnlyList<ArticleItem> articles) => articles.Count == 0 || articles.Any(article => !article.IsStarred);

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
		// Hide Read Feeds leaves out feeds, and folders, with nothing unread, except the selected row. At launch,
		// before the last session's selection is restored, that's the row it had selected, so it's there to restore.
		var keptKey = selectedKey ?? (_selectionRestored ? null : Settings.SelectedSidebarKey);
		var unreadCounts = Accounts.ToDictionary(account => account, account => account.Store.UnreadCounts());
		bool Shown(SidebarItem item) =>
			!Settings.HideReadFeeds || item.Key == keptKey
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
				path = await Task.Run(() => _favicons.IconPathAsync(site, feed.IconUrl, feed.Url));
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
			var refreshing = Accounts.ToList();
			var results = await Task.WhenAll(refreshing.Select(account => Task.Run(() => account.RefreshAllAsync())));
			// The local account logs feed by feed as it goes; a sync account reports once, when it's done.
			for (var i = 0; i < refreshing.Count; i++)
			{
				if (refreshing[i].Kind == AccountKind.Local)
				{
					continue;
				}
				if (results[i].Error is { } error)
				{
					Activity.Add(ActivityLevel.Error, refreshing[i].Name, null, error);
				}
				else
				{
					Activity.Add(ActivityLevel.Info, refreshing[i].Name, null, results[i].NewArticles == 1 ? "Synced: 1 new article" : $"Synced: {results[i].NewArticles} new articles");
				}
			}
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
		AccountsChanged?.Invoke(this, EventArgs.Empty);
		_expandedKeys.Add(SidebarItem.Group(SidebarItemKind.AccountGroup, "", [], account).Key);
		LoadSidebar();
		_ = RefreshAsync();
	}

	/// <summary>Saves the changes to a sync account's name, server, username or password, then syncs it with them.</summary>
	public async Task EditFreshRssAccountAsync(ReaderApiAccount account, string name, string serverUrl, string username, string password)
	{
		var before = account.Record;
		await _accounts.UpdateFreshRssAccountAsync(account, name, serverUrl, username, password);
		AccountsChanged?.Invoke(this, EventArgs.Empty);
		// The sidebar names the account.
		LoadSidebar();
		var after = account.Record;
		if (password.Length > 0 || after.Endpoint != before.Endpoint || after.Username != before.Username)
		{
			_ = RefreshAsync();
		}
	}

	/// <summary>Raised when an account is added, removed, or turned on or off.</summary>
	public event EventHandler? AccountsChanged;

	/// <summary>Removes a sync account and its local copy; nothing changes on the server.</summary>
	public void RemoveAccount(IAccount account)
	{
		_accounts.RemoveAccount(account);
		Undo.Clear();
		Settings.DisabledAccountIds.Remove(account.AccountId);
		SaveSettings();
		AccountsChanged?.Invoke(this, EventArgs.Empty);
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
		_selectionRestored = true;
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

	private bool _selectionRestored;

	/// <summary>Records the window and the sidebar's state for the next launch.</summary>
	public void SaveState(WindowPlacement? window)
	{
		Settings.Window = window ?? Settings.Window;
		Settings.ExpandedSidebarKeys = [.. _expandedKeys];
		Settings.SelectedSidebarKey = SelectedSidebarItem?.Key;
		Settings.SelectedArticleId = SelectedArticle?.ArticleId;
		SaveSettings();
	}

	public void SaveSettings()
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
	/// <summary>Every row in the sidebar for the feed: one for each folder it's in, or the top level.</summary>
	public IReadOnlyList<SidebarItem> RowsForFeed(IAccount account, string feedId) =>
		[.. AllSidebarItems().Where(item => item.Account == account && item.Feed?.FeedId == feedId)];

	public async Task DeleteAsync(SidebarItem item)
	{
		var account = item.Account!;
		if (item.Feed is { } feed)
		{
			var folderName = item.ParentFolder?.Name;
			await account.RemoveFeedAsync(feed.FeedId, item.ParentFolder?.FolderId);
			var current = feed;
			Undo.Push(
				"Delete Feed",
				async () => current = await RestoreFeedAsync(account, current, folderName),
				async () => await account.RemoveFeedAsync(current.FeedId, FolderIdNamed(account, folderName)));
		}
		else if (item.Folder is { } folder)
		{
			var feeds = item.Children.Where(child => child.Feed is not null).Select(child => child.Feed!).ToList();
			await account.RemoveFolderAsync(folder.FolderId);
			Undo.Push(
				"Delete Folder",
				async () =>
				{
					if (account.Kind == AccountKind.Local)
					{
						account.Store.EnsureFolder(folder.Name);
					}
					for (var i = 0; i < feeds.Count; i++)
					{
						feeds[i] = await RestoreFeedAsync(account, feeds[i], folder.Name);
					}
				},
				async () =>
				{
					if (FolderIdNamed(account, folder.Name) is { } id)
					{
						await account.RemoveFolderAsync(id);
					}
				});
		}
		LoadSidebar();
	}

	private static long? FolderIdNamed(IAccount account, string? name) =>
		name is null ? null : account.Store.GetFolders().FirstOrDefault(folder => folder.Name == name)?.FolderId;

	/// <summary>
	/// Undoes a delete as NetNewsWire does: subscribes to the feed again, in its folder, with the name the reader gave it.
	/// Its articles come back with the next refresh; the ones that were deleted with it, and their read and starred marks, don't.
	/// </summary>
	private async Task<StoredFeed> RestoreFeedAsync(IAccount account, StoredFeed feed, string? folderName)
	{
		StoredFeed restored;
		if (account.Kind == AccountKind.Local)
		{
			restored = account.Store.AddFeed(feed.Url, feed.Name, feed.HomePageUrl, folderName is null ? null : account.Store.EnsureFolder(folderName).FolderId);
			if (feed.EditedName is { } editedName)
			{
				account.Store.SetFeedEditedName(restored.FeedId, editedName);
			}
		}
		else
		{
			restored = await Task.Run(() => account.SubscribeAsync(feed.Url, folderName));
			if (feed.EditedName is { } editedName)
			{
				await account.RenameFeedAsync(restored.FeedId, editedName);
			}
		}
		LoadSidebar();
		_ = RefreshAsync();
		return restored;
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
		ChangeStatus([.. unread], StatusKey.Read, true, "Mark All as Read");
	}

	/// <summary>Toggles read for <paramref name="articles"/>, or the selection, as NetNewsWire's toggle does.</summary>
	public void ToggleRead(IReadOnlyList<ArticleItem>? articles = null)
	{
		var targets = articles ?? SelectedArticles;
		if (targets.Count > 0)
		{
			SetStatus(targets, StatusKey.Read, MarksRead(targets));
		}
	}

	public void ToggleStarred(IReadOnlyList<ArticleItem>? articles = null)
	{
		var targets = articles ?? SelectedArticles;
		if (targets.Count > 0)
		{
			SetStatus(targets, StatusKey.Starred, MarksStarred(targets));
		}
	}

	/// <summary>The commands the reader undoes with Ctrl+Z: marking articles read or unread, starred or not, and deleting feeds and folders.</summary>
	public UndoStack Undo { get; } = new();

	private void SetStatus(IReadOnlyList<ArticleItem> articles, StatusKey key, bool flag) =>
		ChangeStatus(
			[.. articles.Where(article => (key == StatusKey.Read ? article.IsRead : article.IsStarred) != flag).Select(article => (article.Account, article.ArticleId))],
			key, flag, StatusLabel(key, flag));

	private static string StatusLabel(StatusKey key, bool flag) => key switch
	{
		StatusKey.Read => flag ? "Mark Read" : "Mark Unread",
		_ => flag ? "Mark Starred" : "Mark Unstarred",
	};

	/// <summary>Sets the status of <paramref name="changing"/>, the articles that don't have it yet, and records the change to undo.</summary>
	private void ChangeStatus(List<(IAccount Account, string ArticleId)> changing, StatusKey key, bool flag, string label)
	{
		if (changing.Count == 0)
		{
			return;
		}
		ApplyStatus(changing, key, flag);
		Undo.Push(label, () => ApplyStatus(changing, key, !flag), () => ApplyStatus(changing, key, flag));
	}

	private void ApplyStatus(List<(IAccount Account, string ArticleId)> articles, StatusKey key, bool flag)
	{
		foreach (var group in articles.GroupBy(pair => pair.Account, pair => pair.ArticleId))
		{
			var ids = group.ToHashSet();
			group.Key.SetArticleStatus(ids, key, flag);
			foreach (var shown in Articles.Where(shown => shown.Account == group.Key && ids.Contains(shown.ArticleId)))
			{
				if (key == StatusKey.Read)
				{
					shown.IsRead = flag;
				}
				else
				{
					shown.IsStarred = flag;
				}
			}
		}
		ArticleStatusChanged();
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
	// The timeline's Mark Above as Read and Mark Below as Read, as in NetNewsWire: the unread articles listed above the
	// first of the articles, or below the last.

	public IReadOnlyList<ArticleItem> UnreadAbove(IReadOnlyList<ArticleItem> articles)
	{
		var first = articles.Select(IndexOf).Where(index => index >= 0).DefaultIfEmpty(-1).Min();
		return first < 0 ? [] : [.. Articles.Take(first).Where(article => !article.IsRead)];
	}

	public IReadOnlyList<ArticleItem> UnreadBelow(IReadOnlyList<ArticleItem> articles)
	{
		var last = articles.Select(IndexOf).DefaultIfEmpty(-1).Max();
		return last < 0 ? [] : [.. Articles.Skip(last + 1).Where(article => !article.IsRead)];
	}

	/// <summary>NetNewsWire's Mark Above as Read (Ctrl+Alt+K): the unread articles above the first selected one.</summary>
	public void MarkAboveRead() => MarkRead(UnreadAbove(SelectedArticles));

	/// <summary>NetNewsWire's Mark Below as Read (Ctrl+Shift+K): the unread articles below the last selected one.</summary>
	public void MarkBelowRead() => MarkRead(UnreadBelow(SelectedArticles));

	public void MarkRead(IReadOnlyList<ArticleItem> articles)
	{
		if (articles.Count > 0)
		{
			SetStatus(articles, StatusKey.Read, true);
		}
	}

	/// <summary>NetNewsWire's Select “Feed” in Sidebar: the article's feed, unless the timeline is already showing it.</summary>
	public SidebarItem? FeedRowFor(ArticleItem article) =>
		AllSidebarItems().FirstOrDefault(item => item.Account == article.Account && item.Feed?.FeedId == article.Article.FeedId);

	public bool IsShowingOnlyFeedOf(ArticleItem article) =>
		_searchString is null && SelectedSidebarItem is { Kind: SidebarItemKind.Feed } row && row.Account == article.Account && row.Feed!.FeedId == article.Article.FeedId;

	public void SelectFeedInSidebar(ArticleItem article)
	{
		if (FeedRowFor(article) is { } row)
		{
			EndSearch();
			SelectedSidebarItem = row;
			SelectedArticle = Articles.FirstOrDefault(item => item.Account == article.Account && item.ArticleId == article.ArticleId);
		}
	}

	/// <summary>How many unread articles the article's feed has, for Mark All as Read in “Feed”.</summary>
	public int UnreadCountInFeedOf(ArticleItem article) => article.Account.Store.UnreadCounts().GetValueOrDefault(article.Article.FeedId);

	public void MarkAllReadInFeedOf(ArticleItem article)
	{
		var ids = article.Account.Store.GetUnreadArticles(article.Article.FeedId).Select(unread => unread.ArticleId).ToList();
		if (ids.Count == 0)
		{
			return;
		}
		ChangeStatus([.. ids.Select(id => (article.Account, id))], StatusKey.Read, true, "Mark All as Read");
	}

	public void MarkOlderRead()
	{
		if (SelectedArticle is not { } selected)
		{
			return;
		}
		var cutoff = selected.Article.LogicalDatePublished;
		ChangeStatus(
			[.. Articles.Where(article => !article.IsRead && article.Article.LogicalDatePublished < cutoff).Select(article => (article.Account, article.ArticleId))],
			StatusKey.Read, true, "Mark Read");
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

	/// <summary>The browser the reader picked, or null for Windows' default.</summary>
	public static string? BrowserPath { get; set; }

	public static void OpenInBrowser(string? url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
		{
			return;
		}
		if (BrowserPath is { } path && File.Exists(path))
		{
			try
			{
				Process.Start(new ProcessStartInfo(path) { ArgumentList = { uri.AbsoluteUri } });
				return;
			}
			catch (System.ComponentModel.Win32Exception)
			{
				// The picked browser won't start; Windows' default will.
			}
		}
		Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
	}

	// The General preferences, as NetNewsWire's General pane has them.

	public void SetAppearance(AppAppearance appearance)
	{
		Settings.Appearance = appearance;
		SaveSettings();
		AppearanceChanged?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Raised when the reader picks Light, Dark or System.</summary>
	public event EventHandler? AppearanceChanged;

	/// <summary>Every unread article in the accounts that are on: what the taskbar badge counts, as NetNewsWire's Dock badge does.</summary>
	public int TotalUnread { get; private set; }

	/// <summary>Raised when the unread total or the Unread Counts preference changes.</summary>
	public event EventHandler? UnreadBadgeChanged;

	public void SetUnreadCountDisplay(UnreadCountDisplay display)
	{
		Settings.UnreadCounts = display;
		SidebarItem.CountDisplay = display;
		SaveSettings();
		UnreadBadgeChanged?.Invoke(this, EventArgs.Empty);
		foreach (var item in AllSidebarItems())
		{
			item.RefreshCountText();
		}
	}

	public void SetBrowser(string? path)
	{
		Settings.BrowserPath = path;
		BrowserPath = path;
		SaveSettings();
	}

	public void SetRefreshInterval(int minutes)
	{
		Settings.RefreshIntervalMinutes = RefreshIntervals.Normalized(minutes);
		SaveSettings();
		RefreshIntervalChanged?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Raised when the reader picks how often to refresh.</summary>
	public event EventHandler? RefreshIntervalChanged;

	public void OpenThemesFolder()
	{
		Directory.CreateDirectory(Themes.UserThemesDirectory);
		Process.Start(new ProcessStartInfo(Themes.UserThemesDirectory) { UseShellExecute = true });
	}

	public static StoredFeed? FeedFor(ArticleItem article) => article.Account.Store.GetFeed(article.Article.FeedId);

	// Search, as NetNewsWire's Mac window runs it: the timeline shows the Search smart feed for what's typed, across
	// every account, until the field is cleared or a sidebar row is chosen.

	/// <summary>What's in the search field; <see cref="ApplySearch"/> runs it.</summary>
	[ObservableProperty]
	public partial string SearchText { get; set; } = "";

	private string? _searchString;

	public bool IsSearching => _searchString is not null;

	/// <summary>
	/// Nothing in the sidebar that has articles is selected, so the timeline has nothing to show and says so. A group
	/// heading, like Smart Feeds, has no articles of its own; search results are shown whatever the sidebar has selected.
	/// </summary>
	public bool HasNoFeedSelected => !IsSearching && SelectedSidebarItem is null or { IsGroup: true };

	/// <summary>NetNewsWire's window title while searching: "Search: …".</summary>
	public string WindowTitle => _searchString is { } search ? $"Search: {search}"
		: Settings.ArticleTitleInWindowTitle && SelectedArticle?.Title is { Length: > 0 } title ? title : "WinNewsReader";

	public void ApplySearch()
	{
		var search = SearchText.Trim() is { Length: > 0 } text ? text : null;
		if (search == _searchString)
		{
			return;
		}
		_searchString = search;
		OnPropertyChanged(nameof(IsSearching));
		OnPropertyChanged(nameof(HasNoFeedSelected));
		OnPropertyChanged(nameof(WindowTitle));
		LoadArticles(keepSelection: false);
	}

	public void EndSearch()
	{
		SearchText = "";
		ApplySearch();
	}

	/// <summary>Builds the search index for articles saved before it existed, so the first search isn't slow.</summary>
	public Task PrepareSearchAsync() => Task.Run(() =>
	{
		foreach (var account in Accounts.ToList())
		{
			account.Store.EnsureSearchIndex();
		}
	});

	partial void OnSelectedSidebarItemChanged(SidebarItem? value)
	{
		// Choosing a row ends a search, as in NetNewsWire.
		if (_searchString is not null && !_rebuildingSidebar)
		{
			_searchString = null;
			SearchText = "";
			OnPropertyChanged(nameof(IsSearching));
			OnPropertyChanged(nameof(WindowTitle));
		}
		OnPropertyChanged(nameof(IsHidingReadArticles));
		OnPropertyChanged(nameof(CanHideReadArticles));
		OnPropertyChanged(nameof(HasNoFeedSelected));
		if (!_rebuildingSidebar)
		{
			LoadArticles(keepSelection: false);
		}
	}

	/// <summary>Selecting an article marks it read, as in NetNewsWire.</summary>
	partial void OnSelectedArticleChanged(ArticleItem? value)
	{
		if (Settings.ArticleTitleInWindowTitle)
		{
			OnPropertyChanged(nameof(WindowTitle));
		}
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
		OnPropertyChanged(nameof(ReadToggleGlyph));
		OnPropertyChanged(nameof(StarToggleGlyph));
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
		var selectedMany = keepSelection && SelectedArticles.Count > 1 ? SelectedArticles : null;
		var pairs = _searchString is { } search
			? Accounts.ToList().SelectMany(account => account.Store.Search(search).Select(article => (Account: account, Article: article)))
			: ArticlesFor(SelectedSidebarItem);
		if (_searchString is null && SelectedSidebarItem is { } item && HidesReadArticles(item))
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
			if (selectedMany is not null)
			{
				// A refresh keeps a multiple selection too, as far as the articles are still listed.
				SetSelectedArticles([.. Articles.Where(article => selectedMany.Any(old => old.Account == article.Account && old.ArticleId == article.ArticleId))]);
			}
			else
			{
				SelectedArticle = selected is null ? null
					: Articles.FirstOrDefault(article => article.Account == selected.Account && article.ArticleId == selected.ArticleId);
				if (SelectedArticle is null && SelectedArticles.Count > 0)
				{
					SetSelectedArticles([]);
				}
			}
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
		if (TotalUnread != allUnread)
		{
			TotalUnread = allUnread;
			UnreadBadgeChanged?.Invoke(this, EventArgs.Empty);
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
		UpdateHiddenReadFeeds(counts);
	}

	/// <summary>
	/// NetNewsWire's Hide Read Feeds follows the counts: a feed goes as soon as it has nothing unread, and comes back when
	/// it has, except the selected row, which stays while it's selected.
	/// </summary>
	/// <summary>For a new sidebar selection: a feed read through while it was selected goes once it isn't.</summary>
	public void UpdateHiddenReadFeeds() => UpdateHiddenReadFeeds(Accounts.ToDictionary(account => account, account => account.Store.UnreadCounts()));

	private void UpdateHiddenReadFeeds(Dictionary<IAccount, IReadOnlyDictionary<string, int>> counts)
	{
		if (!Settings.HideReadFeeds || _rebuildingSidebar)
		{
			return;
		}
		var shown = AllSidebarItems().Where(item => item.Feed is not null).Select(item => (item.Account!, item.Feed!.FeedId)).ToHashSet();
		var selected = SelectedSidebarItem is { Feed: { } feed } row ? (row.Account!, feed.FeedId) : default;
		var unread = counts.SelectMany(pair => pair.Value.Keys.Select(feedId => (pair.Key, feedId))).ToHashSet();
		if (unread.Any(key => !shown.Contains(key)) || shown.Any(key => key != selected && !unread.Contains(key)))
		{
			LoadSidebar();
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
