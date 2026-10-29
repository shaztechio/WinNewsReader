using WinNewsReader.Store;

namespace WinNewsReader.Core;

/// <summary>A feed that hasn't published for a while, with the date of its newest article.</summary>
public sealed record Dinosaur(StoredFeed Feed, DateTimeOffset LastUpdate);

/// <summary>NetNewsWire's Dinosaurs window: the feeds not updated in a number of months, six by default.</summary>
public static class Dinosaurs
{
	public const int DefaultMonths = 6;

	/// <summary>The feeds whose newest article is older than <paramref name="months"/> months before <paramref name="now"/>, stalest first.</summary>
	public static IReadOnlyList<Dinosaur> Stale(IEnumerable<StoredFeed> feeds, IReadOnlyDictionary<string, DateTimeOffset> newestArticles, DateTimeOffset now, int months = DefaultMonths)
	{
		var cutoff = now.AddMonths(-months);
		return
		[
			.. feeds
				.Where(feed => newestArticles.TryGetValue(feed.FeedId, out var date) && date < cutoff)
				.Select(feed => new Dinosaur(feed, newestArticles[feed.FeedId]))
				.OrderBy(dinosaur => dinosaur.LastUpdate),
		];
	}
}
