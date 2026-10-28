using Microsoft.Data.Sqlite;
using WinNewsReader.Core;
using WinNewsReader.Store;

namespace WinNewsReader.Sync.GoogleReader.Tests;

public sealed class ReaderApiAccountTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "wnr-tests-" + Guid.NewGuid().ToString("N"));
	private readonly FakeReaderServer _server = new();
	private readonly ArticlesStore _store;
	private readonly FeedDownloader _downloader;
	private readonly ReaderApiAccount _account;

	public ReaderApiAccountTests()
	{
		_store = new ArticlesStore(Path.Combine(_directory, "articles.db"));
		_downloader = new FeedDownloader(_server);
		var record = new AccountRecord("fresh", AccountKind.FreshRss, "FreshRSS", FakeReaderServer.Endpoint, FakeReaderServer.Username);
		_account = new ReaderApiAccount(record, _store, NewCaller(), _downloader);
	}

	public void Dispose()
	{
		_account.Dispose();
		_downloader.Dispose();
		_store.Dispose();
		SqliteConnection.ClearAllPools();
		Directory.Delete(_directory, recursive: true);
	}

	[Fact]
	public async Task FirstSyncBringsFeedsFoldersArticlesAndStatuses()
	{
		var tech = _server.AddFeed("Tech ＆ Code", "https://tech.example/feed", "Tech");
		var news = _server.AddFeed("News", "https://news.example/feed");
		_server.AddItem(101, tech, "Unread <one>");
		_server.AddItem(102, tech, "Read", read: true);
		_server.AddItem(103, news, "Starred", read: true, starred: true);

		var summary = await ((IAccount)_account).RefreshAllAsync();

		Assert.Null(summary.Error);
		Assert.Equal(2, summary.NewArticles);
		var folder = Assert.Single(_store.GetFolders());
		Assert.Equal("Tech", folder.Name);
		Assert.Equal("Tech & Code", Assert.Single(_store.GetFeeds(folder.FolderId)).Name);
		Assert.Equal(["feed/2"], _store.GetTopLevelFeeds().Select(feed => feed.FeedId));
		// Unread and starred articles are downloaded; a read, unstarred one keeps only its status, as in NetNewsWire.
		Assert.Equal(["101", "103"], _store.GetArticles(["feed/1", "feed/2"]).Select(article => article.ArticleId).Order());
		Assert.False(_store.GetArticle("101")!.Read);
		Assert.True(_store.GetArticle("103")!.Starred);
		Assert.Equal(["101"], _store.StatusArticleIds(StatusKey.Read, false));
		Assert.Equal("https://site.example/101", _store.GetArticle("101")!.ExternalUrl);
	}

	[Fact]
	public async Task LocalChangesAreSentAndServerChangesComeBack()
	{
		var feed = _server.AddFeed("Feed", "https://a.example/feed");
		var first = _server.AddItem(1, feed, "One");
		var second = _server.AddItem(2, feed, "Two");
		await _account.RefreshAllAsync();

		_account.SetArticleStatus(["1"], StatusKey.Read, true);
		_account.SetArticleStatus(["2"], StatusKey.Starred, true);
		await _account.SendQueuedStatusesAsync();
		Assert.True(first.Read);
		Assert.True(second.Starred);
		Assert.Equal(0, _store.PendingSyncStatusCount());

		// Another client marks things on the server.
		first.Read = false;
		second.Starred = false;
		second.Read = true;
		await _account.RefreshAllAsync();
		Assert.False(_store.GetArticle("1")!.Read);
		Assert.False(_store.GetArticle("2")!.Starred);
		Assert.True(_store.GetArticle("2")!.Read);
	}

	[Fact]
	public async Task QueuedLocalChangeWinsUntilItIsSent()
	{
		var feed = _server.AddFeed("Feed", "https://a.example/feed");
		var item = _server.AddItem(1, feed, "One");
		await _account.RefreshAllAsync();

		_server.FailEditTag = true;
		_account.SetArticleStatus(["1"], StatusKey.Read, true);
		await _account.RefreshAllAsync();
		// The send failed, so the server still says unread, but the local read stays and stays queued.
		Assert.False(item.Read);
		Assert.True(_store.GetArticle("1")!.Read);
		Assert.Equal(1, _store.PendingSyncStatusCount());

		_server.FailEditTag = false;
		await _account.RefreshAllAsync();
		Assert.True(item.Read);
		Assert.Equal(0, _store.PendingSyncStatusCount());
	}

	[Fact]
	public async Task ExpiredLoginAndWriteTokenAreRenewed()
	{
		var feed = _server.AddFeed("Feed", "https://a.example/feed");
		var item = _server.AddItem(1, feed, "One");
		await _account.RefreshAllAsync();
		Assert.Equal(1, _server.LoginCount);

		_server.AuthToken = "auth-2";
		_server.WriteToken = "write-2";
		_account.SetArticleStatus(["1"], StatusKey.Read, true);
		await _account.SendQueuedStatusesAsync();

		Assert.True(item.Read);
		Assert.Equal(2, _server.LoginCount);
	}

	[Fact]
	public async Task ItemIdsFollowContinuations()
	{
		var feed = _server.AddFeed("Feed", "https://a.example/feed");
		for (var id = 1; id <= 7; id++)
		{
			_server.AddItem(id, feed, $"Item {id}");
		}
		_server.IdsPageSize = 3;

		await _account.RefreshAllAsync();

		Assert.Equal(7, _store.GetArticles("feed/1").Count);
	}

	[Fact]
	public async Task FeedsAndFoldersRemovedOnTheServerGoLocally()
	{
		var kept = _server.AddFeed("Kept", "https://a.example/feed", "Old");
		var dropped = _server.AddFeed("Dropped", "https://b.example/feed", "Other");
		_server.AddItem(1, dropped, "One");
		await _account.RefreshAllAsync();
		Assert.Equal(2, _store.GetFolders().Count);

		_server.Feeds.Remove(dropped);
		_server.Labels.Remove("Other");
		kept.Labels.Clear();
		_server.Labels.Remove("Old");
		_server.Labels.Add("New");
		kept.Labels.Add("New");
		await _account.RefreshAllAsync();

		Assert.Equal(["New"], _store.GetFolders().Select(folder => folder.Name));
		Assert.Equal(["Kept"], _store.GetFeeds().Select(feed => feed.Name));
		Assert.Empty(_store.GetArticles("feed/2"));
	}

	[Fact]
	public async Task SubscribeFindsTheFeedAddsItToAFolderAndDownloads()
	{
		foreach (var scheme in new[] { "http", "https" })
		{
			_server.Sites[$"{scheme}://blog.example/"] = """<html><head><link rel="alternate" type="application/rss+xml" href="https://blog.example/feed.xml"></head></html>""";
		}
		_server.Sites["https://blog.example/feed.xml"] = """<?xml version="1.0"?><rss version="2.0"><channel><title>Blog</title><link>https://blog.example/</link></channel></rss>""";
		_server.QuickAddItems = feed => _server.AddItem(500, feed, "First post");

		var feed = await _account.SubscribeAsync("blog.example", "Reading");

		Assert.Equal("https://blog.example/feed.xml", feed.Url);
		var folder = Assert.Single(_store.GetFolders());
		Assert.Equal(feed.FeedId, Assert.Single(_store.GetFeeds(folder.FolderId)).FeedId);
		Assert.Equal("First post", Assert.Single(_store.GetArticles(feed.FeedId)).Title);
	}

	[Fact]
	public async Task RemoveFolderUnsubscribesFeedsOnlyInIt()
	{
		var only = _server.AddFeed("Only", "https://a.example/feed", "Gone");
		var shared = _server.AddFeed("Shared", "https://b.example/feed", "Gone", "Stays");
		await _account.RefreshAllAsync();
		var gone = _store.GetFolders().Single(folder => folder.Name == "Gone");

		await _account.RemoveFolderAsync(gone.FolderId);

		Assert.DoesNotContain(only, _server.Feeds);
		Assert.Equal(["Stays"], shared.Labels);
		Assert.DoesNotContain("Gone", _server.Labels);
		Assert.Equal(["Shared"], _store.GetFeeds().Select(feed => feed.Name));
		Assert.Equal(["Stays"], _store.GetFolders().Select(folder => folder.Name));
	}

	[Fact]
	public async Task RenameFolderRenamesTheLabel()
	{
		var feed = _server.AddFeed("Feed", "https://a.example/feed", "Old");
		await _account.RefreshAllAsync();

		await _account.RenameFolderAsync(_store.GetFolders()[0].FolderId, "New");

		Assert.Equal(["New"], feed.Labels);
		Assert.Equal(["New"], _store.GetFolders().Select(folder => folder.Name));
		await _account.RefreshAllAsync();
		Assert.Equal(["New"], _store.GetFolders().Select(folder => folder.Name));
	}

	[Fact]
	public async Task WrongPasswordIsReported()
	{
		using var caller = new ReaderApiCaller(FakeReaderServer.Endpoint, FakeReaderServer.Username, "wrong", _server);
		await Assert.ThrowsAsync<ReaderApiAuthenticationException>(() => caller.LoginAsync());
	}

	[Fact]
	public async Task RefreshReportsServerErrorsWithoutThrowing()
	{
		using var caller = new ReaderApiCaller("https://rss.example/api/missing.php", FakeReaderServer.Username, FakeReaderServer.Password, _server);
		var record = new AccountRecord("x", AccountKind.FreshRss, "FreshRSS", "https://rss.example/api/missing.php", FakeReaderServer.Username);
		using var account = new ReaderApiAccount(record, _store, caller, _downloader);

		var summary = await account.RefreshAllAsync();

		Assert.StartsWith("FreshRSS: ", summary.Error);
	}

	[Theory]
	[InlineData("rss.example", "https://rss.example/api/greader.php")]
	[InlineData("https://rss.example/", "https://rss.example/api/greader.php")]
	[InlineData("https://rss.example/freshrss/api/greader.php", "https://rss.example/freshrss/api/greader.php")]
	[InlineData("http://192.168.1.5:8080", "http://192.168.1.5:8080/api/greader.php")]
	public void EndpointFromServerAddress(string typed, string expected) => Assert.Equal(expected, ReaderApiCaller.NormalizedEndpoint(typed));

	[Theory]
	[InlineData("tag:google.com,2005:reader/item/00058a3b5197197b", "1559362260113787")]
	[InlineData("tag:google.com,2005:reader/item/ffffffffffffffff", "-1")]
	public void ArticleIdsRoundTrip(string entryId, string articleId)
	{
		Assert.Equal(articleId, ReaderApiCaller.ArticleIdFromEntryId(entryId));
		Assert.Equal(entryId, ReaderApiCaller.LongItemId(articleId));
	}

	[Fact]
	public void NonNumericArticleIdsCantBeSent() => Assert.Null(ReaderApiCaller.LongItemId("abc"));

	private ReaderApiCaller NewCaller() =>
		new(FakeReaderServer.Endpoint, FakeReaderServer.Username, FakeReaderServer.Password, _server);
}
