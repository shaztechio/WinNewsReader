using Microsoft.Data.Sqlite;
using WinNewsReader.Core;
using WinNewsReader.Store;

namespace WinNewsReader.Sync.GoogleReader.Tests;

/// <summary>Moving and copying feeds between the local account and a FreshRSS account.</summary>
public sealed class FeedTransferTests : IDisposable
{
	private const string FeedUrl = "https://blog.example/feed.xml";

	private readonly string _directory = Path.Combine(Path.GetTempPath(), "wnr-tests-" + Guid.NewGuid().ToString("N"));
	private readonly FakeReaderServer _server = new();
	private readonly FeedDownloader _downloader;
	private readonly ArticlesStore _localStore;
	private readonly ArticlesStore _freshStore;
	private readonly LocalAccount _local;
	private readonly ReaderApiAccount _fresh;

	public FeedTransferTests()
	{
		_server.Sites[FeedUrl] = """
			<?xml version="1.0"?>
			<rss version="2.0"><channel><title>Blog</title><link>https://blog.example/</link>
			<item><guid>post-1</guid><title>Post one</title><pubDate>Tue, 27 Oct 2026 10:00:00 GMT</pubDate></item>
			</channel></rss>
			""";
		_downloader = new FeedDownloader(_server);
		_localStore = new ArticlesStore(Path.Combine(_directory, "local.db"));
		_freshStore = new ArticlesStore(Path.Combine(_directory, "fresh.db"));
		_local = new LocalAccount(_localStore, _downloader);
		var record = new AccountRecord("fresh", AccountKind.FreshRss, "FreshRSS", FakeReaderServer.Endpoint, FakeReaderServer.Username);
		_fresh = new ReaderApiAccount(record, _freshStore,
			new ReaderApiCaller(FakeReaderServer.Endpoint, FakeReaderServer.Username, FakeReaderServer.Password, _server), _downloader);
	}

	public void Dispose()
	{
		_fresh.Dispose();
		_downloader.Dispose();
		_localStore.Dispose();
		_freshStore.Dispose();
		SqliteConnection.ClearAllPools();
		Directory.Delete(_directory, recursive: true);
	}

	[Fact]
	public async Task MoveFromLocalToFreshRssSubscribesThereAndRemovesHere()
	{
		var (feed, _) = await _local.SubscribeAsync(FeedUrl);

		var moved = await FeedTransfer.TransferAsync(_local, feed, null, _fresh, "Reading", copy: false);

		var serverFeed = Assert.Single(_server.Feeds);
		Assert.Equal(FeedUrl, serverFeed.Url);
		Assert.Equal(["Reading"], serverFeed.Labels);
		Assert.Equal(serverFeed.Id, moved.FeedId);
		Assert.Equal(["Reading"], _freshStore.GetFolders().Select(folder => folder.Name));
		Assert.Empty(_localStore.GetFeeds());
	}

	[Fact]
	public async Task CopyFromFreshRssToLocalKeepsBoth()
	{
		var serverFeed = _server.AddFeed("Blog", FeedUrl, "Reading");
		await _fresh.RefreshAllAsync();
		var feed = _freshStore.GetFeed(serverFeed.Id)!;
		var folderId = _freshStore.GetFolders().Single().FolderId;

		var copied = await FeedTransfer.TransferAsync(_fresh, feed, folderId, _local, null, copy: true);

		Assert.Equal(FeedUrl, copied.FeedId);
		Assert.Equal("Post one", Assert.Single(_localStore.GetArticles(FeedUrl)).Title);
		Assert.Single(_server.Feeds);
		Assert.NotNull(_freshStore.GetFeed(serverFeed.Id));
	}

	[Fact]
	public async Task JsonFeedsCantMoveToFreshRss()
	{
		const string jsonUrl = "https://json.example/feed.json";
		// Over 128 bytes: NetNewsWire doesn't take anything shorter as a feed.
		_server.Sites[jsonUrl] = """
			{"version": "https://jsonfeed.org/version/1.1", "title": "Json", "home_page_url": "https://json.example/",
			"items": [{"id": "1", "url": "https://json.example/1", "content_text": "Hello from a JSON Feed"}]}
			""";
		var (feed, _) = await _local.SubscribeAsync(jsonUrl);

		var error = await Assert.ThrowsAsync<SubscribeException>(() => FeedTransfer.TransferAsync(_local, feed, null, _fresh, null, copy: false));

		Assert.Contains("JSON Feed", error.Message);
		Assert.Single(_localStore.GetFeeds());
	}

	[Fact]
	public async Task FailedSubscribeRemovesNothing()
	{
		var (feed, _) = await _local.SubscribeAsync(FeedUrl);
		_server.Sites.Remove(FeedUrl);

		await Assert.ThrowsAsync<SubscribeException>(() => FeedTransfer.TransferAsync(_local, feed, null, _fresh, null, copy: false));

		Assert.Single(_localStore.GetFeeds());
		Assert.Empty(_server.Feeds);
	}
}
