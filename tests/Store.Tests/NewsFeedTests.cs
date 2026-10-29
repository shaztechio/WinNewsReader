using WinNewsReader.Parser.Feeds;

namespace WinNewsReader.Store.Tests;

/// <summary>The app's own news feed on the website, which Help → Add WinNewsReader News Feed subscribes to.</summary>
public sealed class NewsFeedTests
{
	private static string FeedPath()
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			var path = Path.Combine(directory.FullName, "site", "feed.xml");
			if (File.Exists(path))
			{
				return path;
			}
		}
		throw new FileNotFoundException("site/feed.xml");
	}

	[Fact]
	public void TheSitesFeedParsesWithEntriesThatHaveUniqueIds()
	{
		var parsed = FeedParser.Parse(new ParserData("https://winnewsreader.com/feed.xml", File.ReadAllBytes(FeedPath())));

		Assert.NotNull(parsed);
		Assert.Equal("WinNewsReader News", parsed!.Title);
		Assert.NotEmpty(parsed.Items);
		Assert.All(parsed.Items, item =>
		{
			Assert.False(string.IsNullOrWhiteSpace(item.Title));
			Assert.NotNull(item.DatePublished ?? item.DateModified);
		});
		Assert.Equal(parsed.Items.Count, parsed.Items.Select(item => item.UniqueId).Distinct().Count());
	}
}
