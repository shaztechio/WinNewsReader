using Microsoft.Data.Sqlite;

namespace WinNewsReader.Store.Tests;

/// <summary>The steps behind undoing a delete in the local account: the feed or folder is put back where it was, with its edited name.</summary>
public sealed class RestoreDeletedTests : IDisposable
{
	private const string Feed = "https://a.example/feed";

	private readonly string _directory = Path.Combine(Path.GetTempPath(), "wnr-tests-" + Guid.NewGuid().ToString("N"));
	private readonly ArticlesStore _store;

	public RestoreDeletedTests() => _store = new ArticlesStore(Path.Combine(_directory, "articles.db"));

	public void Dispose()
	{
		_store.Dispose();
		SqliteConnection.ClearAllPools();
		Directory.Delete(_directory, recursive: true);
	}

	[Fact]
	public void ADeletedFeedComesBackInItsFolderWithItsEditedName()
	{
		var folder = _store.EnsureFolder("News");
		_store.AddFeed(Feed, "A", "https://a.example/", folder.FolderId);
		_store.SetFeedEditedName(Feed, "My A");
		var deleted = _store.GetFeed(Feed)!;

		_store.RemoveFeed(Feed, folder.FolderId);
		Assert.Null(_store.GetFeed(Feed));

		var restored = _store.AddFeed(deleted.Url, deleted.Name, deleted.HomePageUrl, _store.EnsureFolder("News").FolderId);
		_store.SetFeedEditedName(restored.FeedId, deleted.EditedName);

		var back = _store.GetFeed(Feed)!;
		Assert.Equal("My A", back.EditedName);
		Assert.Equal("https://a.example/", back.HomePageUrl);
		Assert.Contains(_store.GetFolders(), f => f.Name == "News");
	}

	[Fact]
	public void ADeletedFolderComesBackWithItsFeeds()
	{
		var folder = _store.EnsureFolder("News");
		_store.AddFeed(Feed, "A", null, folder.FolderId);

		_store.RemoveFolder(folder.FolderId);
		Assert.Empty(_store.GetFolders());
		Assert.Null(_store.GetFeed(Feed));

		var again = _store.EnsureFolder("News");
		_store.AddFeed(Feed, "A", null, again.FolderId);

		Assert.Single(_store.GetFolders());
		Assert.NotNull(_store.GetFeed(Feed));
	}
}
