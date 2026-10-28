using Microsoft.Data.Sqlite;
using WinNewsReader.Parser.Feeds;

namespace WinNewsReader.Store.Tests;

/// <summary>The queries behind the smart feeds, folder timelines and sidebar unread counts.</summary>
public sealed class StoreQueryTests : IDisposable
{
	private const string FeedA = "https://a.example/feed";
	private const string FeedB = "https://b.example/feed";
	private const string FeedC = "https://c.example/feed";

	private readonly string _directory = Path.Combine(Path.GetTempPath(), "wnr-tests-" + Guid.NewGuid().ToString("N"));
	private readonly ArticlesStore _store;

	public StoreQueryTests()
	{
		_store = new ArticlesStore(Path.Combine(_directory, "articles.db"));
		foreach (var feed in new[] { FeedA, FeedB, FeedC })
		{
			_store.AddFeed(feed);
		}
	}

	public void Dispose()
	{
		_store.Dispose();
		SqliteConnection.ClearAllPools();
		Directory.Delete(_directory, recursive: true);
	}

	[Fact]
	public void ArticlesForSeveralFeeds()
	{
		_store.UpsertArticles(FeedA, [Item("a")]);
		_store.UpsertArticles(FeedB, [Item("b")]);
		_store.UpsertArticles(FeedC, [Item("c")]);

		Assert.Equal(["a", "b"], _store.GetArticles([FeedA, FeedB]).Select(a => a.UniqueId).Order());
		Assert.Empty(_store.GetArticles(Array.Empty<string>()));
	}

	[Fact]
	public void StarredArticlesAndUnreadCount()
	{
		_store.UpsertArticles(FeedA, [Item("a"), Item("b"), Item("c")]);
		var ids = _store.GetArticles(FeedA).ToDictionary(a => a.UniqueId, a => a.ArticleId);
		_store.MarkStarred([ids["a"], ids["b"]]);
		_store.MarkRead([ids["b"]]);

		Assert.Equal(["a", "b"], _store.GetStarredArticles().Select(a => a.UniqueId).Order());
		Assert.Equal(1, _store.StarredUnreadCount());
	}

	[Fact]
	public void TodayUsesPublishDateThenArrivalDate()
	{
		var now = DateTimeOffset.UtcNow;
		_store.UpsertArticles(FeedA,
		[
			Item("recent", now.AddHours(-2)),
			Item("old", now.AddDays(-3)),
			// No publish date: arrived just now, so it counts.
			Item("undated", null),
		]);

		var cutoff = now.AddHours(-24);
		Assert.Equal(["recent", "undated"], _store.GetArticlesSince(cutoff).Select(a => a.UniqueId).Order());
		Assert.Equal(2, _store.UnreadCountSince(cutoff));
	}

	[Fact]
	public void ArticlesSortByLogicalPublishDate()
	{
		var now = DateTimeOffset.UtcNow;
		_store.UpsertArticles(FeedA,
		[
			Item("published-old", now.AddDays(-3)),
			// Only an Atom <updated> date: sorts by it, not by when it arrived.
			new() { FeedUrl = "", UniqueId = "modified-only", DateModified = now.AddDays(-2) },
			Item("published-new", now.AddDays(-1)),
		]);

		Assert.Equal(["published-new", "modified-only", "published-old"], _store.GetArticles(FeedA).Select(a => a.UniqueId));
		Assert.Equal(now.AddDays(-2).ToUnixTimeSeconds(), _store.GetArticles(FeedA)[1].LogicalDatePublished.ToUnixTimeSeconds());
	}

	[Fact]
	public void UnreadCountsByFeed()
	{
		_store.UpsertArticles(FeedA, [Item("a1"), Item("a2")]);
		_store.UpsertArticles(FeedB, [Item("b1")]);
		_store.MarkAllRead(FeedB);

		var counts = _store.UnreadCounts();
		Assert.Equal(2, counts[FeedA]);
		Assert.False(counts.ContainsKey(FeedB));
		Assert.False(counts.ContainsKey(FeedC));
	}

	private static ParsedItem Item(string uniqueId, DateTimeOffset? datePublished = default) =>
		new() { FeedUrl = "", UniqueId = uniqueId, Title = uniqueId, DatePublished = datePublished };
}
