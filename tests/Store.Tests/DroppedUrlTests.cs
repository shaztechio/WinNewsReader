using WinNewsReader.Core;

namespace WinNewsReader.Store.Tests;

public sealed class DroppedUrlTests
{
	[Theory]
	[InlineData("https://example.com/feed.xml", "https://example.com/feed.xml")]
	[InlineData("  http://example.com/  ", "http://example.com/")]
	[InlineData("feed:https://example.com/atom", "https://example.com/atom")]
	[InlineData("feed://example.com/rss", "http://example.com/rss")]
	[InlineData("feeds://example.com/rss", "https://example.com/rss")]
	[InlineData("FEED://example.com/rss", "http://example.com/rss")]
	[InlineData("https://a.example/one\r\nhttps://b.example/two", "https://a.example/one")]
	public void FindsTheFeedAddress(string dropped, string expected) => Assert.Equal(expected, DroppedUrl.Feed(dropped));

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("just some words")]
	[InlineData("ftp://example.com/feed")]
	[InlineData("file:///C:/feed.xml")]
	[InlineData("javascript:alert(1)")]
	public void IgnoresAnythingElse(string? dropped) => Assert.Null(DroppedUrl.Feed(dropped));
}
