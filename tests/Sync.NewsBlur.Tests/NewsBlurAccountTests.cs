using Microsoft.Data.Sqlite;
using WinNewsReader.Core;
using WinNewsReader.Store;

namespace WinNewsReader.Sync.NewsBlur.Tests;

public sealed class NewsBlurAccountTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "wnr-tests-" + Guid.NewGuid().ToString("N"));
	private readonly FakeNewsBlurServer _server = new();
	private readonly ArticlesStore _store;
	private readonly FeedDownloader _downloader;
	private readonly NewsBlurAccount _account;

	public NewsBlurAccountTests()
	{
		_store = new ArticlesStore(Path.Combine(_directory, "articles.db"));
		_downloader = new FeedDownloader(_server);
		var record = new AccountRecord("nb", AccountKind.NewsBlur, "NewsBlur", NewsBlurCaller.DefaultBaseAddress, FakeNewsBlurServer.Username);
		_account = new NewsBlurAccount(record, _store, NewCaller(), _downloader);
	}

	public void Dispose()
	{
		_account.Dispose();
		_downloader.Dispose();
		_store.Dispose();
		SqliteConnection.ClearAllPools();
		Directory.Delete(_directory, recursive: true);
	}

	private NewsBlurCaller NewCaller(string password = FakeNewsBlurServer.Password) => new(FakeNewsBlurServer.Username, password, _server);

	[Fact]
	public async Task WrongPasswordIsRejectedWithNewsBlursMessage()
	{
		using var caller = NewCaller("nope");

		var error = await Assert.ThrowsAsync<NewsBlurAuthenticationException>(() => caller.LoginAsync());

		Assert.Equal("Wrong username or password.", error.Message);
		await NewCaller().LoginAsync();
	}

	[Fact]
	public async Task FirstSyncBringsFeedsFoldersStoriesAndStatuses()
	{
		var tech = _server.AddFeed("Tech", "https://tech.example/feed", "Tech");
		var news = _server.AddFeed("News", "https://news.example/feed");
		_server.AddStory("a:1", tech, "Unread one", unread: true);
		_server.AddStory("a:2", news, "Starred", starred: true);
		_server.AddStory("a:3", news, "Unread starred", unread: true, starred: true);

		var summary = await _account.RefreshAllAsync();

		Assert.Null(summary.Error);
		Assert.Equal(3, summary.NewArticles);
		Assert.Equal(["Tech"], _store.GetFolders().Select(folder => folder.Name));
		Assert.Contains(_store.GetTopLevelFeeds(), feed => feed.FeedId == news.Id.ToString());
		Assert.Single(_store.GetFeeds(_store.GetFolders().Single().FolderId));
		Assert.Equal(["a:1", "a:3"], _store.StatusArticleIds(StatusKey.Read, false).Order());
		Assert.Equal(["a:2", "a:3"], _store.StatusArticleIds(StatusKey.Starred, true).Order());
	}

	[Fact]
	public async Task OnlyOneLoginIsNeededAcrossManyRequests()
	{
		_server.AddFeed("Feed", "https://a.example/feed");

		await _account.RefreshAllAsync();
		await _account.RefreshAllAsync();

		Assert.Equal(1, _server.LoginCount);
	}

	[Fact]
	public async Task ARefusedSessionIsRenewedOnce()
	{
		_server.AddFeed("Feed", "https://a.example/feed");
		await _account.RefreshAllAsync();

		_server.RefuseNextRequests = 1;
		var summary = await _account.RefreshAllAsync();

		Assert.Null(summary.Error);
		Assert.Equal(2, _server.LoginCount);
	}

	[Fact]
	public async Task MarkingReadAndStarredIsSent()
	{
		var feed = _server.AddFeed("Feed", "https://a.example/feed");
		var one = _server.AddStory("a:1", feed, "One", unread: true);
		var two = _server.AddStory("a:2", feed, "Two", starred: true);
		await _account.RefreshAllAsync();

		_account.SetArticleStatus(["a:1"], StatusKey.Read, true);
		_account.SetArticleStatus(["a:2"], StatusKey.Read, false);
		_account.SetArticleStatus(["a:2"], StatusKey.Starred, false);
		await _account.SendQueuedStatusesAsync();

		Assert.False(one.Unread);
		Assert.True(two.Unread);
		Assert.False(two.Starred);
		Assert.Empty(_store.SelectSyncStatusesForProcessing());
	}

	[Fact]
	public async Task ServerStatusChangesComeDown()
	{
		var feed = _server.AddFeed("Feed", "https://a.example/feed");
		var story = _server.AddStory("a:1", feed, "One", unread: true);
		await _account.RefreshAllAsync();

		story.Unread = false;
		story.Starred = true;
		await _account.RefreshAllAsync();

		Assert.Empty(_store.StatusArticleIds(StatusKey.Read, false));
		Assert.Equal(["a:1"], _store.StatusArticleIds(StatusKey.Starred, true));
	}

	[Fact]
	public async Task FeedsInNoFolderAreDroppedWhenUnsubscribedElsewhere()
	{
		_server.AddFeed("Feed", "https://a.example/feed", "Folder");
		await _account.RefreshAllAsync();

		_server.Feeds.Clear();
		_server.Folders.Clear();
		await _account.RefreshAllAsync();

		Assert.Empty(_store.GetFeeds());
		Assert.Empty(_store.GetFolders());
	}

	[Fact]
	public async Task SubscribingPutsTheFeedInTheFolderAndDownloadsItsStories()
	{
		_server.KnownFeeds["https://new.example/feed"] = "New Feed";
		await _account.RefreshAllAsync();

		var feed = await _account.SubscribeAsync("https://new.example/feed", "Reading");

		Assert.Equal("New Feed", feed.Name);
		Assert.Equal(["Reading"], _store.GetFolders().Select(folder => folder.Name));
		Assert.Single(_store.GetFeeds(_store.GetFolders().Single().FolderId));
	}

	[Fact]
	public async Task SubscribingToSomethingNewsBlurCantFindSaysSo()
	{
		await Assert.ThrowsAsync<SubscribeException>(() => _account.SubscribeAsync("https://nothing.example/feed", null));
	}

	[Fact]
	public async Task RenamingAFeedAndAFolderRenamesThemOnTheServer()
	{
		var feed = _server.AddFeed("Old", "https://a.example/feed", "OldFolder");
		await _account.RefreshAllAsync();

		await _account.RenameFeedAsync(feed.Id.ToString(), "New");
		await _account.RenameFolderAsync(_store.GetFolders().Single().FolderId, "NewFolder");

		Assert.Equal("New", feed.Title);
		Assert.Equal("NewFolder", feed.Folder);
		Assert.Equal(["NewFolder"], _store.GetFolders().Select(folder => folder.Name));
	}

	[Fact]
	public async Task MovingAFeedMovesItOnTheServerAndCopyingIsRefused()
	{
		var feed = _server.AddFeed("Feed", "https://a.example/feed", "From");
		_server.AddFeed("Other", "https://b.example/feed", "To");
		await _account.RefreshAllAsync();
		var from = _store.GetFolders().Single(folder => folder.Name == "From");
		var to = _store.GetFolders().Single(folder => folder.Name == "To");

		await Assert.ThrowsAsync<InvalidOperationException>(() => _account.MoveFeedAsync(feed.Id.ToString(), from.FolderId, to.FolderId, copy: true));
		await _account.MoveFeedAsync(feed.Id.ToString(), from.FolderId, to.FolderId, copy: false);
		await _account.MoveFeedAsync(feed.Id.ToString(), to.FolderId, null, copy: false);

		Assert.Equal(" ", feed.Folder);
		Assert.Contains(_store.GetTopLevelFeeds(), stored => stored.FeedId == feed.Id.ToString());
	}

	[Fact]
	public async Task RemovingAFeedUnsubscribesIt()
	{
		var feed = _server.AddFeed("Feed", "https://a.example/feed");
		await _account.RefreshAllAsync();

		await _account.RemoveFeedAsync(feed.Id.ToString(), null);

		Assert.Empty(_server.Feeds);
		Assert.Null(_store.GetFeed(feed.Id.ToString()));
	}

	[Fact]
	public async Task RemovingAFolderDeletesItAndUnsubscribesItsFeeds()
	{
		_server.AddFeed("In", "https://a.example/feed", "Doomed");
		_server.AddFeed("Kept", "https://b.example/feed");
		await _account.RefreshAllAsync();

		await _account.RemoveFolderAsync(_store.GetFolders().Single().FolderId);

		Assert.Equal(["Kept"], _server.Feeds.Select(feed => feed.Title));
		Assert.Empty(_server.Folders);
		Assert.Empty(_store.GetFolders());
	}

	[Fact]
	public async Task ReconfiguringUsesTheNewPassword()
	{
		_server.AddFeed("Feed", "https://a.example/feed");
		var record = new AccountRecord("nb", AccountKind.NewsBlur, "Renamed", NewsBlurCaller.DefaultBaseAddress, FakeNewsBlurServer.Username);

		await _account.ReconfigureAsync(record, NewCaller());
		var summary = await _account.RefreshAllAsync();

		Assert.Null(summary.Error);
		Assert.Equal("Renamed", _account.Name);
	}
}
