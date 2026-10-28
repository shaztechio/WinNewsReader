using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using WinNewsReader.Core;
using WinNewsReader.Store;

namespace WinNewsReader.App.ViewModels;

/// <summary>State and actions for the main window: sidebar, timeline and the selected article.</summary>
public sealed partial class MainViewModel : ObservableObject
{
	private readonly LocalAccount _account;
	private readonly HashSet<string> _expandedKeys = [nameof(SidebarItemKind.SmartFeedsGroup), nameof(SidebarItemKind.AccountGroup)];
	private bool _rebuildingSidebar;

	public MainViewModel(LocalAccount account)
	{
		_account = account;
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
		}
		UpdateUnreadCounts();
		SelectedSidebarItem = FindSidebarItem(selectedKey);
		_rebuildingSidebar = false;
		LoadArticles(keepSelection: true);
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

	/// <summary>Selects the next unread article after the selection, wrapping to the top of the timeline.</summary>
	public void SelectNextUnread()
	{
		var start = SelectedArticle is null ? 0 : IndexOf(SelectedArticle) + 1;
		var next = Articles.Skip(start).Concat(Articles.Take(start)).FirstOrDefault(article => !article.IsRead);
		if (next is not null)
		{
			SelectedArticle = next;
		}
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
		Articles = [.. articles.Select(article => new ArticleItem(article, feedNames.GetValueOrDefault(article.FeedId, "")))];
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
}
