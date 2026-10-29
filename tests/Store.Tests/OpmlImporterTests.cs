using System.Text;
using WinNewsReader.Core;
using WinNewsReader.Parser.Feeds;
using WinNewsReader.Parser.Opml;

namespace WinNewsReader.Store.Tests;

/// <summary>Importing OPML into a sync account, which subscribes to each feed on its server.</summary>
public sealed class OpmlImporterTests
{
	private const string Opml = """
		<?xml version="1.0" encoding="UTF-8"?>
		<opml version="1.1"><head><title>Subs</title></head><body>
			<outline text="Top" type="rss" xmlUrl="https://top.example/feed" htmlUrl="https://top.example/"/>
			<outline text="News">
				<outline text="One" type="rss" xmlUrl="https://one.example/feed"/>
				<outline text="Inner">
					<outline text="Nested" type="rss" xmlUrl="https://nested.example/feed"/>
				</outline>
			</outline>
			<outline>
				<outline text="Loose" type="rss" xmlUrl="https://loose.example/feed"/>
			</outline>
		</body></opml>
		""";

	private static IReadOnlyList<OpmlFeed> Parsed() =>
		OpmlImporter.Feeds(OpmlParser.Parse(new ParserData("file:///subs.opml", Encoding.UTF8.GetBytes(Opml))).Children ?? []);

	[Fact]
	public void FeedsComeInOrderWithTheirFolders()
	{
		var feeds = Parsed();

		Assert.Equal(
			[("https://top.example/feed", (string?)null), ("https://one.example/feed", "News"), ("https://nested.example/feed", "Inner"), ("https://loose.example/feed", null)],
			feeds.Select(feed => (feed.Url, feed.FolderName)));
		Assert.Equal("https://top.example/", feeds[0].HomePageUrl);
	}

	private sealed class RecordingAccount(HashSet<string> failing) : IAccount
	{
		public List<(string Url, string? Folder)> Subscribed { get; } = [];

		public string AccountId => "fake";
		public AccountKind Kind => AccountKind.FreshRss;
		public string Name => "Fake";
		public WinNewsReader.Store.ArticlesStore Store => throw new NotSupportedException();
		public bool FeedsCanBeAtTopLevel => false;
		public bool FeedsCanBeInSeveralFolders => false;
		public Task MoveFeedAsync(string feedId, long? fromFolderId, long? toFolderId, bool copy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<RefreshSummary> RefreshAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyList<FeedSpecifier>> FindFeedsAsync(string url, CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<WinNewsReader.Store.StoredFeed> SubscribeAsync(string url, string? folderName, CancellationToken cancellationToken = default)
		{
			if (failing.Contains(url))
			{
				throw new SubscribeException("no feed");
			}
			Subscribed.Add((url, folderName));
			return Task.FromResult(new WinNewsReader.Store.StoredFeed(url, url, null, null, null, null, null));
		}

		public Task RemoveFeedAsync(string feedId, long? folderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task RenameFolderAsync(long folderId, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task RenameFeedAsync(string feedId, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public void SetArticleStatus(IReadOnlyCollection<string> articleIds, WinNewsReader.Store.StatusKey key, bool flag) => throw new NotSupportedException();
	}

	[Fact]
	public async Task SubscribesToEachFeedInItsFolderAndReportsTheOnesThatFail()
	{
		var account = new RecordingAccount(["https://one.example/feed"]);
		var steps = new List<(int, int)>();

		var result = await OpmlImporter.SubscribeAsync(account, Parsed(), (done, total) => steps.Add((done, total)));

		Assert.Equal(3, result.Subscribed);
		Assert.Equal(["One"], result.Failed);
		Assert.Equal([("https://top.example/feed", (string?)null), ("https://nested.example/feed", "Inner"), ("https://loose.example/feed", null)], account.Subscribed);
		Assert.Equal([(1, 4), (2, 4), (3, 4), (4, 4)], steps);
	}
}
