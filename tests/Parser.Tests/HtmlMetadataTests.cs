using WinNewsReader.Parser.Html;
using static WinNewsReader.Parser.Tests.TestHelpers;

namespace WinNewsReader.Parser.Tests;

public class HtmlMetadataTests
{
	private const string CocoUrl = "https://www.theatlantic.com/entertainment/archive/2017/11/coco-is-among-pixars-best-movies-in-years/546695/";
	private const string CocoImage = "https://cdn.theatlantic.com/assets/media/img/mt/2017/11/1033101_first_full_length_trailer_arrives_pixars_coco/facebook.jpg?1511382177";

	[Fact]
	public void DaringFireball()
	{
		var metadata = HtmlMetadataParser.HtmlMetadata(ParserData("DaringFireball", "html", "http://daringfireball.net/"));

		Assert.Equal("http://daringfireball.net/graphics/favicon.ico?v=005", metadata.Favicons.FirstOrDefault()?.Url);
		var feedLink = Assert.Single(metadata.FeedLinks);
		Assert.Null(feedLink.Title);
		Assert.Equal("application/atom+xml", feedLink.Type);
		Assert.Equal("http://daringfireball.net/feeds/main", feedLink.Url);
	}

	[Fact]
	public void Furbo()
	{
		var metadata = HtmlMetadataParser.HtmlMetadata(ParserData("furbo", "html", "http://furbo.org/"));

		Assert.Equal("http://furbo.org/favicon.ico", metadata.Favicons.FirstOrDefault()?.Url);
		var feedLink = Assert.Single(metadata.FeedLinks);
		Assert.Equal("Iconfactory News Feed", feedLink.Title);
		Assert.Equal("application/rss+xml", feedLink.Type);
	}

	[Fact]
	public void Inessential()
	{
		var metadata = HtmlMetadataParser.HtmlMetadata(ParserData("inessential", "html", "http://inessential.com/"));

		Assert.Null(metadata.Favicons.FirstOrDefault()?.Url);
		var feedLink = Assert.Single(metadata.FeedLinks);
		Assert.Equal("RSS", feedLink.Title);
		Assert.Equal("application/rss+xml", feedLink.Type);
		Assert.Equal("http://inessential.com/xml/rss.xml", feedLink.Url);
		Assert.Empty(metadata.AppleTouchIcons);
	}

	[Fact]
	public void SixColors()
	{
		var metadata = HtmlMetadataParser.HtmlMetadata(ParserData("sixcolors", "html", "http://sixcolors.com/"));

		Assert.Equal("https://sixcolors.com/images/favicon.ico", metadata.Favicons.FirstOrDefault()?.Url);
		var feedLink = Assert.Single(metadata.FeedLinks);
		Assert.Equal("RSS", feedLink.Title);
		Assert.Equal("application/rss+xml", feedLink.Type);
		Assert.Equal("http://feedpress.me/sixcolors", feedLink.Url);

		Assert.Equal(6, metadata.AppleTouchIcons.Count);
		var icon = metadata.AppleTouchIcons[3];
		Assert.Equal("apple-touch-icon", icon.Rel);
		Assert.Equal("120x120", icon.Sizes);
		Assert.Equal("https://sixcolors.com/apple-touch-icon-120.png", icon.Url);
	}

	[Fact]
	public void CocoOGImage()
	{
		var metadata = HtmlMetadataParser.HtmlMetadata(ParserData("coco", "html", CocoUrl));
		Assert.Equal(CocoImage, metadata.OpenGraphImages.First().Url);
	}

	[Fact]
	public void CocoTwitterImage()
	{
		var metadata = HtmlMetadataParser.HtmlMetadata(ParserData("coco", "html", CocoUrl));
		Assert.Equal(CocoImage, metadata.TwitterImageUrl);
	}

	[Fact]
	public void YouTube()
	{
		// YouTube puts its feed links in the body.
		var metadata = HtmlMetadataParser.HtmlMetadata(ParserData("YouTubeTheVolvoRocks", "html", "https://www.youtube.com/user/TheVolvorocks"));

		var feedLink = Assert.Single(metadata.FeedLinks);
		Assert.Equal("RSS", feedLink.Title);
		Assert.Equal("application/rss+xml", feedLink.Type);
		Assert.Equal("https://www.youtube.com/feeds/videos.xml?channel_id=UCct7QF2jcWRY6dhXWMSq9LQ", feedLink.Url);
	}

	[Fact]
	public void BothRssAndAtomFeedLinksReported()
	{
		var metadata = Metadata("""
			<html><head>
			<link rel="alternate" type="application/rss+xml" title="RSS" href="/feed.rss">
			<link rel="alternate" type="application/atom+xml" title="Atom" href="/feed.atom">
			</head><body></body></html>
			""", "http://example.com/");
		Assert.Equal(2, metadata.FeedLinks.Count);
		Assert.Equal(["application/atom+xml", "application/rss+xml"], metadata.FeedLinks.Select(link => link.Type).Order());
	}

	[Fact]
	public void PageWithNoHeadReturnsEmptyMetadata()
	{
		var metadata = Metadata("<html><body>Hello world</body></html>", "http://example.com/");
		Assert.Empty(metadata.FeedLinks);
		Assert.Empty(metadata.Favicons);
		Assert.Empty(metadata.AppleTouchIcons);
	}

	[Fact]
	public void RelativeFaviconResolvedAgainstPageUrl()
	{
		var metadata = Metadata("""
			<html><head>
			<link rel="icon" href="/favicon.ico">
			<link rel="apple-touch-icon" sizes="60x60" href="/touch-60.png">
			<link rel="apple-touch-icon" sizes="120x120" href="/touch-120.png">
			</head><body></body></html>
			""", "http://example.com/some/page.html");
		Assert.Equal("http://example.com/favicon.ico", metadata.Favicons.FirstOrDefault()?.Url);
		Assert.Equal(2, metadata.AppleTouchIcons.Count);
		Assert.Equal(["120x120", "60x60"], metadata.AppleTouchIcons.Select(icon => icon.Sizes).Order());
		Assert.Equal(["http://example.com/touch-120.png", "http://example.com/touch-60.png"], metadata.AppleTouchIcons.Select(icon => icon.Url).Order());
	}

	[Fact]
	public void TypelessFeedLinkAccepted()
	{
		var metadata = Metadata("""
			<html><head>
			<link rel="alternate" href="/feed.xml">
			</head><body></body></html>
			""", "http://example.com/");
		var link = Assert.Single(metadata.FeedLinks);
		Assert.Equal("http://example.com/feed.xml", link.Url);
		Assert.Null(link.Type);
	}

	[Fact]
	public void NonFeedTypedAlternateRejected()
	{
		var metadata = Metadata("""
			<html><head>
			<link rel="alternate" hreflang="fr" type="text/html" href="/fr/page.html">
			</head><body></body></html>
			""", "http://example.com/");
		Assert.Empty(metadata.FeedLinks);
	}

	[Fact]
	public void UnquotedAttributeValues()
	{
		// ranchero.com writes its feed links this way.
		var metadata = Metadata("""
			<html><head>
			<link rel=alternate type=application/rss+xml title="ranchero.com RSS feed" href=https://ranchero.com/xml/rss.xml>
			<link rel=alternate type=application/json title="ranchero.com JSON feed" href=https://ranchero.com/feed.json>
			</head><body></body></html>
			""", "https://ranchero.com/");

		Assert.Equal(2, metadata.FeedLinks.Count);
		var rssLink = Assert.Single(metadata.FeedLinks, link => link.Type == "application/rss+xml");
		Assert.Equal("https://ranchero.com/xml/rss.xml", rssLink.Url);
		Assert.Equal("ranchero.com RSS feed", rssLink.Title);
	}

	private static HtmlMetadata Metadata(string html, string url) => HtmlMetadataParser.HtmlMetadata(ParserData(html, url));
}
