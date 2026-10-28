using WinNewsReader.Parser.Feeds;
using WinNewsReader.Parser.Opml;
using WinNewsReader.Store;

namespace WinNewsReader.Core;

public sealed record FeedRefreshResult(string FeedId, DownloadStatus Status, int NewArticles, string? Error);

public sealed class SubscribeException(string message) : Exception(message);

public sealed record OpmlImportResult(int FeedsAdded, int FoldersAdded);

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
	/// subscribes at the top level or in the folder named <paramref name="folderName"/>, creating it if needed,
	/// and stores its articles. Throws <see cref="SubscribeException"/> if no feed is found or it can't be fetched.
	/// </summary>
	public async Task<(StoredFeed Feed, int NewArticles)> SubscribeAsync(string userUrl, string? folderName = null, CancellationToken cancellationToken = default)
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
		var folder = folderName is null ? null : Store.EnsureFolder(folderName);
		var feed = Store.AddFeed(url, parsed.Title, parsed.HomePageUrl, folder?.FolderId);
		var newArticles = Save(feed.FeedId, parsed, download);
		return (Store.GetFeed(feed.FeedId)!, newArticles);
	}

	/// <summary>Adds every feed and folder in an OPML file without fetching. Feeds already subscribed are also put in the file's folders.</summary>
	public OpmlImportResult ImportOpml(string path)
	{
		var document = OpmlParser.Parse(new ParserData(new Uri(Path.GetFullPath(path)).AbsoluteUri, File.ReadAllBytes(path)));
		var feedsBefore = Store.GetFeeds().Count;
		var foldersBefore = Store.GetFolders().Count;
		LoadOpmlItems(document.Children ?? [], null);
		return new OpmlImportResult(Store.GetFeeds().Count - feedsBefore, Store.GetFolders().Count - foldersBefore);
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

	/// <summary>
	/// NetNewsWire's <c>Account.loadOPMLItems</c>. Folders are one level deep, so a nested folder becomes a
	/// top-level folder of its own, and the items of a folder with no title go one level up.
	/// </summary>
	private void LoadOpmlItems(IEnumerable<OpmlItem> items, StoredFolder? parentFolder)
	{
		foreach (var item in items)
		{
			if (item.FeedSpecifier is { } specifier)
			{
				Store.AddFeed(specifier.FeedUrl, specifier.Title, specifier.HomePageUrl, parentFolder?.FolderId);
			}
			else if (item.TitleFromAttributes is { } folderName)
			{
				LoadOpmlItems(item.Children ?? [], Store.EnsureFolder(folderName));
			}
			else
			{
				LoadOpmlItems(item.Children ?? [], parentFolder);
			}
		}
	}
}
