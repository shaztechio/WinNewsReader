using WinNewsReader.Store;

namespace WinNewsReader.Core;

/// <summary>
/// Moving or copying a feed to another account, as dragging one onto another account does in NetNewsWire: the feed is
/// subscribed in the target account by its URL, and a move then removes it from the source. Read and starred states
/// don't carry over, since each account keeps its own.
/// </summary>
public static class FeedTransfer
{
	/// <summary>
	/// Subscribes to <paramref name="feed"/> in <paramref name="target"/>, in the folder named <paramref name="toFolderName"/>
	/// or the top level. Unless copying, then removes it from where it was in <paramref name="source"/>. Nothing is removed
	/// if the subscribe fails. Returns the target account's feed.
	/// </summary>
	public static async Task<StoredFeed> TransferAsync(
		IAccount source, StoredFeed feed, long? fromFolderId, IAccount target, string? toFolderName, bool copy, CancellationToken cancellationToken = default)
	{
		if (source == target)
		{
			throw new ArgumentException("Within one account, use IAccount.MoveFeedAsync.", nameof(target));
		}
		var added = await target.SubscribeAsync(feed.Url, toFolderName, cancellationToken);
		if (!copy)
		{
			await source.RemoveFeedAsync(feed.FeedId, fromFolderId, cancellationToken);
		}
		return added;
	}
}
