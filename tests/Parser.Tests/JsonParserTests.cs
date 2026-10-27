using WinNewsReader.Parser.Feeds;
using static WinNewsReader.Parser.Tests.TestHelpers;

namespace WinNewsReader.Parser.Tests;

/// <summary>Ported from NetNewsWire's JSONFeedParserTests and RSSInJSONParserTests.</summary>
public class JsonParserTests
{
	[Fact]
	public void FaviconAndIconUrls()
	{
		var feed = Parse(ParserData("DaringFireball", "json", "http://daringfireball.net/"));
		Assert.Equal("https://daringfireball.net/graphics/favicon-64.png", feed.FaviconUrl);
		Assert.Equal("https://daringfireball.net/graphics/apple-touch-icon.png", feed.IconUrl);
	}

	[Theory]
	[InlineData("allthis", "http://leancrew.com/allthis/", 12)]
	[InlineData("curt", "http://curtclifton.net/", 26)]
	[InlineData("pxlnv", "http://pxlnv.com/", 20)]
	[InlineData("rose", "http://www.rosemaryorchard.com/", 84)]
	[InlineData("3960", "http://journal.3960.org/", 20)]
	public void ItemCounts(string filename, string url, int expectedCount) =>
		Assert.Equal(expectedCount, Parse(ParserData(filename, "json", url)).Items.Count);

	[Fact]
	public void CurtContentHtml()
	{
		var feed = Parse(ParserData("curt", "json", "http://curtclifton.net/"));
		var article = Assert.Single(feed.Items, i => i.Title == "Twitter Quitter");
		Assert.StartsWith(
			"<p>I&#8217;ve decided to close my Twitter account. William Van Hecke <a href=\"https://tinyletter.com/fet/letters/microcosmographia-xlxi-reasons-to-stay-on-twitter\">makes a convincing case</a>",
			article.ContentHtml);
	}

	[Fact]
	public void Language()
	{
		var feed = Parse(ParserData("3960", "json", "http://journal.3960.org/"));
		Assert.Equal("de-DE", feed.Language);
		Assert.All(feed.Items, item => Assert.Equal("de-DE", item.Language));
	}

	[Fact]
	public void Authors()
	{
		var feed = Parse(ParserData("authors", "json", "https://example.com/"));
		Assert.Equal(4, feed.Items.Count);
		var rootAuthors = new HashSet<ParsedAuthor> { new("Root Author 1", null, null, null), new("Root Author 2", null, null, null) };
		var itemAuthors = new HashSet<ParsedAuthor> { new("Item Author 1", null, null, null), new("Item Author 2", null, null, null) };
		var legacyItemAuthors = new HashSet<ParsedAuthor> { new("Legacy Item Author", null, null, null) };

		Assert.True(rootAuthors.SetEquals(feed.Authors!));
		Assert.Null(Assert.Single(feed.Items, i => i.UniqueId == "Item without authors").Authors);
		Assert.True(legacyItemAuthors.SetEquals(Assert.Single(feed.Items, i => i.UniqueId == "Item with legacy author").Authors!));
		Assert.True(itemAuthors.SetEquals(Assert.Single(feed.Items, i => i.UniqueId == "Item with modern authors").Authors!));
		Assert.True(itemAuthors.SetEquals(Assert.Single(feed.Items, i => i.UniqueId == "Item with both").Authors!));
	}

	[Fact]
	public void Attachments()
	{
		var item = Parse(ParserData("attachments", "json", "https://example.com/")).Items.First();
		Assert.Equal(2, item.Attachments!.Count);
		var audio = Assert.Single(item.Attachments, a => a.Url == "https://example.com/episode1.mp3");
		Assert.Equal("audio/mpeg", audio.MimeType);
		Assert.Equal("Episode 1", audio.Title);
		Assert.Equal(12345678, audio.SizeInBytes);
		Assert.Equal(3600, audio.DurationInSeconds);
		Assert.Null(Assert.Single(item.Attachments, a => a.Url == "https://example.com/episode1.jpg").MimeType);
	}

	[Fact]
	public void RssInJsonFeedLanguage() =>
		Assert.Equal("en-us", Parse(ParserData("ScriptingNews", "json", "http://scripting.com/")).Language);
}
