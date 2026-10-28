using Microsoft.Data.Sqlite;
using WinNewsReader.Parser.Feeds;

namespace WinNewsReader.Store.Tests;

/// <summary>The store's support for sync accounts: service article IDs, statuses ahead of articles, and the send queue.</summary>
public sealed class SyncStoreTests : IDisposable
{
	private const string FeedId = "feed/1";

	private readonly string _directory = Path.Combine(Path.GetTempPath(), "wnr-tests-" + Guid.NewGuid().ToString("N"));
	private readonly ArticlesStore _store;

	public SyncStoreTests()
	{
		_store = new ArticlesStore(Path.Combine(_directory, "articles.db"));
		_store.SyncFeed(FeedId, "https://a.example/feed", "A", "https://a.example/");
	}

	public void Dispose()
	{
		_store.Dispose();
		SqliteConnection.ClearAllPools();
		Directory.Delete(_directory, recursive: true);
	}

	[Fact]
	public void SyncServiceIdIsTheArticleId()
	{
		_store.UpsertArticles(FeedId, [Item("1001")], defaultRead: true);
		var article = Assert.Single(_store.GetArticles(FeedId));
		Assert.Equal("1001", article.ArticleId);
		Assert.True(article.Read);
	}

	[Fact]
	public void StatusesCanComeBeforeArticles()
	{
		var changed = _store.SetStatuses(["1001", "1002"], StatusKey.Read, true);
		Assert.Equal(["1001", "1002"], changed.Order());
		_store.SetStatuses(["1002"], StatusKey.Read, false);
		_store.SetStatuses(["1003"], StatusKey.Starred, true);

		Assert.Equal(["1002", "1003"], _store.StatusArticleIds(StatusKey.Read, false).Order());
		Assert.Equal(["1003"], _store.StatusArticleIds(StatusKey.Starred, true));
		Assert.Equal(["1002", "1003"], _store.ArticleIdsMissingContent(DateTimeOffset.UtcNow.AddDays(-1)).Order());

		// The downloaded article keeps the status it already had, rather than the default.
		_store.UpsertArticles(FeedId, [Item("1002")], defaultRead: true);
		Assert.False(_store.GetArticle("1002")!.Read);
		Assert.Equal(["1003"], _store.ArticleIdsMissingContent(DateTimeOffset.UtcNow.AddDays(-1)));
	}

	[Fact]
	public void SetStatusesReportsOnlyChanges()
	{
		_store.SetStatuses(["1001"], StatusKey.Read, true);
		Assert.Empty(_store.SetStatuses(["1001"], StatusKey.Read, true));
		Assert.Equal(["1001"], _store.SetStatuses(["1001"], StatusKey.Read, false));
	}

	[Fact]
	public void SyncQueueSelectsSendsAndKeepsNewerChanges()
	{
		_store.QueueSyncStatuses(["1", "2"], StatusKey.Read, true);
		_store.QueueSyncStatuses(["3"], StatusKey.Starred, true);
		Assert.Equal(3, _store.PendingSyncStatusCount());

		var selected = _store.SelectSyncStatusesForProcessing();
		Assert.Equal(3, selected.Count);
		Assert.Contains(new SyncStatus("3", StatusKey.Starred, true), selected);
		Assert.Equal(0, _store.PendingSyncStatusCount());

		// Article 1 changes again while being sent: the newer change must survive the delete.
		_store.QueueSyncStatuses(["1"], StatusKey.Read, false);
		_store.DeleteSelectedSyncStatuses(["1", "2"], StatusKey.Read);
		_store.ResetSelectedSyncStatuses(["3"], StatusKey.Starred);

		var next = _store.SelectSyncStatusesForProcessing();
		Assert.Equal([new SyncStatus("1", StatusKey.Read, false), new SyncStatus("3", StatusKey.Starred, true)], next.OrderBy(s => s.ArticleId));
		Assert.Equal(["1"], _store.PendingSyncArticleIds(StatusKey.Read));
	}

	[Fact]
	public void ResetAllSelectedRequeuesInterruptedSends()
	{
		_store.QueueSyncStatuses(["1"], StatusKey.Read, true);
		_store.SelectSyncStatusesForProcessing();
		_store.ResetAllSelectedSyncStatuses();
		Assert.Equal(1, _store.PendingSyncStatusCount());
	}

	[Fact]
	public void SyncFeedUpdatesAndPlacementReplaces()
	{
		_store.SyncFeed(FeedId, "https://a.example/rss", "", "https://a.example/home");
		var feed = _store.GetFeed(FeedId)!;
		Assert.Equal("https://a.example/rss", feed.Url);
		Assert.Equal("A", feed.Name);
		Assert.Equal("https://a.example/home", feed.HomePageUrl);

		var tech = _store.EnsureFolder("Tech");
		var news = _store.EnsureFolder("News");
		_store.SetFeedPlacement(FeedId, topLevel: false, [tech.FolderId, news.FolderId]);
		Assert.Empty(_store.GetTopLevelFeeds());
		Assert.Single(_store.GetFeeds(news.FolderId));

		_store.SetFeedPlacement(FeedId, topLevel: true, []);
		Assert.Single(_store.GetTopLevelFeeds());
		Assert.Empty(_store.GetFeeds(tech.FolderId));
	}

	[Fact]
	public void Metadata()
	{
		Assert.Null(_store.GetMetadata("lastFetch"));
		_store.SetMetadata("lastFetch", "123");
		Assert.Equal("123", _store.GetMetadata("lastFetch"));
	}

	private static ParsedItem Item(string id) => new() { FeedUrl = FeedId, UniqueId = id, SyncServiceId = id, Title = id };
}
