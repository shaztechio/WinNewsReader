using WinNewsReader.Parser.Feeds;
using WinNewsReader.Parser.Opml;
using WinNewsReader.Store;

namespace WinNewsReader.Core;

public sealed record FeedRefreshResult(string FeedId, DownloadStatus Status, int NewArticles, string? Error);

public sealed class SubscribeException(string message) : Exception(message);

/// <summary>
/// The "On This PC" account: subscriptions and articles live only in the local store, and refreshing
/// fetches each feed directly.
/// </summary>
public sealed class LocalAccount(ArticlesStore store, FeedDownloader downloader)
{
	/// <summary>Feeds refreshed at once across all hosts. Per-host limits apply on top.</summary>
	public const int MaxConcurrentRefreshes = 8;

	private readonly FeedFinder _feedFinder = new(downloader);

	public ArticlesStore Store { get; } = store;

	/// <summary>
	/// Finds the best feed for <paramref name="userUrl"/>, which may be a feed or a web page, then
	/// subscribes and stores its articles. Throws <see cref="SubscribeException"/> if no feed is found
	/// or it can't be fetched.
	/// </summary>
	public async Task<(StoredFeed Feed, int NewArticles)> SubscribeAsync(string userUrl, CancellationToken cancellationToken = default)
	{
		var normalizedUrl = userUrl.NormalizedUrl();
		IReadOnlySet<FeedSpecifier> feedSpecifiers;
		try
		{
			feedSpecifiers = await _feedFinder.FindAsync(normalizedUrl, cancellationToken);
		}
		catch (FeedNotFoundException)
		{
			throw new SubscribeException($"Couldn’t find a feed at {normalizedUrl}.");
		}
		var url = FeedSpecifier.BestFeed([.. feedSpecifiers])?.Url ?? throw new SubscribeException($"Couldn’t find a feed at {normalizedUrl}.");

		var download = await downloader.DownloadAsync(url, null, null, cancellationToken);
		if (download.Status != DownloadStatus.Ok)
		{
			throw new SubscribeException($"Couldn't fetch {url}: {download.Error}");
		}
		var parsed = Parse(url, download.Data!) ?? throw new SubscribeException($"{url} isn't a feed.");
		var feed = Store.AddFeed(url, parsed.Title, parsed.HomePageUrl);
		var newArticles = Save(feed.FeedId, parsed, download);
		return (Store.GetFeed(feed.FeedId)!, newArticles);
	}

	/// <summary>Adds every feed in an OPML file without fetching. Returns the number added.</summary>
	public int ImportOpml(string path)
	{
		var document = OpmlParser.Parse(new ParserData(new Uri(Path.GetFullPath(path)).AbsoluteUri, File.ReadAllBytes(path)));
		var existing = Store.GetFeeds().Select(feed => feed.FeedId).ToHashSet();
		var added = 0;
		foreach (var specifier in Flatten(document))
		{
			if (existing.Add(specifier.FeedUrl))
			{
				Store.AddFeed(specifier.FeedUrl, specifier.Title, specifier.HomePageUrl);
				added++;
			}
		}
		return added;
	}

	public async Task<IReadOnlyList<FeedRefreshResult>> RefreshAllAsync(CancellationToken cancellationToken = default)
	{
		using var gate = new SemaphoreSlim(MaxConcurrentRefreshes);
		var tasks = Store.GetFeeds().Select(async feed =>
		{
			await gate.WaitAsync(cancellationToken);
			try
			{
				return await RefreshAsync(feed, cancellationToken);
			}
			finally
			{
				gate.Release();
			}
		});
		return await Task.WhenAll(tasks);
	}

	public async Task<FeedRefreshResult> RefreshAsync(StoredFeed feed, CancellationToken cancellationToken = default)
	{
		var download = await downloader.DownloadAsync(feed.Url, feed.ETag, feed.LastModified, cancellationToken);
		if (download.Status != DownloadStatus.Ok)
		{
			return new FeedRefreshResult(feed.FeedId, download.Status, 0, download.Error);
		}
		if (Parse(feed.Url, download.Data!) is not { } parsed)
		{
			return new FeedRefreshResult(feed.FeedId, DownloadStatus.Failed, 0, "Not a feed");
		}
		return new FeedRefreshResult(feed.FeedId, DownloadStatus.Ok, Save(feed.FeedId, parsed, download), null);
	}

	private int Save(string feedId, ParsedFeed parsed, DownloadResult download)
	{
		Store.UpdateFeedMetadata(feedId, parsed.Title, parsed.HomePageUrl, parsed.IconUrl, download.ETag, download.LastModified);
		return Store.UpsertArticles(feedId, parsed.Items);
	}

	private static ParsedFeed? Parse(string url, byte[] data)
	{
		try
		{
			return FeedParser.Parse(new ParserData(url, data));
		}
		catch (FeedParserException)
		{
			return null;
		}
	}

	private static IEnumerable<OpmlFeedSpecifier> Flatten(OpmlItem item)
	{
		if (item.FeedSpecifier is { } specifier)
		{
			yield return specifier;
		}
		foreach (var child in item.Children ?? [])
		{
			foreach (var descendant in Flatten(child))
			{
				yield return descendant;
			}
		}
	}
}
