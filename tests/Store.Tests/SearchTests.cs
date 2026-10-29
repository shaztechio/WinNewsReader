using Microsoft.Data.Sqlite;
using WinNewsReader.Parser.Feeds;

namespace WinNewsReader.Store.Tests;

/// <summary>NetNewsWire's article search: the full-text index and the query built from what the reader types.</summary>
public sealed class SearchTests : IDisposable
{
	private const string Feed = "https://a.example/feed";

	private readonly string _directory = Path.Combine(Path.GetTempPath(), "wnr-tests-" + Guid.NewGuid().ToString("N"));
	private readonly ArticlesStore _store;

	public SearchTests()
	{
		_store = new ArticlesStore(Path.Combine(_directory, "articles.db"));
		_store.AddFeed(Feed);
		_store.UpsertArticles(Feed,
		[
			Item("reader", "On Reading Feeds", "<p>Feed <b>readers</b> are quiet.</p><script>var hidden = 'scriptword';</script>"),
			Item("bereal", "Why I Quit ‘BeReal’", "<p>A photo app.</p>"),
			Item("author", "Untitled Notes", "<p>Plain text.</p>", author: "Jane Writer"),
			Item("summary", "Summary Only", null, summary: "Café crème &amp; croissants"),
		]);
	}

	public void Dispose()
	{
		_store.Dispose();
		SqliteConnection.ClearAllPools();
		Directory.Delete(_directory, recursive: true);
	}

	[Theory]
	[InlineData("reading", "reader")]
	[InlineData("READERS quiet", "reader")]
	[InlineData("feed rea", "reader")]
	[InlineData("bereal", "bereal")]
	[InlineData("jane", "author")]
	[InlineData("crème", "summary")]
	[InlineData("croissants", "summary")]
	public void FindsWordsAndPrefixesInTitleTextAndAuthors(string search, string uniqueId) =>
		Assert.Equal([uniqueId], _store.Search(search).Select(a => a.UniqueId));

	[Theory]
	[InlineData("scriptword")]
	[InlineData("<blockquote>")]
	[InlineData("nothing-here-at-all")]
	[InlineData("   ")]
	[InlineData("\"")]
	[InlineData("AND")]
	public void FindsNothingForMarkupScriptsOrNoWords(string search) => Assert.Empty(_store.Search(search));

	[Fact]
	public void OrMatchesEither() =>
		Assert.Equal(["author", "bereal"], _store.Search("photo OR jane").Select(a => a.UniqueId).Order());

	[Fact]
	public void QueryCharactersCantBreakTheSearch()
	{
		// FTS syntax in what the reader types is reduced to plain words rather than failing.
		Assert.Equal(["reader"], _store.Search("\"reading* -(feeds) ^").Select(a => a.UniqueId));
		Assert.Empty(_store.Search("reading NEAR NOT OR"));
	}

	[Fact]
	public void ChangedArticleIsReindexedAndDeletedOneIsDropped()
	{
		_store.UpsertArticles(Feed, [Item("reader", "On Writing Feeds", "<p>Rewritten.</p>")]);

		Assert.Empty(_store.Search("quiet"));
		Assert.Equal(["reader"], _store.Search("rewritten").Select(a => a.UniqueId));

		_store.RemoveFeed(Feed);
		Assert.Empty(_store.Search("rewritten"));
	}

	[Fact]
	public void ArticlesFromBeforeTheIndexAreIndexedOnSearch()
	{
		var path = Path.Combine(_directory, "old.db");
		using (var store = new ArticlesStore(path))
		{
			store.AddFeed(Feed);
			store.UpsertArticles(Feed, [Item("old", "An Old Article", "<p>From an older database.</p>")]);
		}
		using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
		{
			connection.Open();
			using var command = connection.CreateCommand();
			command.CommandText = "UPDATE articles SET searchRowID = NULL; DELETE FROM search;";
			command.ExecuteNonQuery();
		}

		using var reopened = new ArticlesStore(path);
		Assert.Equal(["old"], reopened.Search("older").Select(a => a.UniqueId));
	}

	[Fact]
	public void RebuildingTheIndexKeepsEveryArticleFindable()
	{
		_store.RebuildSearchIndex();

		Assert.Equal(["reader"], _store.Search("reading").Select(a => a.UniqueId));
		Assert.Equal(["summary"], _store.Search("croissants").Select(a => a.UniqueId));
		Assert.Empty(_store.Search("scriptword"));
	}

	private static ParsedItem Item(string uniqueId, string title, string? html, string? summary = null, string? author = null) => new()
	{
		FeedUrl = Feed,
		UniqueId = uniqueId,
		Title = title,
		ContentHtml = html,
		Summary = summary,
		Authors = author is null ? null : new HashSet<ParsedAuthor> { new(author, null, null, null) },
	};
}
