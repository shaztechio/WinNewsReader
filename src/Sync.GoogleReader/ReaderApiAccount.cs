using System.Globalization;
using WinNewsReader.Core;
using WinNewsReader.Parser.Feeds;
using WinNewsReader.Store;

namespace WinNewsReader.Sync.GoogleReader;

/// <summary>
/// A FreshRSS account (or another Google Reader API server), ported from NetNewsWire's <c>ReaderAPIAccountDelegate</c>.
/// The server is the source of truth for feeds, folders and statuses; local status changes are queued and sent.
/// </summary>
public sealed class ReaderApiAccount : IAccount, IDisposable
{
	/// <summary>How far back the first sync, and a new subscription, fetch article IDs: NetNewsWire's three months.</summary>
	private static readonly TimeSpan InitialFetchWindow = TimeSpan.FromDays(91);

	/// <summary>Unread or starred articles newer than this get downloaded.</summary>
	private static readonly TimeSpan ArticleCutoff = TimeSpan.FromDays(91);

	/// <summary>Local status changes wait this long for more before being sent, so a run of marking sends once.</summary>
	private static readonly TimeSpan StatusSendDelay = TimeSpan.FromSeconds(3);

	private const int EntriesPerRequest = 150;
	private const int StatusesPerRequest = 1000;
	private const string LastArticleFetchKey = "lastArticleFetchStartTime";

	private readonly AccountRecord _record;
	private readonly ReaderApiCaller _caller;
	private readonly FeedFinder _finder;
	private readonly SemaphoreSlim _syncGate = new(1, 1);
	private readonly Lock _sendLock = new();
	private CancellationTokenSource? _pendingSend;

	public ReaderApiAccount(AccountRecord record, ArticlesStore store, ReaderApiCaller caller, FeedDownloader downloader)
	{
		_record = record;
		Store = store;
		_caller = caller;
		_finder = new FeedFinder(downloader);
		// A send cut short when the app last closed left its statuses selected; queue them again.
		store.ResetAllSelectedSyncStatuses();
	}

	public string AccountId => _record.AccountId;

	public AccountKind Kind => _record.Kind;

	public string Name => _record.Name;

	public ArticlesStore Store { get; }

	/// <summary>
	/// NetNewsWire's <c>refreshAll</c>: the feed list, then queued statuses up, then every recent article ID (marked read
	/// until the unread list says otherwise), then statuses down, then any unread or starred article not yet downloaded.
	/// </summary>
	public async Task<RefreshSummary> RefreshAllAsync(CancellationToken cancellationToken = default)
	{
		await _syncGate.WaitAsync(cancellationToken);
		try
		{
			await RefreshFeedListAsync(cancellationToken);
			try
			{
				await SendStatusesAsync(cancellationToken);
			}
			catch (ReaderApiException)
			{
				// A failed send mustn't block fetching new articles; the statuses stay queued.
			}

			var since = Store.GetMetadata(LastArticleFetchKey) is { } saved && long.TryParse(saved, CultureInfo.InvariantCulture, out var seconds)
				? DateTimeOffset.FromUnixTimeSeconds(seconds)
				: DateTimeOffset.UtcNow - InitialFetchWindow;
			var (articleIds, serverDate) = await _caller.GetItemIdsAsync(ReaderApiItemIdType.AllForAccount, since, cancellationToken: cancellationToken);
			Store.SetStatuses(articleIds, StatusKey.Read, true);
			await RefreshStatusesAsync(cancellationToken);
			if (serverDate is { } date)
			{
				Store.SetMetadata(LastArticleFetchKey, date.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
			}
			return new RefreshSummary(await DownloadMissingArticlesAsync(cancellationToken), 0);
		}
		catch (ReaderApiException ex)
		{
			return new RefreshSummary(0, 0, $"{Name}: {ex.Message}");
		}
		finally
		{
			_syncGate.Release();
		}
	}

	/// <summary>Finds the feed, as NetNewsWire does skipping JSON feeds, subscribes on the server and downloads its articles.</summary>
	public async Task<StoredFeed> SubscribeAsync(string url, string? folderName, CancellationToken cancellationToken = default)
	{
		var feedUrl = await _finder.FindBestFeedUrlAsync(url, specifier => !specifier.Url.Contains("json", StringComparison.OrdinalIgnoreCase), cancellationToken);
		await _syncGate.WaitAsync(cancellationToken);
		try
		{
			var feedId = await _caller.QuickAddAsync(feedUrl, cancellationToken) ?? throw new SubscribeException($"{Name} couldn’t subscribe to {feedUrl}.");
			if (folderName is not null)
			{
				await _caller.EditSubscriptionFoldersAsync(feedId, folderName, null, cancellationToken);
			}
			await RefreshFeedListAsync(cancellationToken);
			var (articleIds, _) = await _caller.GetItemIdsAsync(ReaderApiItemIdType.AllForFeed, DateTimeOffset.UtcNow - InitialFetchWindow, feedId, cancellationToken);
			Store.SetStatuses(articleIds, StatusKey.Read, true);
			await RefreshStatusesAsync(cancellationToken);
			await DownloadMissingArticlesAsync(cancellationToken);
			return Store.GetFeed(feedId) ?? throw new SubscribeException($"{Name} subscribed to {feedUrl}, but it isn’t in the feed list.");
		}
		catch (ReaderApiException ex)
		{
			throw new SubscribeException($"{Name}: {ex.Message}");
		}
		finally
		{
			_syncGate.Release();
		}
	}

	/// <summary>As in NetNewsWire, removing a sync feed unsubscribes it everywhere.</summary>
	public async Task RemoveFeedAsync(string feedId, long? folderId, CancellationToken cancellationToken = default)
	{
		await _caller.UnsubscribeAsync(feedId, cancellationToken);
		Store.RemoveFeed(feedId);
	}

	public async Task RenameFolderAsync(long folderId, string name, CancellationToken cancellationToken = default)
	{
		var folder = Store.GetFolders().Single(folder => folder.FolderId == folderId);
		await _caller.RenameFolderAsync(folder.Name, name, cancellationToken);
		Store.RenameFolder(folderId, name);
	}

	/// <summary>
	/// NetNewsWire's <c>removeFolder</c>: feeds also in another folder just leave this one; the rest are unsubscribed.
	/// Then the folder's label is deleted on the server.
	/// </summary>
	public async Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken = default)
	{
		var folders = Store.GetFolders();
		var folder = folders.Single(folder => folder.FolderId == folderId);
		var membershipCounts = folders.SelectMany(f => Store.GetFeeds(f.FolderId)).GroupBy(feed => feed.FeedId).ToDictionary(group => group.Key, group => group.Count());
		foreach (var feed in Store.GetFeeds(folderId))
		{
			if (membershipCounts.GetValueOrDefault(feed.FeedId) > 1)
			{
				await _caller.EditSubscriptionFoldersAsync(feed.FeedId, null, folder.Name, cancellationToken);
			}
			else
			{
				await _caller.UnsubscribeAsync(feed.FeedId, cancellationToken);
			}
		}
		await _caller.DeleteFolderAsync(folder.Name, cancellationToken);
		Store.RemoveFolder(folderId);
	}

	/// <summary>Changes the statuses here, and queues the changes to send shortly.</summary>
	public void SetArticleStatus(IReadOnlyCollection<string> articleIds, StatusKey key, bool flag)
	{
		var changed = Store.SetStatuses(articleIds, key, flag);
		if (changed.Count == 0)
		{
			return;
		}
		Store.QueueSyncStatuses(changed, key, flag);
		ScheduleStatusSend();
	}

	/// <summary>Sends queued statuses now, outside a refresh. Errors leave them queued for the next try.</summary>
	public async Task SendQueuedStatusesAsync(CancellationToken cancellationToken = default)
	{
		await _syncGate.WaitAsync(cancellationToken);
		try
		{
			await SendStatusesAsync(cancellationToken);
		}
		catch (ReaderApiException)
		{
			// Queued statuses stay queued until the next send or refresh.
		}
		finally
		{
			_syncGate.Release();
		}
	}

	public void Dispose()
	{
		lock (_sendLock)
		{
			_pendingSend?.Cancel();
		}
		_caller.Dispose();
	}

	/// <summary>
	/// Makes the store match the server's feed list. Folders are the server's <c>user/-/label/…</c> tags, by name; a feed
	/// with no label is at the top level. NetNewsWire leaves folders alone when the server has no labels at all.
	/// </summary>
	internal void ApplyFeedList(IReadOnlyList<ReaderApiTag> tags, IReadOnlyList<ReaderApiSubscription> subscriptions)
	{
		static IEnumerable<string> Labels(ReaderApiSubscription subscription) =>
			(subscription.Categories ?? []).Select(category => new ReaderApiTag(category.Id, null).FolderName).OfType<string>();

		var folderNames = tags.Select(tag => tag.FolderName).OfType<string>().Concat(subscriptions.SelectMany(Labels)).ToHashSet();
		var folderIds = folderNames.ToDictionary(name => name, name => Store.EnsureFolder(name).FolderId);

		foreach (var subscription in subscriptions)
		{
			Store.SyncFeed(subscription.FeedId, subscription.Url, DecodeFullwidth(subscription.Title), subscription.HomePageUrl);
			var labels = Labels(subscription).ToList();
			Store.SetFeedPlacement(subscription.FeedId, topLevel: labels.Count == 0, labels.Select(label => folderIds[label]));
		}

		var subscribed = subscriptions.Select(subscription => subscription.FeedId).ToHashSet();
		foreach (var feed in Store.GetFeeds().Where(feed => !subscribed.Contains(feed.FeedId)))
		{
			Store.RemoveFeed(feed.FeedId);
		}
		if (folderNames.Count > 0)
		{
			foreach (var folder in Store.GetFolders().Where(folder => !folderNames.Contains(folder.Name)))
			{
				Store.RemoveFolder(folder.FolderId);
			}
		}
	}

	/// <summary>FreshRSS sends &amp;, &lt; and &gt; in titles as fullwidth characters; NetNewsWire maps them back.</summary>
	internal static string? DecodeFullwidth(string? text) =>
		text?.Replace('＆', '&').Replace('＜', '<').Replace('＞', '>');

	internal static ParsedItem ToParsedItem(ReaderApiEntry entry, string feedId)
	{
		var articleId = ReaderApiCaller.ArticleIdFromEntryId(entry.Id);
		return new ParsedItem
		{
			SyncServiceId = articleId,
			UniqueId = articleId,
			FeedUrl = feedId,
			ExternalUrl = entry.Alternates?.FirstOrDefault()?.Href,
			Title = DecodeFullwidth(entry.Title),
			ContentHtml = entry.Summary?.Content,
			Summary = entry.Summary?.Content,
			ImageUrl = entry.Enclosures?.FirstOrDefault(enclosure => enclosure.Type?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)?.Href,
			DatePublished = entry.Published is { } published ? DateTimeOffset.FromUnixTimeMilliseconds((long)(published * 1000)) : null,
			Authors = entry.Author is { Length: > 0 } author ? new HashSet<ParsedAuthor> { new(DecodeFullwidth(author), null, null, null) } : null,
		};
	}

	private async Task RefreshFeedListAsync(CancellationToken cancellationToken)
	{
		var tags = await _caller.GetTagsAsync(cancellationToken);
		var subscriptions = await _caller.GetSubscriptionsAsync(cancellationToken);
		ApplyFeedList(tags, subscriptions);
	}

	/// <summary>
	/// NetNewsWire's <c>sendArticleStatus</c>. Sent statuses leave the queue; failed ones go back, and the first error is
	/// thrown after trying every group. IDs the server can't take are dropped rather than retried forever.
	/// </summary>
	private async Task SendStatusesAsync(CancellationToken cancellationToken)
	{
		var statuses = Store.SelectSyncStatusesForProcessing();
		ReaderApiException? firstError = null;
		foreach (var group in statuses.GroupBy(status => (status.Key, status.Flag)))
		{
			var (key, flag) = group.Key;
			var ids = group.Select(status => status.ArticleId).ToList();
			var unsendable = ids.Where(id => ReaderApiCaller.LongItemId(id) is null).ToList();
			Store.DeleteSelectedSyncStatuses(unsendable, key);
			foreach (var chunk in ids.Except(unsendable).Chunk(StatusesPerRequest))
			{
				try
				{
					// Read is a state that gets added; unread is that state removed.
					await _caller.SetStateAsync(chunk, starred: key == StatusKey.Starred, add: flag, cancellationToken);
					Store.DeleteSelectedSyncStatuses(chunk, key);
				}
				catch (ReaderApiException ex)
				{
					firstError ??= ex;
					Store.ResetSelectedSyncStatuses(chunk, key);
				}
			}
		}
		if (firstError is not null)
		{
			throw firstError;
		}
	}

	/// <summary>
	/// NetNewsWire's <c>refreshArticleStatus</c>: local read and starred statuses follow the server's lists, except for
	/// articles with a change still queued here — the queued change is the truth.
	/// </summary>
	private async Task RefreshStatusesAsync(CancellationToken cancellationToken)
	{
		var (unreadIds, _) = await _caller.GetItemIdsAsync(ReaderApiItemIdType.Unread, cancellationToken: cancellationToken);
		var serverUnread = unreadIds.ToHashSet();
		var pendingRead = Store.PendingSyncArticleIds(StatusKey.Read);
		var localUnread = Store.StatusArticleIds(StatusKey.Read, false);
		Store.SetStatuses([.. serverUnread.Except(localUnread).Except(pendingRead)], StatusKey.Read, false);
		Store.SetStatuses([.. localUnread.Except(serverUnread).Except(pendingRead)], StatusKey.Read, true);

		var (starredIds, _) = await _caller.GetItemIdsAsync(ReaderApiItemIdType.Starred, cancellationToken: cancellationToken);
		var serverStarred = starredIds.ToHashSet();
		var pendingStarred = Store.PendingSyncArticleIds(StatusKey.Starred);
		var localStarred = Store.StatusArticleIds(StatusKey.Starred, true);
		Store.SetStatuses([.. serverStarred.Except(localStarred).Except(pendingStarred)], StatusKey.Starred, true);
		Store.SetStatuses([.. localStarred.Except(serverStarred).Except(pendingStarred)], StatusKey.Starred, false);
	}

	/// <summary>NetNewsWire's <c>refreshMissingArticles</c>. Returns how many articles were new.</summary>
	private async Task<int> DownloadMissingArticlesAsync(CancellationToken cancellationToken)
	{
		var newArticles = 0;
		// An article from a feed that's no longer subscribed would be stranded, belonging to no feed.
		var feedIds = Store.GetFeeds().Select(feed => feed.FeedId).ToHashSet();
		foreach (var chunk in Store.ArticleIdsMissingContent(DateTimeOffset.UtcNow - ArticleCutoff).Chunk(EntriesPerRequest))
		{
			var entries = await _caller.GetEntriesAsync(chunk, cancellationToken);
			foreach (var feedEntries in entries.Where(entry => entry.Origin?.StreamId is { } feedId && feedIds.Contains(feedId)).GroupBy(entry => entry.Origin!.StreamId!))
			{
				newArticles += Store.UpsertArticles(feedEntries.Key, feedEntries.Select(entry => ToParsedItem(entry, feedEntries.Key)), defaultRead: true);
			}
		}
		return newArticles;
	}

	private void ScheduleStatusSend()
	{
		CancellationToken token;
		lock (_sendLock)
		{
			_pendingSend?.Cancel();
			_pendingSend = new CancellationTokenSource();
			token = _pendingSend.Token;
		}
		_ = Task.Run(async () =>
		{
			try
			{
				await Task.Delay(StatusSendDelay, token);
				await SendQueuedStatusesAsync(token);
			}
			catch (OperationCanceledException)
			{
				// A newer change rescheduled the send, or the account closed.
			}
		}, CancellationToken.None);
	}
}
