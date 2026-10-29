using Microsoft.Data.Sqlite;
using WinNewsReader.Core;
using WinNewsReader.Store;

namespace WinNewsReader.Sync.GoogleReader.Tests;

/// <summary>The Old Reader and BazQux, which speak the same API as FreshRSS with a few differences NetNewsWire handles.</summary>
public sealed class ReaderApiVariantTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "wnr-tests-" + Guid.NewGuid().ToString("N"));
	private readonly FakeReaderServer _server = new();
	private readonly ArticlesStore _store;
	private readonly FeedDownloader _downloader;
	private readonly ReaderApiAccount _account;

	public ReaderApiVariantTests()
	{
		_store = new ArticlesStore(Path.Combine(_directory, "articles.db"));
		_downloader = new FeedDownloader(_server);
		var record = new AccountRecord("old", AccountKind.TheOldReader, "The Old Reader", FakeReaderServer.Endpoint, FakeReaderServer.Username);
		var caller = new ReaderApiCaller(FakeReaderServer.Endpoint, FakeReaderServer.Username, FakeReaderServer.Password, _server, ReaderApiVariant.TheOldReader);
		_account = new ReaderApiAccount(record, _store, caller, _downloader);
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
	public void EachHostedServiceHasItsOwnFixedAddressAndFreshRssHasNone()
	{
		Assert.Equal("https://theoldreader.com", ReaderApiVariants.Host(ReaderApiVariant.TheOldReader));
		Assert.Equal("https://bazqux.com", ReaderApiVariants.Host(ReaderApiVariant.BazQux));
		Assert.Null(ReaderApiVariants.Host(ReaderApiVariant.FreshRss));
	}

	[Theory]
	[InlineData(AccountKind.FreshRss, ReaderApiVariant.FreshRss)]
	[InlineData(AccountKind.TheOldReader, ReaderApiVariant.TheOldReader)]
	[InlineData(AccountKind.BazQux, ReaderApiVariant.BazQux)]
	public void AnAccountKindMapsToItsVariant(AccountKind kind, ReaderApiVariant variant) => Assert.Equal(variant, ReaderApiVariants.For(kind));

	[Fact]
	public void TheOldReaderUsesItsArticleIdsAsTheyAreWhereFreshRssConvertsHex()
	{
		const string entryId = "tag:google.com,2005:reader/item/5f3a9c7be2d14a08";

		Assert.Equal("5f3a9c7be2d14a08", ReaderApiCaller.ArticleIdFromEntryId(entryId, ReaderApiVariant.TheOldReader));
		Assert.Equal("tag:google.com,2005:reader/item/5f3a9c7be2d14a08", ReaderApiCaller.LongItemId("5f3a9c7be2d14a08", ReaderApiVariant.TheOldReader));
		// FreshRSS and BazQux read the hex as a number and write it back as 16 hex digits.
		Assert.Equal("6861969038152649224", ReaderApiCaller.ArticleIdFromEntryId(entryId));
		Assert.Equal("tag:google.com,2005:reader/item/5f3a9c7be2d14a08", ReaderApiCaller.LongItemId("6861969038152649224", ReaderApiVariant.BazQux));
		Assert.Null(ReaderApiCaller.LongItemId("5f3a9c7be2d14a08"));
	}

	[Fact]
	public async Task TheOldReaderCanSendAnyArticleIdWhereFreshRssNeedsANumber()
	{
		using var oldReader = new ReaderApiCaller(FakeReaderServer.Endpoint, "a", "b", _server, ReaderApiVariant.TheOldReader);
		using var freshRss = new ReaderApiCaller(FakeReaderServer.Endpoint, "a", "b", _server);

		Assert.True(oldReader.CanSend("5f3a9c7be2d14a08"));
		Assert.False(freshRss.CanSend("5f3a9c7be2d14a08"));
		Assert.True(freshRss.CanSend("12345"));
		await Task.CompletedTask;
	}

	[Fact]
	public async Task RemovingAFolderOnTheOldReaderUnsubscribesItsFeedsButDoesNotDeleteTheLabel()
	{
		var feed = _server.AddFeed("Only", "https://a.example/feed", "Gone");
		await _account.RefreshAllAsync();
		var gone = _store.GetFolders().Single(folder => folder.Name == "Gone");
		_server.Requests.Clear();

		await _account.RemoveFolderAsync(gone.FolderId);

		Assert.DoesNotContain(feed, _server.Feeds);
		Assert.DoesNotContain(_server.Requests, request => request.Contains("disable-tag", StringComparison.Ordinal));
		Assert.Empty(_store.GetFolders());
	}
}
