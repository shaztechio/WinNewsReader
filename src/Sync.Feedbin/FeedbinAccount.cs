using System.Globalization;
using WinNewsReader.Core;
using WinNewsReader.Parser.Feeds;
using WinNewsReader.Store;

namespace WinNewsReader.Sync.Feedbin;

/// <summary>
/// A Feedbin account, ported from NetNewsWire's <c>FeedbinAccountDelegate</c>. Feedbin is the source of truth for
/// subscriptions, tags (folders) and which articles are unread or starred; local status changes are queued and sent.
/// A feed's ID here is Feedbin's feed ID, and folders are Feedbin's tags, which can overlap.
/// </summary>
public sealed class FeedbinAccount : ISyncAccount, IDisposable
{
	/// <summary>How far back the first sync fetches articles, as NetNewsWire does.</summary>
	private static readonly TimeSpan InitialFetchWindow = TimeSpan.FromDays(91);

	private static readonly TimeSpan ArticleCutoff = TimeSpan.FromDays(91);

	/// <summary>Local status changes wait this long for more before being sent, so a run of marking sends once.</summary>
	private static readonly TimeSpan StatusSendDelay = TimeSpan.FromSeconds(3);

	private const int EntriesPerRequest = 100;
	private const int StatusesPerRequest = 1000;
	private const string LastArticleFetchKey = "lastArticleFetchStartTime";

	private AccountRecord _record;
	private FeedbinCaller _caller;
	private readonly FeedFinder _finder;
	private readonly SemaphoreSlim _syncGate = new(1, 1);
	private readonly Lock _sendLock = new();
	private CancellationTokenSource? _pendingSend;
	private DateTimeOffset? _lastBackdate;

	// What Feedbin calls things, beyond the IDs the store knows: needed to rename, unsubscribe and untag.
	private readonly Dictionary<string, long> _subscriptionIds = [];
	private readonly Dictionary<(string FeedId, string Folder), long> _taggingIds = [];

	public FeedbinAccount(AccountRecord record, ArticlesStore store, FeedbinCaller caller, FeedDownloader downloader)
	{
		_record = record;
		Store = store;
		_caller = caller;
		_finder = new FeedFinder(downloader);
		// A send cut short when the app last closed left its statuses selected; queue them again.
		store.ResetAllSelectedSyncStatuses();
	}

	public string AccountId => _record.AccountId;

	public AccountKind Kind => AccountKind.Feedbin;

	public string Name => _record.Name;

	public AccountRecord Record => _record;

	public ArticlesStore Store { get; }

	/// <summary>Feedbin feeds are untagged, which is the top level, or tagged with any number of folders.</summary>
	public bool FeedsCanBeAtTopLevel => true;

	public bool FeedsCanBeInSeveralFolders => true;

	public Task ReconfigureAsync(AccountRecord record, string password, CancellationToken cancellationToken = default) =>
		ReconfigureAsync(record, new FeedbinCaller(record.Username, password), cancellationToken);

	public async Task ReconfigureAsync(AccountRecord record, FeedbinCaller caller, CancellationToken cancellationToken = default)
	{
		if (record.AccountId != _record.AccountId)
		{
			throw new ArgumentException("An account keeps its ID.", nameof(record));
		}
		await _syncGate.WaitAsync(cancellationToken);
		try
		{
			var old = _caller;
			_record = record;
			_caller = caller;
			old.Dispose();
		}
		finally
		{
			_syncGate.Release();
		}
	}

	/// <summary>
	/// NetNewsWire's <c>moveFeed</c> and copy: tags the feed with the new folder and, for a move, takes the old tag off.
	/// A feed can't be copied to the top level, as it isn't a place a tag can put it twice.
	/// </summary>
	public async Task MoveFeedAsync(string feedId, long? fromFolderId, long? toFolderId, bool copy, CancellationToken cancellationToken = default)
	{
		if (copy && toFolderId is null)
		{
			throw new InvalidOperationException($"{Name} can't copy a feed to the top level.");
		}
		if (fromFolderId == toFolderId && !copy)
		{
			return;
		}
		var folders = Store.GetFolders();
		var feedNumber = FeedNumber(feedId);
		// Tag first: if that fails, the feed still has its old tag.
		if (toFolderId is { } to)
		{
			var toName = folders.Single(folder => folder.FolderId == to).Name;
			_taggingIds[(feedId, toName)] = await _caller.CreateTaggingAsync(feedNumber, toName, cancellationToken);
		}
		if (!copy && fromFolderId is { } from)
		{
			await DeleteTaggingAsync(feedId, folders.Single(folder => folder.FolderId == from).Name, cancellationToken);
		}
		Store.MoveFeed(feedId, fromFolderId, toFolderId, copy);
	}

	/// <summary>
	/// NetNewsWire's <c>refreshAll</c>: the feed list, then queued statuses up, then read and starred statuses down,
	/// then new articles since the last refresh, then any unread or starred article that's still missing.
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
			catch (FeedbinException)
			{
				// A failed send mustn't block fetching new articles; the statuses stay queued.
			}
			await RefreshStatusesAsync(cancellationToken);
			var fetched = await RefreshArticlesAsync(cancellationToken);
			return new RefreshSummary(fetched + await DownloadMissingArticlesAsync(cancellationToken), 0);
		}
		catch (FeedbinException ex)
		{
			return new RefreshSummary(0, 0, $"{Name}: {ex.Message}");
		}
		finally
		{
			_syncGate.Release();
		}
	}

	public Task<IReadOnlyList<FeedSpecifier>> FindFeedsAsync(string url, CancellationToken cancellationToken = default) =>
		_finder.FindCandidatesAsync(url, _ => true, cancellationToken);

	/// <summary>Asks Feedbin to subscribe to what was typed, as NetNewsWire does, then tags it and downloads its articles.</summary>
	public async Task<StoredFeed> SubscribeAsync(string url, string? folderName, CancellationToken cancellationToken = default)
	{
		var normalizedUrl = url.NormalizedUrl();
		await _syncGate.WaitAsync(cancellationToken);
		try
		{
			var result = await _caller.CreateSubscriptionAsync(normalizedUrl, cancellationToken);
			if (result is FeedbinCreateResult.Choices choices)
			{
				var chosen = choices.Feeds.FirstOrDefault(choice => choice.Url == normalizedUrl) ?? choices.Feeds.FirstOrDefault()
					?? throw new SubscribeException($"Couldn’t find a feed at {normalizedUrl}.");
				result = await _caller.CreateSubscriptionAsync(chosen.Url, cancellationToken);
			}
			var subscription = result switch
			{
				FeedbinCreateResult.Created created => created.Subscription,
				FeedbinCreateResult.AlreadySubscribed => throw new SubscribeException($"You’re already subscribed to {normalizedUrl}."),
				_ => throw new SubscribeException($"Couldn’t find a feed at {normalizedUrl}."),
			};
			var feedId = subscription.FeedId.ToString(CultureInfo.InvariantCulture);
			_subscriptionIds[feedId] = subscription.SubscriptionId;
			Store.SyncFeed(feedId, subscription.Url, subscription.Title, subscription.HomePageUrl);
			if (folderName is not null)
			{
				var folder = Store.EnsureFolder(folderName);
				_taggingIds[(feedId, folderName)] = await _caller.CreateTaggingAsync(subscription.FeedId, folderName, cancellationToken);
				Store.SetFeedPlacement(feedId, topLevel: false, [folder.FolderId]);
			}
			else
			{
				Store.SetFeedPlacement(feedId, topLevel: true, []);
			}
			var page = await _caller.GetEntriesSinceAsync(DateTimeOffset.UtcNow - InitialFetchWindow, subscription.FeedId, cancellationToken);
			await ProcessPagesAsync(page, cancellationToken);
			await RefreshStatusesAsync(cancellationToken);
			await DownloadMissingArticlesAsync(cancellationToken);
			return Store.GetFeed(feedId) ?? throw new SubscribeException($"{Name} subscribed to {normalizedUrl}, but it isn’t in the feed list.");
		}
		catch (FeedbinException ex)
		{
			throw new SubscribeException($"{Name}: {ex.Message}");
		}
		finally
		{
			_syncGate.Release();
		}
	}

	/// <summary>
	/// NetNewsWire's <c>removeFeed</c>: a feed with several tags just loses this folder's; otherwise it's unsubscribed
	/// everywhere.
	/// </summary>
	public async Task RemoveFeedAsync(string feedId, long? folderId, CancellationToken cancellationToken = default)
	{
		var tagCount = _taggingIds.Keys.Count(key => key.FeedId == feedId);
		if (folderId is { } id && tagCount > 1)
		{
			var folder = Store.GetFolders().Single(folder => folder.FolderId == id);
			await DeleteTaggingAsync(feedId, folder.Name, cancellationToken);
			Store.RemoveFeed(feedId, id);
			return;
		}
		await UnsubscribeAsync(feedId, cancellationToken);
		Store.RemoveFeed(feedId);
	}

	public async Task RenameFeedAsync(string feedId, string name, CancellationToken cancellationToken = default)
	{
		await _caller.RenameSubscriptionAsync(await SubscriptionIdAsync(feedId, cancellationToken), name, cancellationToken);
		Store.SetFeedEditedName(feedId, name);
	}

	/// <summary>NetNewsWire's <c>renameFolder</c>: Feedbin renames the tag. A folder with no feeds exists only here.</summary>
	public async Task RenameFolderAsync(long folderId, string name, CancellationToken cancellationToken = default)
	{
		var folder = Store.GetFolders().Single(folder => folder.FolderId == folderId);
		if (Store.GetFeeds(folderId).Count > 0)
		{
			await _caller.RenameTagAsync(folder.Name, name, cancellationToken);
			foreach (var key in _taggingIds.Keys.Where(key => key.Folder == folder.Name).ToList())
			{
				_taggingIds[(key.FeedId, name)] = _taggingIds[key];
				_taggingIds.Remove(key);
			}
		}
		Store.RenameFolder(folderId, name);
	}

	/// <summary>NetNewsWire's <c>removeFolder</c>: feeds also in another folder lose this tag; the rest are unsubscribed.</summary>
	public async Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken = default)
	{
		var folders = Store.GetFolders();
		var folder = folders.Single(folder => folder.FolderId == folderId);
		foreach (var feed in Store.GetFeeds(folderId))
		{
			if (_taggingIds.Keys.Count(key => key.FeedId == feed.FeedId) > 1)
			{
				await DeleteTaggingAsync(feed.FeedId, folder.Name, cancellationToken);
			}
			else
			{
				await UnsubscribeAsync(feed.FeedId, cancellationToken);
			}
		}
		Store.RemoveFolder(folderId);
	}

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
		catch (FeedbinException)
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
	/// Makes the store match Feedbin's tags, subscriptions and taggings. A feed with no tagging is at the top level;
	/// folders Feedbin has no tag for are removed, their feeds moving to the top level first.
	/// </summary>
	internal void ApplyFeedList(IReadOnlyList<FeedbinTag> tags, IReadOnlyList<FeedbinSubscription> subscriptions, IReadOnlyList<FeedbinTagging> taggings)
	{
		var folderNames = tags.Select(tag => tag.Name).Concat(taggings.Select(tagging => tagging.Name)).ToHashSet();
		var folderIds = folderNames.ToDictionary(name => name, name => Store.EnsureFolder(name).FolderId);

		_subscriptionIds.Clear();
		_taggingIds.Clear();
		var taggingsByFeed = taggings.GroupBy(tagging => tagging.FeedId).ToDictionary(group => group.Key, group => group.ToList());
		foreach (var subscription in subscriptions)
		{
			var feedId = subscription.FeedId.ToString(CultureInfo.InvariantCulture);
			_subscriptionIds[feedId] = subscription.SubscriptionId;
			Store.SyncFeed(feedId, subscription.Url, subscription.Title, subscription.HomePageUrl);
			var feedTaggings = taggingsByFeed.GetValueOrDefault(subscription.FeedId) ?? [];
			foreach (var tagging in feedTaggings)
			{
				_taggingIds[(feedId, tagging.Name)] = tagging.Id;
			}
			Store.SetFeedPlacement(feedId, topLevel: feedTaggings.Count == 0, feedTaggings.Select(tagging => folderIds[tagging.Name]).Distinct());
		}

		var subscribed = subscriptions.Select(subscription => subscription.FeedId.ToString(CultureInfo.InvariantCulture)).ToHashSet();
		foreach (var feed in Store.GetFeeds().Where(feed => !subscribed.Contains(feed.FeedId)))
		{
			Store.RemoveFeed(feed.FeedId);
		}
		foreach (var folder in Store.GetFolders().Where(folder => !folderNames.Contains(folder.Name)))
		{
			Store.RemoveFolder(folder.FolderId);
		}
	}

	internal static ParsedItem ToParsedItem(FeedbinEntry entry)
	{
		var id = entry.Id.ToString(CultureInfo.InvariantCulture);
		var jsonAuthor = entry.JsonFeed?.Author;
		return new ParsedItem
		{
			SyncServiceId = id,
			UniqueId = id,
			FeedUrl = entry.FeedId.ToString(CultureInfo.InvariantCulture),
			Url = entry.Url,
			ExternalUrl = entry.JsonFeed?.ExternalUrl,
			Title = entry.Title,
			ContentHtml = entry.Content,
			Summary = entry.Summary,
			ImageUrl = entry.Images?.OriginalUrl,
			DatePublished = entry.PublishedDate,
			Authors = entry.Author is not null || jsonAuthor is not null
				? new HashSet<ParsedAuthor> { new(entry.Author, jsonAuthor?.Url, jsonAuthor?.Avatar, null) }
				: null,
		};
	}

	private static long FeedNumber(string feedId) =>
		long.TryParse(feedId, CultureInfo.InvariantCulture, out var number) ? number : throw new InvalidOperationException($"“{feedId}” isn't a Feedbin feed.");

	private async Task<long> SubscriptionIdAsync(string feedId, CancellationToken cancellationToken)
	{
		if (!_subscriptionIds.ContainsKey(feedId))
		{
			await RefreshFeedListAsync(cancellationToken);
		}
		return _subscriptionIds.TryGetValue(feedId, out var id) ? id : throw new FeedbinException("Feedbin doesn’t have that subscription any more.");
	}

	private async Task DeleteTaggingAsync(string feedId, string folderName, CancellationToken cancellationToken)
	{
		if (!_taggingIds.ContainsKey((feedId, folderName)))
		{
			await RefreshFeedListAsync(cancellationToken);
		}
		if (_taggingIds.TryGetValue((feedId, folderName), out var taggingId))
		{
			await _caller.DeleteTaggingAsync(taggingId, cancellationToken);
			_taggingIds.Remove((feedId, folderName));
		}
	}

	private async Task UnsubscribeAsync(string feedId, CancellationToken cancellationToken)
	{
		await _caller.DeleteSubscriptionAsync(await SubscriptionIdAsync(feedId, cancellationToken), cancellationToken);
		_subscriptionIds.Remove(feedId);
		foreach (var key in _taggingIds.Keys.Where(key => key.FeedId == feedId).ToList())
		{
			_taggingIds.Remove(key);
		}
	}

	private async Task RefreshFeedListAsync(CancellationToken cancellationToken)
	{
		var tags = await _caller.GetTagsAsync(cancellationToken);
		var subscriptions = await _caller.GetSubscriptionsAsync(cancellationToken);
		var taggings = await _caller.GetTaggingsAsync(cancellationToken);
		ApplyFeedList(tags, subscriptions, taggings);
	}

	/// <summary>
	/// NetNewsWire's <c>refreshArticles</c>: the pages of entries since the last fetch, going back a day once a day to
	/// catch articles that were updated rather than new. Returns how many articles were new.
	/// </summary>
	private async Task<int> RefreshArticlesAsync(CancellationToken cancellationToken)
	{
		DateTimeOffset since;
		if (Store.GetMetadata(LastArticleFetchKey) is { } saved && long.TryParse(saved, CultureInfo.InvariantCulture, out var seconds))
		{
			var lastFetch = DateTimeOffset.FromUnixTimeSeconds(seconds);
			if (_lastBackdate is null || _lastBackdate.Value.AddDays(1) < lastFetch)
			{
				_lastBackdate = lastFetch;
				since = lastFetch.AddDays(-1);
			}
			else
			{
				since = lastFetch;
			}
		}
		else
		{
			since = DateTimeOffset.UtcNow - InitialFetchWindow;
		}
		var first = await _caller.GetEntriesSinceAsync(since, cancellationToken: cancellationToken);
		var count = await ProcessPagesAsync(first, cancellationToken);
		if (first.ServerDate is { } serverDate)
		{
			Store.SetMetadata(LastArticleFetchKey, serverDate.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
		}
		return count;
	}

	/// <summary>Saves a page of entries and every page after it.</summary>
	private async Task<int> ProcessPagesAsync(FeedbinEntryPage page, CancellationToken cancellationToken)
	{
		var count = 0;
		while (true)
		{
			count += ProcessEntries(page.Entries);
			if (page.NextPage is not { } next)
			{
				return count;
			}
			page = await _caller.GetEntryPageAsync(next, cancellationToken);
		}
	}

	private int ProcessEntries(IEnumerable<FeedbinEntry> entries)
	{
		var feedIds = Store.GetFeeds().Select(feed => feed.FeedId).ToHashSet();
		var added = 0;
		foreach (var group in entries.GroupBy(entry => entry.FeedId.ToString(CultureInfo.InvariantCulture)).Where(group => feedIds.Contains(group.Key)))
		{
			added += Store.UpsertArticles(group.Key, group.Select(ToParsedItem), defaultRead: true);
		}
		return added;
	}

	/// <summary>
	/// NetNewsWire's <c>sendArticleStatus</c>: unread, read, starred and unstarred each go in batches of 1000. Sent
	/// statuses leave the queue; failed ones go back, and the first error is thrown after trying every group.
	/// </summary>
	private async Task SendStatusesAsync(CancellationToken cancellationToken)
	{
		var statuses = Store.SelectSyncStatusesForProcessing();
		FeedbinException? firstError = null;
		foreach (var group in statuses.GroupBy(status => (status.Key, status.Flag)))
		{
			var (key, flag) = group.Key;
			var all = group.Select(status => status.ArticleId).ToList();
			var unsendable = all.Where(id => !long.TryParse(id, CultureInfo.InvariantCulture, out _)).ToList();
			Store.DeleteSelectedSyncStatuses(unsendable, key);
			foreach (var chunk in all.Except(unsendable).Chunk(StatusesPerRequest))
			{
				var ids = chunk.Select(id => long.Parse(id, CultureInfo.InvariantCulture)).ToList();
				try
				{
					// Feedbin lists the unread, so marking read removes from that list and marking unread adds.
					await ((key, flag) switch
					{
						(StatusKey.Read, false) => _caller.AddUnreadAsync(ids, cancellationToken),
						(StatusKey.Read, true) => _caller.RemoveUnreadAsync(ids, cancellationToken),
						(StatusKey.Starred, true) => _caller.AddStarredAsync(ids, cancellationToken),
						_ => _caller.RemoveStarredAsync(ids, cancellationToken),
					});
					Store.DeleteSelectedSyncStatuses(chunk, key);
				}
				catch (FeedbinException ex)
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
	/// NetNewsWire's <c>refreshArticleStatus</c>: local read and starred statuses follow Feedbin's lists, except for
	/// articles with a change still queued here — the queued change is the truth.
	/// </summary>
	private async Task RefreshStatusesAsync(CancellationToken cancellationToken)
	{
		// Starred first: an article known only as starred starts out read, and the unread list below says otherwise.
		var serverStarred = (await _caller.GetStarredEntryIdsAsync(cancellationToken)).Select(id => id.ToString(CultureInfo.InvariantCulture)).ToHashSet();
		var pendingStarred = Store.PendingSyncArticleIds(StatusKey.Starred);
		var localStarred = Store.StatusArticleIds(StatusKey.Starred, true);
		Store.SetStatuses([.. serverStarred.Except(localStarred).Except(pendingStarred)], StatusKey.Starred, true);
		Store.SetStatuses([.. localStarred.Except(serverStarred).Except(pendingStarred)], StatusKey.Starred, false);

		var serverUnread = (await _caller.GetUnreadEntryIdsAsync(cancellationToken)).Select(id => id.ToString(CultureInfo.InvariantCulture)).ToHashSet();
		var pendingRead = Store.PendingSyncArticleIds(StatusKey.Read);
		var localUnread = Store.StatusArticleIds(StatusKey.Read, false);
		Store.SetStatuses([.. serverUnread.Except(localUnread).Except(pendingRead)], StatusKey.Read, false);
		Store.SetStatuses([.. localUnread.Except(serverUnread).Except(pendingRead)], StatusKey.Read, true);
	}

	/// <summary>NetNewsWire's <c>refreshMissingArticles</c>: downloads unread or starred articles known only by ID.</summary>
	private async Task<int> DownloadMissingArticlesAsync(CancellationToken cancellationToken)
	{
		var added = 0;
		var missing = Store.ArticleIdsMissingContent(DateTimeOffset.UtcNow - ArticleCutoff)
			.Select(id => long.TryParse(id, CultureInfo.InvariantCulture, out var number) ? (long?)number : null).OfType<long>();
		foreach (var chunk in missing.Chunk(EntriesPerRequest))
		{
			added += ProcessEntries(await _caller.GetEntriesAsync(chunk, cancellationToken));
		}
		return added;
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
