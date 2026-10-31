using Microsoft.Data.Sqlite;
using WinNewsReader.Core;
using WinNewsReader.Store;

namespace WinNewsReader.Sync.Feedbin.Tests;

public sealed class FeedbinAccountTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "wnr-tests-" + Guid.NewGuid().ToString("N"));
	private readonly FakeFeedbinServer _server = new();
	private readonly ArticlesStore _store;
	private readonly FeedDownloader _downloader;
	private readonly FeedbinAccount _account;

	public FeedbinAccountTests()
	{
		_store = new ArticlesStore(Path.Combine(_directory, "articles.db"));
		_downloader = new FeedDownloader(_server);
		var record = new AccountRecord("fb", AccountKind.Feedbin, "Feedbin", FeedbinCaller.DefaultBaseAddress, FakeFeedbinServer.Email);
		_account = new FeedbinAccount(record, _store, NewCaller(), _downloader);
	}

	public void Dispose()
	{
		_account.Dispose();
		_downloader.Dispose();
		_store.Dispose();
		SqliteConnection.ClearAllPools();
		Directory.Delete(_directory, recursive: true);
	}

	private FeedbinCaller NewCaller(string password = FakeFeedbinServer.Password) => new(FakeFeedbinServer.Email, password, _server);

	[Fact]
	public async Task WrongPasswordIsRejected()
	{
		using var caller = NewCaller("nope");

		await Assert.ThrowsAsync<FeedbinAuthenticationException>(() => caller.ValidateCredentialsAsync());
		await NewCaller().ValidateCredentialsAsync();
	}

	[Fact]
	public async Task FirstSyncBringsFeedsFoldersArticlesAndStatuses()
	{
		var tech = _server.AddSubscription("Tech", "https://tech.example/feed", "Tech", "Favorites");
		var news = _server.AddSubscription("News", "https://news.example/feed");
		_server.AddEntry(1, tech, "Unread one", unread: true);
		_server.AddEntry(2, tech, "Read");
		_server.AddEntry(3, news, "Starred", starred: true);

		var summary = await _account.RefreshAllAsync();

		Assert.Null(summary.Error);
		Assert.Equal(3, summary.NewArticles);
		Assert.Equal(["Favorites", "Tech"], _store.GetFolders().Select(folder => folder.Name).Order());
		var techFeed = _store.GetFeed(tech.FeedId.ToString())!;
		Assert.Equal("Tech", techFeed.Name);
		Assert.Equal(2, _store.GetFolders().Count(folder => _store.GetFeeds(folder.FolderId).Any(feed => feed.FeedId == techFeed.FeedId)));
		Assert.Contains(_store.GetTopLevelFeeds(), feed => feed.FeedId == news.FeedId.ToString());
		Assert.Equal(["1"], _store.StatusArticleIds(StatusKey.Read, false));
		Assert.Equal(["3"], _store.StatusArticleIds(StatusKey.Starred, true));
	}

	[Fact]
	public async Task EveryPageOfNewArticlesIsFetched()
	{
		var feed = _server.AddSubscription("Feed", "https://a.example/feed");
		for (var i = 1; i <= 5; i++)
		{
			_server.AddEntry(i, feed, $"Article {i}");
		}
		_server.PageSize = 2;

		var summary = await _account.RefreshAllAsync();

		Assert.Equal(5, summary.NewArticles);
		Assert.Contains(_server.Requests, request => request.Contains("page=3", StringComparison.Ordinal));
	}

	[Fact]
	public async Task MarkingReadAndStarredIsSentAndNotResentOnTheNextSync()
	{
		var feed = _server.AddSubscription("Feed", "https://a.example/feed");
		var one = _server.AddEntry(1, feed, "One", unread: true);
		var two = _server.AddEntry(2, feed, "Two");
		await _account.RefreshAllAsync();

		_account.SetArticleStatus(["1"], StatusKey.Read, true);
		_account.SetArticleStatus(["2"], StatusKey.Read, false);
		_account.SetArticleStatus(["2"], StatusKey.Starred, true);
		await _account.SendQueuedStatusesAsync();

		Assert.False(one.Unread);
		Assert.True(two.Unread);
		Assert.True(two.Starred);
		Assert.Empty(_store.SelectSyncStatusesForProcessing());
	}

	[Fact]
	public async Task AFailedSendLeavesTheChangeQueuedAndKeepsLocalState()
	{
		var feed = _server.AddSubscription("Feed", "https://a.example/feed");
		_server.AddEntry(1, feed, "One", unread: true);
		await _account.RefreshAllAsync();
		_server.FailStatusChanges = true;

		_account.SetArticleStatus(["1"], StatusKey.Read, true);
		await _account.SendQueuedStatusesAsync();
		await _account.RefreshAllAsync();

		Assert.Empty(_store.StatusArticleIds(StatusKey.Read, false));
		Assert.Single(_store.SelectSyncStatusesForProcessing());
	}

	[Fact]
	public async Task ServerStatusChangesComeDown()
	{
		var feed = _server.AddSubscription("Feed", "https://a.example/feed");
		var entry = _server.AddEntry(1, feed, "One", unread: true);
		await _account.RefreshAllAsync();

		entry.Unread = false;
		entry.Starred = true;
		await _account.RefreshAllAsync();

		Assert.Empty(_store.StatusArticleIds(StatusKey.Read, false));
		Assert.Equal(["1"], _store.StatusArticleIds(StatusKey.Starred, true));
	}

	[Fact]
	public async Task UnsubscribingElsewhereRemovesTheFeedHere()
	{
		var feed = _server.AddSubscription("Feed", "https://a.example/feed", "Folder");
		await _account.RefreshAllAsync();

		_server.Subscriptions.Clear();
		_server.Taggings.Clear();
		await _account.RefreshAllAsync();

		Assert.Empty(_store.GetFeeds());
		Assert.Empty(_store.GetFolders());
		Assert.Null(_store.GetFeed(feed.FeedId.ToString()));
	}

	[Fact]
	public async Task SubscribingTagsTheFeedAndDownloadsItsArticles()
	{
		_server.KnownFeeds["https://new.example/feed"] = "New Feed";
		await _account.RefreshAllAsync();

		var feed = await _account.SubscribeAsync("https://new.example/feed", "Reading");

		Assert.Equal("New Feed", feed.Name);
		Assert.Contains(_server.Taggings, tagging => tagging.Name == "Reading");
		Assert.Equal(["Reading"], _store.GetFolders().Select(folder => folder.Name));
		Assert.Single(_store.GetFeeds(_store.GetFolders().Single().FolderId));
	}

	[Fact]
	public async Task SubscribingToAFeedYouAlreadyHaveSaysSo()
	{
		_server.AddSubscription("Feed", "https://a.example/feed");

		var error = await Assert.ThrowsAsync<SubscribeException>(() => _account.SubscribeAsync("https://a.example/feed", null));

		Assert.Contains("already subscribed", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SubscribingToSomethingFeedbinCantFindSaysSo()
	{
		await Assert.ThrowsAsync<SubscribeException>(() => _account.SubscribeAsync("https://nothing.example/feed", null));
	}

	[Fact]
	public async Task WhenTheAddressHasSeveralFeedsTheOneFeedbinOffersIsUsed()
	{
		_server.Choices["https://site.example/rss"] = "https://site.example/feed.xml";
		_server.KnownFeeds["https://site.example/feed.xml"] = "Site Feed";

		var feed = await _account.SubscribeAsync("https://site.example/rss", null);

		Assert.Equal("Site Feed", feed.Name);
	}

	[Fact]
	public async Task RenamingAFeedRenamesTheSubscription()
	{
		var sub = _server.AddSubscription("Old", "https://a.example/feed");
		await _account.RefreshAllAsync();

		await _account.RenameFeedAsync(sub.FeedId.ToString(), "New");

		Assert.Equal("New", sub.Title);
		Assert.Equal("New", _store.GetFeed(sub.FeedId.ToString())!.EditedName);
	}

	[Fact]
	public async Task RenamingAFolderRenamesTheTag()
	{
		_server.AddSubscription("Feed", "https://a.example/feed", "Old");
		await _account.RefreshAllAsync();

		await _account.RenameFolderAsync(_store.GetFolders().Single().FolderId, "New");

		Assert.Equal(["New"], _server.Taggings.Select(tagging => tagging.Name));
		Assert.Equal(["New"], _store.GetFolders().Select(folder => folder.Name));
	}

	[Fact]
	public async Task MovingAFeedChangesItsTag()
	{
		var sub = _server.AddSubscription("Feed", "https://a.example/feed", "From");
		_server.AddSubscription("Other", "https://b.example/feed", "To");
		await _account.RefreshAllAsync();
		var folders = _store.GetFolders();
		var from = folders.Single(folder => folder.Name == "From");
		var to = folders.Single(folder => folder.Name == "To");

		await _account.MoveFeedAsync(sub.FeedId.ToString(), from.FolderId, to.FolderId, copy: false);

		Assert.Equal(["To"], _server.Taggings.Where(tagging => tagging.FeedId == sub.FeedId).Select(tagging => tagging.Name));
		Assert.Contains(_store.GetFeeds(to.FolderId), feed => feed.FeedId == sub.FeedId.ToString());
	}

	[Fact]
	public async Task CopyingAFeedKeepsTheOldTagAndCopyingToTheTopLevelIsRefused()
	{
		var sub = _server.AddSubscription("Feed", "https://a.example/feed", "From");
		_server.AddSubscription("Other", "https://b.example/feed", "To");
		await _account.RefreshAllAsync();
		var folders = _store.GetFolders();
		var from = folders.Single(folder => folder.Name == "From");
		var to = folders.Single(folder => folder.Name == "To");

		await _account.MoveFeedAsync(sub.FeedId.ToString(), from.FolderId, to.FolderId, copy: true);
		await Assert.ThrowsAsync<InvalidOperationException>(() => _account.MoveFeedAsync(sub.FeedId.ToString(), from.FolderId, null, copy: true));

		Assert.Equal(["From", "To"], _server.Taggings.Where(tagging => tagging.FeedId == sub.FeedId).Select(tagging => tagging.Name).Order());
	}

	[Fact]
	public async Task RemovingAFeedWithTwoTagsOnlyUntagsItButOneTagUnsubscribes()
	{
		var two = _server.AddSubscription("Two", "https://a.example/feed", "A", "B");
		var one = _server.AddSubscription("One", "https://b.example/feed", "A");
		await _account.RefreshAllAsync();
		var a = _store.GetFolders().Single(folder => folder.Name == "A");

		await _account.RemoveFeedAsync(two.FeedId.ToString(), a.FolderId);
		await _account.RemoveFeedAsync(one.FeedId.ToString(), a.FolderId);

		Assert.Contains(_server.Subscriptions, sub => sub.FeedId == two.FeedId);
		Assert.Equal(["B"], _server.Taggings.Where(tagging => tagging.FeedId == two.FeedId).Select(tagging => tagging.Name));
		Assert.DoesNotContain(_server.Subscriptions, sub => sub.FeedId == one.FeedId);
		Assert.Null(_store.GetFeed(one.FeedId.ToString()));
	}

	[Fact]
	public async Task RemovingAFolderUntagsSharedFeedsAndUnsubscribesTheRest()
	{
		var shared = _server.AddSubscription("Shared", "https://a.example/feed", "Doomed", "Kept");
		var only = _server.AddSubscription("Only", "https://b.example/feed", "Doomed");
		await _account.RefreshAllAsync();

		await _account.RemoveFolderAsync(_store.GetFolders().Single(folder => folder.Name == "Doomed").FolderId);

		Assert.Equal(["Kept"], _server.Taggings.Where(tagging => tagging.FeedId == shared.FeedId).Select(tagging => tagging.Name));
		Assert.DoesNotContain(_server.Subscriptions, sub => sub.FeedId == only.FeedId);
		Assert.DoesNotContain(_store.GetFolders(), folder => folder.Name == "Doomed");
	}

	[Fact]
	public async Task ReconfiguringUsesTheNewPassword()
	{
		_server.AddSubscription("Feed", "https://a.example/feed");
		var record = new AccountRecord("fb", AccountKind.Feedbin, "Renamed", FeedbinCaller.DefaultBaseAddress, FakeFeedbinServer.Email);

		await _account.ReconfigureAsync(record, NewCaller());
		var summary = await _account.RefreshAllAsync();

		Assert.Null(summary.Error);
		Assert.Equal("Renamed", _account.Name);
	}

	[Fact]
	public void FeedbinDatesAreFormattedWithSixFractionalDigits() =>
		Assert.Equal("2024-03-05T06:07:08.000000Z", FeedbinCaller.FormatDate(new DateTimeOffset(2024, 3, 5, 6, 7, 8, TimeSpan.Zero)));

	[Theory]
	[InlineData("https://evil.example/v2/entries.json?page=2")]
	[InlineData("http://api.feedbin.com/v2/entries.json?page=2")]
	public async Task PaginationUrlOutsideFeedbinBaseAddressIsRefused(string untrustedUrl)
	{
		using var caller = NewCaller();

		await Assert.ThrowsAsync<FeedbinException>(() => caller.GetEntryPageAsync(untrustedUrl));
		Assert.Empty(_server.Requests);
	}
}
