using WinNewsReader.Core;
using WinNewsReader.Store;

namespace WinNewsReader.Store.Tests;

/// <summary>An account that records what it's asked to do, for testing code that works through <see cref="IAccount"/>.</summary>
internal sealed class FakeAccount(string name = "Fake", HashSet<string>? failing = null) : IAccount
{
	public List<(string Url, string? Folder)> Subscribed { get; } = [];

	public List<long> RemovedFolders { get; } = [];

	public List<(string FeedId, long? FolderId)> RemovedFeeds { get; } = [];

	public string AccountId => name;
	public AccountKind Kind => AccountKind.FreshRss;
	public string Name => name;
	public ArticlesStore Store => throw new NotSupportedException();
	public bool FeedsCanBeAtTopLevel => false;
	public bool FeedsCanBeInSeveralFolders => false;
	public Task MoveFeedAsync(string feedId, long? fromFolderId, long? toFolderId, bool copy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public Task<RefreshSummary> RefreshAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public Task<IReadOnlyList<FeedSpecifier>> FindFeedsAsync(string url, CancellationToken cancellationToken = default) => throw new NotSupportedException();

	public Task<StoredFeed> SubscribeAsync(string url, string? folderName, CancellationToken cancellationToken = default)
	{
		if (failing?.Contains(url) == true)
		{
			throw new SubscribeException("no feed");
		}
		Subscribed.Add((url, folderName));
		return Task.FromResult(new StoredFeed(url, url, null, null, null, null, null));
	}

	public Task RemoveFeedAsync(string feedId, long? folderId, CancellationToken cancellationToken = default)
	{
		RemovedFeeds.Add((feedId, folderId));
		return Task.CompletedTask;
	}

	public Task RenameFolderAsync(long folderId, string folderName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public Task RenameFeedAsync(string feedId, string feedName, CancellationToken cancellationToken = default) => Task.CompletedTask;

	public Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken = default)
	{
		RemovedFolders.Add(folderId);
		return Task.CompletedTask;
	}

	public void SetArticleStatus(IReadOnlyCollection<string> articleIds, StatusKey key, bool flag) => throw new NotSupportedException();
}
