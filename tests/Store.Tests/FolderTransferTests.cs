using WinNewsReader.Core;

namespace WinNewsReader.Store.Tests;

/// <summary>Dragging a folder onto another account, which takes its feeds along.</summary>
public sealed class FolderTransferTests
{
	private static readonly StoredFolder News = new(7, "News");

	private static readonly StoredFeed[] Feeds =
	[
		new("https://a.example/feed", "https://a.example/feed", "A", null, null, null, null),
		new("https://b.example/feed", "https://b.example/feed", "B", null, null, null, null),
	];

	[Fact]
	public async Task MovingSubscribesToEveryFeedInAFolderOfTheSameNameThenRemovesTheFolder()
	{
		var source = new FakeAccount("Source");
		var target = new FakeAccount("Target");

		var result = await FeedTransfer.TransferFolderAsync(source, News, Feeds, target, copy: false);

		Assert.Equal(2, result.Transferred);
		Assert.Empty(result.Failed);
		Assert.Equal([("https://a.example/feed", "News"), ("https://b.example/feed", "News")], target.Subscribed.Select(s => (s.Url, s.Folder)));
		Assert.Equal([7L], source.RemovedFolders);
	}

	[Fact]
	public async Task CopyingLeavesTheFolderWhereItWas()
	{
		var source = new FakeAccount("Source");

		await FeedTransfer.TransferFolderAsync(source, News, Feeds, new FakeAccount("Target"), copy: true);

		Assert.Empty(source.RemovedFolders);
	}

	[Fact]
	public async Task AFeedThatFailsKeepsTheFolderAndIsNamed()
	{
		var source = new FakeAccount("Source");
		var target = new FakeAccount("Target", failing: ["https://b.example/feed"]);

		var result = await FeedTransfer.TransferFolderAsync(source, News, Feeds, target, copy: false);

		Assert.Equal(1, result.Transferred);
		Assert.Equal(["B"], result.Failed);
		Assert.Empty(source.RemovedFolders);
	}

	[Fact]
	public async Task WithinOneAccountItIsRefused()
	{
		var account = new FakeAccount();

		await Assert.ThrowsAsync<ArgumentException>(() => FeedTransfer.TransferFolderAsync(account, News, Feeds, account, copy: false));
	}
}
