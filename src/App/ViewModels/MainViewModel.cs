using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinNewsReader.Core;
using WinNewsReader.Store;

namespace WinNewsReader.App.ViewModels;

/// <summary>State and actions for the main window: sidebar, timeline and the selected article.</summary>
public sealed partial class MainViewModel : ObservableObject
{
	/// <summary>Favicon lookups at once; the downloader's per-host limits apply on top.</summary>
	private const int MaxConcurrentIconLookups = 6;

	private readonly LocalAccount _account;
	private readonly FaviconDownloader _favicons;
	private readonly HashSet<string> _expandedKeys = [nameof(SidebarItemKind.SmartFeedsGroup), nameof(SidebarItemKind.AccountGroup)];
	private readonly Dictionary<string, ImageSource> _icons = [];
	private bool _rebuildingSidebar;

	public MainViewModel(LocalAccount account, FaviconDownloader favicons)
	{
		_account = account;
		_favicons = favicons;
		Articles = [];
		StatusText = "";
		LoadSidebar();
	}

	/// <summary>NetNewsWire's <c>todayCutoffDate</c>: 24 hours ago, so Today doesn't empty out at midnight.</summary>
	private static DateTimeOffset TodayCutoff => DateTimeOffset.Now.AddHours(-24);

	private ArticlesStore Store => _account.Store;

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

	public IEnumerable<StoredFolder> Folders => Store.GetFolders().SortedByName();

	/// <summary>The folder a new feed goes in by default: the selected folder, or the folder of the selected feed.</summary>
	public StoredFolder? DefaultFolderForNewFeed => SelectedSidebarItem?.Folder ?? SelectedSidebarItem?.ParentFolder;

	/// <summary>Rebuilds the sidebar from the store, keeping expanded folders and the selection.</summary>
	public void LoadSidebar()
	{
		foreach (var item in AllSidebarItems())
		{
			if (item.IsExpanded)
			{
				_expandedKeys.Add(item.Key);
			}
			else
			{
				_expandedKeys.Remove(item.Key);
			}
		}
		var selectedKey = SelectedSidebarItem?.Key;

		_rebuildingSidebar = true;
		SidebarItems.Clear();
		SidebarItems.Add(SidebarItem.Group(SidebarItemKind.SmartFeedsGroup, "Smart Feeds",
		[
			SidebarItem.SmartFeed(SidebarItemKind.Today, "Today"),
			SidebarItem.SmartFeed(SidebarItemKind.AllUnread, "All Unread"),
			SidebarItem.SmartFeed(SidebarItemKind.Starred, "Starred"),
		]));
		// NetNewsWire sorts feeds alphabetically with folders at the end.
		SidebarItems.Add(SidebarItem.Group(SidebarItemKind.AccountGroup, "On This PC",
		[
			.. Store.GetTopLevelFeeds().SortedByName().Select(feed => SidebarItem.ForFeed(feed, null)),
			.. Store.GetFolders().SortedByName().Select(folder =>
				SidebarItem.ForFolder(folder, Store.GetFeeds(folder.FolderId).SortedByName().Select(feed => SidebarItem.ForFeed(feed, folder)))),
		]));
		foreach (var item in AllSidebarItems())
		{
			item.IsExpanded = _expandedKeys.Contains(item.Key);
			item.Icon = item.Feed is null ? null : _icons.GetValueOrDefault(item.Feed.FeedId);
		}
		UpdateUnreadCounts();
		SelectedSidebarItem = FindSidebarItem(selectedKey);
		_rebuildingSidebar = false;
		LoadArticles(keepSelection: true);
		_ = LoadIconsAsync();
	}

	/// <summary>Fetches favicons for feeds that don't have one yet. Misses are remembered by the downloader, so this is cheap to repeat.</summary>
	public async Task LoadIconsAsync()
	{
		var feeds = AllSidebarItems().Select(item => item.Feed).OfType<StoredFeed>()
			.DistinctBy(feed => feed.FeedId).Where(feed => !_icons.ContainsKey(feed.FeedId)).ToList();
		using var gate = new SemaphoreSlim(MaxConcurrentIconLookups);
		await Task.WhenAll(feeds.Select(async feed =>
		{
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
				SetIcon(feed.FeedId, path);
			}
		}));
	}

	private void SetIcon(string feedId, string path)
	{
		// Decoded at 48 px so 16–24 px icons stay sharp at 200% scaling.
		var icon = new BitmapImage { DecodePixelWidth = 48, UriSource = new Uri(path) };
		_icons[feedId] = icon;
		foreach (var item in AllSidebarItems().Where(item => item.Feed?.FeedId == feedId))
		{
			item.Icon = icon;
		}
		foreach (var article in Articles.Where(article => article.Article.FeedId == feedId))
		{
			article.Icon = icon;
		}
	}

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
			// Parsing is CPU-bound, so keep the whole refresh off the UI thread.
			var results = await Task.Run(() => _account.RefreshAllAsync());
			var feeds = Store.GetFeeds().ToDictionary(feed => feed.FeedId);
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

			var failed = results.Count(result => result.Status == DownloadStatus.Failed);
			var newArticles = results.Sum(result => result.NewArticles);
			StatusText = string.Join(" · ", new[]
			{
				newArticles == 1 ? "1 new article" : $"{newArticles:N0} new articles",
				failed == 0 ? null : failed == 1 ? "1 feed failed" : $"{failed} feeds failed",
				$"Updated {DateTime.Now:t}",
			}.Where(part => part is not null));
		}
		finally
		{
			IsRefreshing = false;
		}
	}

	/// <summary>Throws <see cref="SubscribeException"/> when no feed is found.</summary>
	public async Task SubscribeAsync(string url, StoredFolder? folder)
	{
		var (feed, _) = await Task.Run(() => _account.SubscribeAsync(url, folder?.Name));
		LoadSidebar();
		var item = FindSidebarItem($"feed:{folder?.FolderId}:{feed.FeedId}");
		if (item is not null && folder is not null)
		{
			FindSidebarItem($"folder:{folder.FolderId}")!.IsExpanded = true;
		}
		SelectedSidebarItem = item;
	}

	public void AddFolder(string name)
	{
		var folder = Store.EnsureFolder(name);
		LoadSidebar();
		SelectedSidebarItem = FindSidebarItem($"folder:{folder.FolderId}");
	}

	public void RenameFolder(SidebarItem item, string name)
	{
		Store.RenameFolder(item.Folder!.FolderId, name);
		LoadSidebar();
	}

	/// <summary>A feed is removed from the folder or top level it's shown in; a folder goes with its feeds.</summary>
	public void Delete(SidebarItem item)
	{
		if (item.Feed is { } feed)
		{
			Store.RemoveFeed(feed.FeedId, item.ParentFolder?.FolderId);
		}
		else if (item.Folder is { } folder)
		{
			Store.RemoveFolder(folder.FolderId);
		}
		LoadSidebar();
	}

	/// <summary>Imports, then refreshes so the new feeds fill in, as NetNewsWire does.</summary>
	public async Task<OpmlImportResult> ImportOpmlAsync(string path)
	{
		var result = await Task.Run(() => _account.ImportOpml(path));
		LoadSidebar();
		_ = RefreshAsync();
		return result;
	}

	public void ExportOpml(string path) =>
		File.WriteAllText(path, OpmlExporter.OpmlString(Store, Path.GetFileNameWithoutExtension(path)));

	/// <summary>Marks every article in <paramref name="item"/> read: the selected sidebar item by default.</summary>
	public void MarkAllRead(SidebarItem? item = null)
	{
		item ??= SelectedSidebarItem;
		if (item is null)
		{
			return;
		}
		var ids = (item == SelectedSidebarItem
			? Articles.Where(article => !article.IsRead).Select(article => article.ArticleId)
			: ArticlesFor(item).Where(article => !article.Read).Select(article => article.ArticleId)).ToHashSet();
		Store.MarkRead(ids);
		foreach (var article in Articles.Where(article => ids.Contains(article.ArticleId)))
		{
			article.IsRead = true;
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
			Store.MarkStarred([target.ArticleId], !target.IsStarred);
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
		var cutoff = selected.Article.DatePublished ?? selected.Article.DateArrived;
		var older = Articles.Where(article => !article.IsRead && (article.Article.DatePublished ?? article.Article.DateArrived) < cutoff).ToList();
		Store.MarkRead(older.Select(article => article.ArticleId));
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
			item.IsExpanded = expanded;
		}
	}

	/// <summary>Expands or collapses every folder; the two groups stay open so the selection stays visible.</summary>
	public void SetAllExpanded(bool expanded)
	{
		foreach (var item in AllSidebarItems().Where(item => item.Kind == SidebarItemKind.Folder))
		{
			item.IsExpanded = expanded;
		}
		if (!expanded && SelectedSidebarItem?.ParentFolder is not null)
		{
			SelectedSidebarItem = FindSidebarItem($"folder:{SelectedSidebarItem.ParentFolder.FolderId}");
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
			FindSidebarItem($"folder:{folder.FolderId}")!.IsExpanded = true;
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

	public string FeedNameFor(StoredArticle article) =>
		Store.GetFeed(article.FeedId) is { } feed ? NameSorting.DisplayName(feed) : "";

	public StoredFeed? FeedFor(StoredArticle article) => Store.GetFeed(article.FeedId);

	partial void OnSelectedSidebarItemChanged(SidebarItem? value)
	{
		if (!_rebuildingSidebar)
		{
			LoadArticles(keepSelection: false);
		}
	}

	/// <summary>Selecting an article marks it read, as in NetNewsWire.</summary>
	partial void OnSelectedArticleChanged(ArticleItem? value)
	{
		if (value is { IsRead: false })
		{
			SetRead(value, true);
		}
	}

	private void SetRead(ArticleItem article, bool read)
	{
		Store.MarkRead([article.ArticleId], read);
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
		var articles = ArticlesFor(SelectedSidebarItem).ToList();
		// Keep the article being read in view even when it no longer matches, as when All Unread refreshes.
		if (selected is not null && !articles.Any(article => article.ArticleId == selected.ArticleId) && Store.GetArticle(selected.ArticleId) is { } current)
		{
			articles.Add(current);
			articles = [.. articles.OrderByDescending(article => article.DatePublished ?? article.DateArrived).ThenBy(article => article.ArticleId, StringComparer.Ordinal)];
		}
		var feedNames = Store.GetFeeds().ToDictionary(feed => feed.FeedId, NameSorting.DisplayName);
		Articles = [.. articles.Select(article => new ArticleItem(article, feedNames.GetValueOrDefault(article.FeedId, ""), _icons.GetValueOrDefault(article.FeedId)))];
		SelectedArticle = selected is null ? null : Articles.FirstOrDefault(article => article.ArticleId == selected.ArticleId);
	}

	private IReadOnlyList<StoredArticle> ArticlesFor(SidebarItem? item) => item?.Kind switch
	{
		SidebarItemKind.Today => Store.GetArticlesSince(TodayCutoff),
		SidebarItemKind.AllUnread => Store.GetUnreadArticles(),
		SidebarItemKind.Starred => Store.GetStarredArticles(),
		SidebarItemKind.Folder => Store.GetArticles(item.Children.Select(child => child.Feed!.FeedId)),
		SidebarItemKind.Feed => Store.GetArticles(item.Feed!.FeedId),
		_ => [],
	};

	private void UpdateUnreadCounts()
	{
		var counts = Store.UnreadCounts();
		var total = counts.Values.Sum();
		foreach (var group in SidebarItems)
		{
			Update(group);
		}

		int Update(SidebarItem item)
		{
			var childTotal = item.Children.Sum(Update);
			item.UnreadCount = item.Kind switch
			{
				SidebarItemKind.Feed => counts.GetValueOrDefault(item.Feed!.FeedId),
				SidebarItemKind.Folder => childTotal,
				SidebarItemKind.AccountGroup or SidebarItemKind.AllUnread => total,
				SidebarItemKind.Today => Store.UnreadCountSince(TodayCutoff),
				SidebarItemKind.Starred => Store.StarredUnreadCount(),
				_ => 0,
			};
			return item.UnreadCount;
		}
	}

	private IEnumerable<SidebarItem> AllSidebarItems() => SidebarItems.SelectMany(item => item.SelfAndDescendants());

	/// <summary>Rows in the order shown, leaving out the children of collapsed rows.</summary>
	private IEnumerable<SidebarItem> VisibleSidebarItems()
	{
		return SidebarItems.SelectMany(Visible);

		static IEnumerable<SidebarItem> Visible(SidebarItem item) =>
			item.IsExpanded ? item.Children.SelectMany(Visible).Prepend(item) : [item];
	}
}
