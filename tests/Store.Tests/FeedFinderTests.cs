using System.Text;
using WinNewsReader.Core;
using WinNewsReader.Parser.Feeds;

namespace WinNewsReader.Store.Tests;

/// <summary>NetNewsWire's FeedFinderTests.</summary>
public class FeedFinderTests
{
	[Fact]
	public void KnownFeedSpecifierForRelayFMBlog() =>
		Assert.Equal("https://www.relay.fm/blog/feed", FeedSpecifier.KnownFeedSpecifier("https://www.relay.fm/blog")?.Url);

	[Fact]
	public void KnownFeedSpecifierIgnoresRelayFMRoot() =>
		Assert.Null(FeedSpecifier.KnownFeedSpecifier("https://www.relay.fm"));

	[Fact]
	public void BodyLinksWithNonFeedExtensionsAreIgnored()
	{
		var links = string.Concat(new[]
		{
			"https://example.com/feed.xml",
			"https://example.com/feed.PDF",
			"https://example.com/feed.pdf?download=1",
			"https://example.com/rss.jpg#top",
		}.Select(url => $"<a href=\"{url}\">Link</a>"));
		var html = $"<html><body>{links}</body></html>";

		var specifiers = HtmlFeedFinder.FeedSpecifiers(new ParserData("https://example.com/", Encoding.UTF8.GetBytes(html)));

		Assert.Equal(["https://example.com/feed.xml"], specifiers.Select(specifier => specifier.Url));
	}
}
