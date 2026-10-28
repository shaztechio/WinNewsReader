using Microsoft.Data.Sqlite;
using WinNewsReader.Core;
using WinNewsReader.Parser.Feeds;

namespace WinNewsReader.Store.Tests;

public sealed class FolderTests : IDisposable
{
	private const string FeedA = "https://a.example/feed";
	private const string FeedB = "https://b.example/feed";

	private readonly string _directory = Path.Combine(Path.GetTempPath(), "wnr-tests-" + Guid.NewGuid().ToString("N"));
	private readonly FeedDownloader _downloader = new();
	private readonly ArticlesStore _store;
	private readonly LocalAccount _account;

	public FolderTests()
	{
		_store = new ArticlesStore(Path.Combine(_directory, "articles.db"));
		_account = new LocalAccount(_store, _downloader);
	}

	public void Dispose()
	{
		_downloader.Dispose();
		_store.Dispose();
		SqliteConnection.ClearAllPools();
		Directory.Delete(_directory, recursive: true);
	}

	[Fact]
	public void ImportOpmlAddsFeedsAndFolders()
	{
		var opmlPath = WriteOpml("""
			<outline text="A" xmlUrl="https://a.example/feed"/>
			<outline text="Folder">
				<outline text="B" xmlUrl="https://b.example/feed"/>
			</outline>
			""");

		Assert.Equal(new OpmlImportResult(2, 1), _account.ImportOpml(opmlPath));
		Assert.Equal(new OpmlImportResult(0, 0), _account.ImportOpml(opmlPath));
		Assert.Equal(["A"], _store.GetTopLevelFeeds().Select(f => f.Name));
		var folder = Assert.Single(_store.GetFolders());
		Assert.Equal("Folder", folder.Name);
		Assert.Equal(["B"], _store.GetFeeds(folder.FolderId).Select(f => f.Name));
	}

	[Fact]
	public void NestedFoldersBecomeTopLevelFoldersAndUntitledFoldersAreSkipped()
	{
		var opmlPath = WriteOpml("""
			<outline text="Outer">
				<outline text="A" xmlUrl="https://a.example/feed"/>
				<outline text="Inner">
					<outline text="B" xmlUrl="https://b.example/feed"/>
				</outline>
				<outline>
					<outline text="C" xmlUrl="https://c.example/feed"/>
				</outline>
			</outline>
			<outline text="Empty"/>
			""");

		_account.ImportOpml(opmlPath);

		Assert.Empty(_store.GetTopLevelFeeds());
		Assert.Equal(
			["Empty: ", "Inner: B", "Outer: A, C"],
			_store.GetFolders().Select(folder => $"{folder.Name}: {string.Join(", ", _store.GetFeeds(folder.FolderId).Select(f => f.Name))}"));
	}

	[Fact]
	public void ImportPutsExistingFeedInFolderAndKeepsItsName()
	{
		_store.AddFeed(FeedA, "Original");
		_account.ImportOpml(WriteOpml("""<outline text="Folder"><outline text="Renamed" xmlUrl="https://a.example/feed"/></outline>"""));

		Assert.Equal("Original", Assert.Single(_store.GetTopLevelFeeds()).Name);
		Assert.Equal("Original", Assert.Single(_store.GetFeeds(_store.EnsureFolder("Folder").FolderId)).Name);
		Assert.Single(_store.GetFeeds());
	}

	[Fact]
	public void EnsureFolderReturnsExistingFolder()
	{
		var folder = _store.EnsureFolder("News");
		Assert.Equal(folder, _store.EnsureFolder("News"));
		Assert.NotEqual(folder, _store.EnsureFolder("Other"));
	}

	[Fact]
	public void FeedInTwoFoldersStaysUntilRemovedFromBoth()
	{
		var one = _store.EnsureFolder("One");
		var two = _store.EnsureFolder("Two");
		_store.AddFeed(FeedA, folderId: one.FolderId);
		_store.AddFeed(FeedA, folderId: two.FolderId);
		_store.UpsertArticles(FeedA, [Item("x")]);

		Assert.True(_store.RemoveFeed(FeedA, one.FolderId));
		Assert.False(_store.RemoveFeed(FeedA, one.FolderId));
		Assert.Single(_store.GetFeeds());
		Assert.Equal(1, _store.UnreadCount());

		Assert.True(_store.RemoveFeed(FeedA, two.FolderId));
		Assert.Empty(_store.GetFeeds());
		Assert.Equal(0, _store.UnreadCount());
	}

	[Fact]
	public void RemovingFromTopLevelKeepsFolderMembership()
	{
		var folder = _store.EnsureFolder("Folder");
		_store.AddFeed(FeedA);
		_store.AddFeed(FeedA, folderId: folder.FolderId);

		Assert.True(_store.RemoveFeed(FeedA, null));
		Assert.Empty(_store.GetTopLevelFeeds());
		Assert.Single(_store.GetFeeds(folder.FolderId));
	}

	[Fact]
	public void RemoveFolderDeletesFeedsOnlyInIt()
	{
		var folder = _store.EnsureFolder("Folder");
		_store.AddFeed(FeedA, folderId: folder.FolderId);
		_store.AddFeed(FeedB, folderId: folder.FolderId);
		_store.AddFeed(FeedB);
		_store.UpsertArticles(FeedA, [Item("a")]);
		_store.UpsertArticles(FeedB, [Item("b")]);

		Assert.True(_store.RemoveFolder(folder.FolderId));

		Assert.Empty(_store.GetFolders());
		Assert.Equal([FeedB], _store.GetFeeds().Select(f => f.FeedId));
		Assert.Equal(["b"], _store.GetUnreadArticles().Select(a => a.UniqueId));
		Assert.False(_store.RemoveFolder(folder.FolderId));
	}

	[Fact]
	public void UnsubscribeRemovesFeedFromEveryFolder()
	{
		var folder = _store.EnsureFolder("Folder");
		_store.AddFeed(FeedA, folderId: folder.FolderId);
		_store.AddFeed(FeedA);

		Assert.True(_store.RemoveFeed(FeedA));
		Assert.Empty(_store.GetFeeds(folder.FolderId));
		Assert.Single(_store.GetFolders());
	}

	[Fact]
	public void RenameFolder()
	{
		var folder = _store.EnsureFolder("Old");
		Assert.True(_store.RenameFolder(folder.FolderId, "New"));
		Assert.Equal("New", Assert.Single(_store.GetFolders()).Name);
	}

	[Fact]
	public void ExportMatchesNetNewsWireFormat()
	{
		var folder = _store.EnsureFolder("Tech & Code");
		_store.AddFeed(FeedB, "Feed 10", "https://b.example/");
		_store.AddFeed("https://c.example/feed?a=1&b=2", "Feed 9");
		_store.AddFeed(FeedA, "\"Quoted\"", folderId: folder.FolderId);
		_store.EnsureFolder("Empty");

		Assert.Equal(
			"""
			<?xml version="1.0" encoding="UTF-8"?>
			<!-- OPML generated by WinNewsReader -->
			<opml version="1.1">
			<head>
			<title>Subs &lt;1&gt;</title>
			</head>
			<body>
				<outline text="Feed 9" title="Feed 9" description="" type="rss" version="RSS" htmlUrl="" xmlUrl="https://c.example/feed?a=1&amp;b=2"/>
				<outline text="Feed 10" title="Feed 10" description="" type="rss" version="RSS" htmlUrl="https://b.example/" xmlUrl="https://b.example/feed"/>
				<outline text="Empty" title="Empty"/>
				<outline text="Tech &amp; Code" title="Tech &amp; Code">
					<outline text="&quot;Quoted&quot;" title="&quot;Quoted&quot;" description="" type="rss" version="RSS" htmlUrl="" xmlUrl="https://a.example/feed"/>
				</outline>
			</body>
			</opml>
			""".ReplaceLineEndings("\n"),
			OpmlExporter.OpmlString(_store, "Subs <1>"));
	}

	[Fact]
	public void SubsOpmlRoundTrips()
	{
		var imported = _account.ImportOpml(Path.Combine(AppContext.BaseDirectory, "Resources", "Subs.opml"));
		Assert.True(imported.FeedsAdded > 100);
		Assert.Equal(4, imported.FoldersAdded);

		var exportPath = Path.Combine(_directory, "export.opml");
		File.WriteAllText(exportPath, OpmlExporter.OpmlString(_store, "Export"));
		using var copy = new ArticlesStore(Path.Combine(_directory, "copy.db"));
		Assert.Equal(imported, new LocalAccount(copy, _downloader).ImportOpml(exportPath));

		Assert.Equal(Tree(_store), Tree(copy));
	}

	[Fact]
	public void DatabaseFromBeforeFoldersKeepsFeedsAtTopLevel()
	{
		var path = Path.Combine(_directory, "old.db");
		using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
		{
			connection.Open();
			using var command = connection.CreateCommand();
			command.CommandText = """
				CREATE TABLE feeds (feedID TEXT NOT NULL PRIMARY KEY, url TEXT NOT NULL, name TEXT, homePageURL TEXT, iconURL TEXT, etag TEXT, lastModified TEXT, dateAdded DATE NOT NULL);
				INSERT INTO feeds (feedID, url, dateAdded) VALUES ('https://a.example/feed', 'https://a.example/feed', 0);
				""";
			command.ExecuteNonQuery();
		}

		using var store = new ArticlesStore(path);
		Assert.Equal([FeedA], store.GetTopLevelFeeds().Select(f => f.FeedId));
		Assert.Empty(store.GetFolders());
	}

	private static List<string> Tree(ArticlesStore store) =>
	[
		.. store.GetTopLevelFeeds().Select(f => $"{f.Url} {f.Name} {f.HomePageUrl}"),
		.. store.GetFolders().SelectMany(folder => store.GetFeeds(folder.FolderId).Select(f => $"{folder.Name}/{f.Url} {f.Name} {f.HomePageUrl}")),
	];

	private string WriteOpml(string body)
	{
		var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".opml");
		File.WriteAllText(path, $"""
			<?xml version="1.0" encoding="UTF-8"?>
			<opml version="1.0">
			<head><title>Subs</title></head>
			<body>
			{body}
			</body>
			</opml>
			""");
		return path;
	}

	private static ParsedItem Item(string uniqueId) => new() { FeedUrl = "", UniqueId = uniqueId, Title = uniqueId };
}
