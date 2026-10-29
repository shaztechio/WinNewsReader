using Microsoft.Data.Sqlite;
using WinNewsReader.Core;
using WinNewsReader.Parser.Feeds;

namespace WinNewsReader.Store.Tests;

/// <summary>What the Activity Log, Error Log, Current Activity, Account Stats and Dinosaurs windows show.</summary>
public sealed class UtilityWindowTests : IDisposable
{
	private const string Fresh = "https://fresh.example/feed";
	private const string Stale = "https://stale.example/feed";
	private const string Empty = "https://empty.example/feed";

	private readonly string _directory = Path.Combine(Path.GetTempPath(), "wnr-tests-" + Guid.NewGuid().ToString("N"));
	private readonly ArticlesStore _store;

	public UtilityWindowTests()
	{
		_store = new ArticlesStore(Path.Combine(_directory, "articles.db"));
		_store.AddFeed(Fresh, "Fresh");
		_store.AddFeed(Stale, "Stale");
		_store.AddFeed(Empty, "Empty");
		_store.EnsureFolder("News");
		_store.UpsertArticles(Fresh, [Item(Fresh, "f1", DateTimeOffset.UtcNow.AddDays(-2)), Item(Fresh, "f2", DateTimeOffset.UtcNow.AddDays(-30))]);
		_store.UpsertArticles(Stale, [Item(Stale, "s1", DateTimeOffset.UtcNow.AddMonths(-9)), Item(Stale, "s2", DateTimeOffset.UtcNow.AddMonths(-12))]);
	}

	public void Dispose()
	{
		_store.Dispose();
		SqliteConnection.ClearAllPools();
		Directory.Delete(_directory, recursive: true);
	}

	[Fact]
	public void StatisticsCountWhatTheAccountHolds()
	{
		var id = _store.GetUnreadArticles(Fresh).First().ArticleId;
		_store.SetStatuses([id], StatusKey.Read, true);
		_store.SetStatuses([id], StatusKey.Starred, true);

		var stats = _store.Statistics();

		Assert.Equal(new StoreStatistics(Feeds: 3, Folders: 1, Articles: 4, Unread: 3, Starred: 1), stats);
	}

	[Fact]
	public void DinosaursAreFeedsWithoutRecentArticles()
	{
		var dinosaurs = Dinosaurs.Stale(_store.GetFeeds(), _store.NewestArticleDates(), DateTimeOffset.UtcNow);

		// The feed with no articles at all counts from the day it was added, which is today.
		Assert.Equal([Stale], dinosaurs.Select(dinosaur => dinosaur.Feed.FeedId));
		Assert.True(dinosaurs[0].LastUpdate < DateTimeOffset.UtcNow.AddMonths(-8));
	}

	[Fact]
	public void DinosaursUseTheNumberOfMonthsGiven()
	{
		var now = DateTimeOffset.UtcNow;

		Assert.Equal([Stale], Dinosaurs.Stale(_store.GetFeeds(), _store.NewestArticleDates(), now, months: 6).Select(d => d.Feed.FeedId));
		Assert.Empty(Dinosaurs.Stale(_store.GetFeeds(), _store.NewestArticleDates(), now, months: 10));
		Assert.Equal([Stale, Fresh], Dinosaurs.Stale(_store.GetFeeds(), _store.NewestArticleDates(), now.AddMonths(12), months: 6).Select(d => d.Feed.FeedId).Take(2));
	}

	[Fact]
	public void ActivityLogKeepsEntriesAndSplitsOutErrors()
	{
		var log = new ActivityLog();
		var changes = 0;
		log.Changed += (_, _) => changes++;

		log.Add(ActivityLevel.Info, "On This PC", "Fresh", "2 new articles");
		log.Add(ActivityLevel.Error, "On This PC", "Stale", "Not a feed");

		Assert.Equal(["2 new articles", "Not a feed"], log.Entries().Select(entry => entry.Message));
		Assert.Equal(["Not a feed"], log.Errors().Select(entry => entry.Message));
		Assert.Equal(2, changes);
	}

	[Fact]
	public void ActivityLogShowsDownloadsUnderWay()
	{
		var log = new ActivityLog();

		var token = log.Started("On This PC", "Fresh");
		Assert.Equal(["Fresh"], log.InProgress().Select(item => item.Feed));
		log.Finished(token);

		Assert.Empty(log.InProgress());
	}

	[Fact]
	public void ActivityLogDropsTheOldestPastItsLimit()
	{
		var log = new ActivityLog(limit: 2);
		log.Add(ActivityLevel.Info, "a", null, "one");
		log.Add(ActivityLevel.Info, "a", null, "two");
		log.Add(ActivityLevel.Info, "a", null, "three");

		Assert.Equal(["two", "three"], log.Entries().Select(entry => entry.Message));
	}

	private static ParsedItem Item(string feed, string uniqueId, DateTimeOffset published) => new()
	{
		FeedUrl = feed,
		UniqueId = uniqueId,
		Title = uniqueId,
		DatePublished = published,
	};
}
