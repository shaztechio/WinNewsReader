using WinNewsReader.Parser.Feeds;
using static WinNewsReader.Parser.Tests.TestHelpers;

namespace WinNewsReader.Parser.Tests;

/// <summary>Ported from NetNewsWire's FeedParserTypeTests.</summary>
public class FeedTypeTests
{
	[Theory]
	[InlineData("DaringFireball", "html", "http://daringfireball.net/")]
	[InlineData("furbo", "html", "http://furbo.org/")]
	[InlineData("inessential", "html", "http://inessential.com/")]
	[InlineData("sixcolors", "html", "https://sixcolors.com/")]
	public void HtmlFilesAreNotAFeed(string filename, string ext, string url) =>
		Assert.Equal(FeedType.NotAFeed, FeedParser.DetectType(ParserData(filename, ext, url)));

	[Theory]
	[InlineData("EMarley", "rss", "https://medium.com/@emarley")]
	[InlineData("scriptingNews", "rss", "http://scripting.com/")]
	[InlineData("KatieFloyd", "rss", "https://katiefloyd.com/")]
	[InlineData("manton", "rss", "http://manton.org/")]
	[InlineData("dcrainmaker", "xml", "https://www.dcrainmaker.com/")]
	[InlineData("macworld", "rss", "https://www.macworld.com/")]
	[InlineData("natasha", "xml", "https://www.natashatherobot.com/")]
	[InlineData("donthitsave", "xml", "http://donthitsave.com/donthitsavefeed.xml")]
	[InlineData("bio", "rdf", "http://connect.biorxiv.org/")]
	[InlineData("phpxml", "rss", "https://www.fcutrecht.net/")]
	public void RssFeedsAreRss(string filename, string ext, string url) =>
		Assert.Equal(FeedType.Rss, FeedParser.DetectType(ParserData(filename, ext, url)));

	[Theory]
	// File extension is .rss, but it's really an Atom feed.
	[InlineData("DaringFireball", "rss", "http://daringfireball.net/")]
	[InlineData("OneFootTsunami", "atom", "http://onefoottsunami.com/")]
	[InlineData("russcox", "atom", "https://research.swtch.com/")]
	public void AtomFeedsAreAtom(string filename, string ext, string url) =>
		Assert.Equal(FeedType.Atom, FeedParser.DetectType(ParserData(filename, ext, url)));

	[Fact]
	public void ScriptingNewsJsonIsRssInJson() =>
		Assert.Equal(FeedType.RssInJson, FeedParser.DetectType(ParserData("ScriptingNews", "json", "http://scripting.com/")));

	[Theory]
	[InlineData("inessential", "json", "http://inessential.com/")]
	[InlineData("allthis", "json", "http://leancrew.com/allthis/")]
	[InlineData("curt", "json", "http://curtclifton.net/")]
	[InlineData("pxlnv", "json", "http://pxlnv.com/")]
	[InlineData("rose", "json", "https://www.rosemaryorchard.com/")]
	public void JsonFeedsAreJsonFeed(string filename, string ext, string url) =>
		Assert.Equal(FeedType.JsonFeed, FeedParser.DetectType(ParserData(filename, ext, url)));

	[Fact]
	public void PartialDataThatMightBeJsonFeedIsUnknown()
	{
		// The partial data isn't enough to detect a JSON Feed, so the result is Unknown rather than NotAFeed.
		var d = ParserData("allthis-partial", "json", "http://leancrew.com/allthis/");
		Assert.Equal(FeedType.Unknown, FeedParser.DetectType(d, isPartialData: true));
	}
}
