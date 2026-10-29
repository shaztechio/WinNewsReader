using System.Globalization;
using WinNewsReader.Core;
using WinNewsReader.Parser.Feeds;
using WinNewsReader.Store;

namespace WinNewsReader.Sync.NewsBlur;

/// <summary>
/// A NewsBlur account, ported from NetNewsWire's <c>NewsBlurAccountDelegate</c>. NewsBlur is the source of truth for
/// feeds, folders and which stories are unread or starred; local status changes are queued and sent. A story's ID is
/// NewsBlur's story hash, and a feed's is its feed ID.
/// </summary>
public sealed class NewsBlurAccount : ISyncAccount, IDisposable
{
	private static readonly TimeSpan ArticleCutoff = TimeSpan.FromDays(91);

	/// <summary>Local status changes wait this long for more before being sent, so a run of marking sends once.</summary>
	private static readonly TimeSpan StatusSendDelay = TimeSpan.FromSeconds(3);

	/// <summary>NewsBlur takes 100 story hashes at a time to read, and to mark read.</summary>
	private const int StoriesPerRequest = 100;

	private AccountRecord _record;
	private NewsBlurCaller _caller;
	private readonly FeedFinder _finder;
	private readonly SemaphoreSlim _syncGate = new(1, 1);
	private readonly Lock _sendLock = new();
	private CancellationTokenSource? _pendingSend;

	public NewsBlurAccount(AccountRecord record, ArticlesStore store, NewsBlurCaller caller, FeedDownloader downloader)
	{
		_record = record;
		Store = store;
		_caller = caller;
		_finder = new FeedFinder(downloader);
		// A send cut short when the app last closed left its statuses selected; queue them again.
		store.ResetAllSelectedSyncStatuses();
	}

	public string AccountId => _record.AccountId;

	public AccountKind Kind => AccountKind.NewsBlur;

	public string Name => _record.Name;

	public AccountRecord Record => _record;

	public ArticlesStore Store { get; }

	/// <summary>NewsBlur has a top level, which it calls the folder " ", but a feed is in one place at a time.</summary>
	public bool FeedsCanBeAtTopLevel => true;

	public bool FeedsCanBeInSeveralFolders => false;

	public Task ReconfigureAsync(AccountRecord record, string password, CancellationToken cancellationToken = default) =>
		ReconfigureAsync(record, new NewsBlurCaller(record.Username, password), cancellationToken);

	public async Task ReconfigureAsync(AccountRecord record, NewsBlurCaller caller, CancellationToken cancellationToken = default)
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

	/// <summary>NetNewsWire's <c>moveFeed</c>: one call that takes the feed out of the old folder and puts it in the new.</summary>
	public async Task MoveFeedAsync(string feedId, long? fromFolderId, long? toFolderId, bool copy, CancellationToken cancellationToken = default)
	{
		if (copy)
		{
			throw new InvalidOperationException($"{Name} keeps every feed in exactly one place.");
		}
		if (fromFolderId == toFolderId)
		{
			return;
		}
		var folders = Store.GetFolders();
		var from = fromFolderId is null ? null : folders.Single(folder => folder.FolderId == fromFolderId).Name;
		var to = toFolderId is null ? null : folders.Single(folder => folder.FolderId == toFolderId).Name;
		if (to is not null)
		{
			await EnsureFolderExistsAsync(to, cancellationToken);
		}
		await _caller.MoveFeedAsync(FeedNumber(feedId), from, to, cancellationToken);
		Store.MoveFeed(feedId, fromFolderId, toFolderId);
	}

	/// <summary>
	/// NetNewsWire's <c>refreshAll</c>: the feed list, then queued statuses up, then read and starred statuses down,
	/// then any unread or starred story not downloaded yet.
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
			catch (NewsBlurException)
			{
				// A failed send mustn't block fetching new stories; the statuses stay queued.
			}
			await RefreshStatusesAsync(cancellationToken);
			return new RefreshSummary(await DownloadMissingStoriesAsync(cancellationToken), 0);
		}
		catch (NewsBlurException ex)
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

	/// <summary>Asks NewsBlur to subscribe to what was typed, in the folder if given, then downloads the feed's stories.</summary>
	public async Task<StoredFeed> SubscribeAsync(string url, string? folderName, CancellationToken cancellationToken = default)
	{
		var normalizedUrl = url.NormalizedUrl();
		await _syncGate.WaitAsync(cancellationToken);
		try
		{
			if (folderName is not null)
			{
				await EnsureFolderExistsAsync(folderName, cancellationToken);
			}
			var feed = await _caller.AddUrlAsync(normalizedUrl, folderName, cancellationToken)
				?? throw new SubscribeException($"Couldn’t find a feed at {normalizedUrl}.");
			await RefreshFeedListAsync(cancellationToken);
			await RefreshStatusesAsync(cancellationToken);
			await DownloadMissingStoriesAsync(cancellationToken);
			return Store.GetFeed(feed.Id.ToString(CultureInfo.InvariantCulture)) ?? throw new SubscribeException($"{Name} subscribed to {normalizedUrl}, but it isn’t in the feed list.");
		}
		catch (NewsBlurException ex)
		{
			throw new SubscribeException($"{Name}: {ex.Message}");
		}
		finally
		{
			_syncGate.Release();
		}
	}

	/// <summary>Unsubscribes the feed from the folder it's in, or from the top level.</summary>
	public async Task RemoveFeedAsync(string feedId, long? folderId, CancellationToken cancellationToken = default)
	{
		var folder = folderId is null ? null : Store.GetFolders().Single(folder => folder.FolderId == folderId).Name;
		await _caller.DeleteFeedAsync(FeedNumber(feedId), folder, cancellationToken);
		Store.RemoveFeed(feedId);
	}

	public async Task RenameFeedAsync(string feedId, string name, CancellationToken cancellationToken = default)
	{
		await _caller.RenameFeedAsync(FeedNumber(feedId), name, cancellationToken);
		Store.SetFeedEditedName(feedId, name);
	}

	public async Task RenameFolderAsync(long folderId, string name, CancellationToken cancellationToken = default)
	{
		var folder = Store.GetFolders().Single(folder => folder.FolderId == folderId);
		await _caller.RenameFolderAsync(folder.Name, name, cancellationToken);
		Store.RenameFolder(folderId, name);
	}

	/// <summary>NetNewsWire's <c>removeFolder</c>: NewsBlur deletes the folder and unsubscribes the feeds in it.</summary>
	public async Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken = default)
	{
		var folder = Store.GetFolders().Single(folder => folder.FolderId == folderId);
		var feeds = Store.GetFeeds(folderId);
		await _caller.RemoveFolderAsync(folder.Name, feeds.Select(feed => FeedNumber(feed.FeedId)), cancellationToken);
		foreach (var feed in feeds)
		{
			Store.RemoveFeed(feed.FeedId);
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
		catch (NewsBlurException)
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
	/// Makes the store match NewsBlur's feeds and folders. The folder " " is the top level. Folders NewsBlur doesn't have
	/// are removed, their feeds moving to the top level first.
	/// </summary>
	internal void ApplyFeedList(IReadOnlyList<NewsBlurFeed> feeds, IReadOnlyList<NewsBlurFolder> folders)
	{
		var named = folders.Where(folder => folder.Name != NewsBlurCaller.TopLevelFolder).ToList();
		var folderIds = named.ToDictionary(folder => folder.Name, folder => Store.EnsureFolder(folder.Name).FolderId);

		foreach (var feed in feeds)
		{
			var feedId = feed.Id.ToString(CultureInfo.InvariantCulture);
			Store.SyncFeed(feedId, feed.Url, feed.Name, feed.HomePageUrl);
			var inFolders = named.Where(folder => folder.FeedIds.Contains(feed.Id)).Select(folder => folderIds[folder.Name]).ToList();
			Store.SetFeedPlacement(feedId, topLevel: inFolders.Count == 0, inFolders);
		}

		var subscribed = feeds.Select(feed => feed.Id.ToString(CultureInfo.InvariantCulture)).ToHashSet();
		foreach (var feed in Store.GetFeeds().Where(feed => !subscribed.Contains(feed.FeedId)))
		{
			Store.RemoveFeed(feed.FeedId);
		}
		foreach (var folder in Store.GetFolders().Where(folder => !folderIds.ContainsKey(folder.Name)))
		{
			Store.RemoveFolder(folder.FolderId);
		}
	}

	internal static ParsedItem ToParsedItem(NewsBlurStory story) => new()
	{
		SyncServiceId = story.Hash,
		UniqueId = story.Hash,
		FeedUrl = story.FeedId.ToString(CultureInfo.InvariantCulture),
		Url = story.Url,
		Title = story.Title,
		ContentHtml = story.Content,
		ImageUrl = story.ImageUrl,
		DatePublished = story.Published,
		Authors = story.Author is { Length: > 0 } author ? new HashSet<ParsedAuthor> { new(author, null, null, null) } : null,
		Tags = story.Tags.Count > 0 ? new HashSet<string>(story.Tags) : null,
	};

	private static long FeedNumber(string feedId) =>
		long.TryParse(feedId, CultureInfo.InvariantCulture, out var number) ? number : throw new InvalidOperationException($"“{feedId}” isn't a NewsBlur feed.");

	/// <summary>NewsBlur only knows a folder it has been told about; saying so again is harmless.</summary>
	private async Task EnsureFolderExistsAsync(string name, CancellationToken cancellationToken)
	{
		try
		{
			await _caller.AddFolderAsync(name, cancellationToken);
		}
		catch (NewsBlurException) when (!cancellationToken.IsCancellationRequested)
		{
			// Already there, or the move below will say what's wrong.
		}
	}

	private async Task RefreshFeedListAsync(CancellationToken cancellationToken)
	{
		var (feeds, folders) = await _caller.GetFeedsAsync(cancellationToken);
		ApplyFeedList(feeds, folders);
	}

	/// <summary>
	/// NetNewsWire's <c>sendArticleStatus</c>: unread, read, starred and unstarred each go in groups. Sent statuses leave
	/// the queue; failed ones go back, and the first error is thrown after trying every group.
	/// </summary>
	private async Task SendStatusesAsync(CancellationToken cancellationToken)
	{
		var statuses = Store.SelectSyncStatusesForProcessing();
		NewsBlurException? firstError = null;
		foreach (var group in statuses.GroupBy(status => (status.Key, status.Flag)))
		{
			var (key, flag) = group.Key;
			// NetNewsWire sends marking unread, starring and unstarring one at a time; only marking read takes a group.
			var size = (key, flag) == (StatusKey.Read, true) ? StoriesPerRequest : 1;
			foreach (var chunk in group.Select(status => status.ArticleId).Chunk(size))
			{
				try
				{
					await ((key, flag) switch
					{
						(StatusKey.Read, false) => _caller.MarkAsUnreadAsync(chunk, cancellationToken),
						(StatusKey.Read, true) => _caller.MarkAsReadAsync(chunk, cancellationToken),
						(StatusKey.Starred, true) => _caller.StarAsync(chunk, cancellationToken),
						_ => _caller.UnstarAsync(chunk, cancellationToken),
					});
					Store.DeleteSelectedSyncStatuses(chunk, key);
				}
				catch (NewsBlurException ex)
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
	/// NetNewsWire's <c>refreshArticleStatus</c>: local read and starred statuses follow NewsBlur's lists, except for
	/// stories with a change still queued here — the queued change is the truth.
	/// </summary>
	private async Task RefreshStatusesAsync(CancellationToken cancellationToken)
	{
		// Starred first: a story known only as starred starts out read, and the unread list below says otherwise.
		var serverStarred = (await _caller.GetStarredStoryHashesAsync(cancellationToken)).ToHashSet();
		var pendingStarred = Store.PendingSyncArticleIds(StatusKey.Starred);
		var localStarred = Store.StatusArticleIds(StatusKey.Starred, true);
		Store.SetStatuses([.. serverStarred.Except(localStarred).Except(pendingStarred)], StatusKey.Starred, true);
		Store.SetStatuses([.. localStarred.Except(serverStarred).Except(pendingStarred)], StatusKey.Starred, false);

		var serverUnread = (await _caller.GetUnreadStoryHashesAsync(cancellationToken)).ToHashSet();
		var pendingRead = Store.PendingSyncArticleIds(StatusKey.Read);
		var localUnread = Store.StatusArticleIds(StatusKey.Read, false);
		Store.SetStatuses([.. serverUnread.Except(localUnread).Except(pendingRead)], StatusKey.Read, false);
		Store.SetStatuses([.. localUnread.Except(serverUnread).Except(pendingRead)], StatusKey.Read, true);
	}

	/// <summary>NetNewsWire's <c>refreshMissingStories</c>: downloads unread or starred stories known only by hash.</summary>
	private async Task<int> DownloadMissingStoriesAsync(CancellationToken cancellationToken)
	{
		var added = 0;
		foreach (var chunk in Store.ArticleIdsMissingContent(DateTimeOffset.UtcNow - ArticleCutoff).Chunk(StoriesPerRequest))
		{
			var feedIds = Store.GetFeeds().Select(feed => feed.FeedId).ToHashSet();
			var stories = await _caller.GetStoriesAsync(chunk, cancellationToken);
			foreach (var group in stories.GroupBy(story => story.FeedId.ToString(CultureInfo.InvariantCulture)).Where(group => feedIds.Contains(group.Key)))
			{
				added += Store.UpsertArticles(group.Key, group.Select(ToParsedItem), defaultRead: true);
			}
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
