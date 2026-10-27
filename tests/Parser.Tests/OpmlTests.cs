using WinNewsReader.Parser.Opml;
using static WinNewsReader.Parser.Tests.TestHelpers;

namespace WinNewsReader.Parser.Tests;

/// <summary>Ported from NetNewsWire's OPMLTests.</summary>
public class OpmlTests
{
	[Fact]
	public void NotOpmlThrows() =>
		Assert.Throws<OpmlException>(() => OpmlParser.Parse(ParserData("DaringFireball", "rss", "http://daringfireball.net/")));

	[Fact]
	public void SubsStructure()
	{
		var document = OpmlParser.Parse(ParserData("Subs", "opml", "http://example.org/"));
		Assert.Equal("Subs", document.Title);
		Assert.Equal("http://example.org/", document.Url);
		CheckStructure(document);
	}

	[Fact]
	public void FindingTitles() => CheckStructure(OpmlParser.Parse(ParserData("SubsNoTitleAttributes", "opml", "http://example.org/")));

	private static void CheckStructure(OpmlItem item)
	{
		if (item is not OpmlDocument)
		{
			Assert.NotNull(item.Text);
		}
		var isFolder = item.IsFolder || item.AttributeTitle == "Skip";
		if (isFolder)
		{
			Assert.Null(item.FeedSpecifier);
		}
		else
		{
			Assert.NotNull(item.FeedSpecifier);
			Assert.NotNull(item.FeedSpecifier.Title);
			Assert.NotEmpty(item.FeedSpecifier.FeedUrl);
		}
		foreach (var child in item.Children ?? [])
		{
			CheckStructure(child);
		}
	}

	[Fact]
	public void DeeplyNestedFolders()
	{
		const string opml = """
			<?xml version="1.0" encoding="UTF-8"?>
			<opml version="1.0">
			  <head><title>Nested</title></head>
			  <body>
			    <outline text="L1">
			      <outline text="L2">
			        <outline text="L3">
			          <outline text="feed" type="rss" xmlUrl="http://example.com/feed.xml"/>
			        </outline>
			      </outline>
			    </outline>
			  </body>
			</opml>
			""";
		var document = OpmlParser.Parse(ParserData(opml, "http://example.com/"));
		var l1 = document.Children![0];
		Assert.Equal("L1", l1.Text);
		var l2 = l1.Children![0];
		Assert.Equal("L2", l2.Text);
		var l3 = l2.Children![0];
		Assert.Equal("L3", l3.Text);
		Assert.Equal("http://example.com/feed.xml", l3.Children![0].FeedSpecifier?.FeedUrl);
	}

	[Fact]
	public void XmlUrlAttributeIsCaseInsensitive()
	{
		const string opml = """
			<?xml version="1.0" encoding="UTF-8"?>
			<opml version="1.0">
			  <head><title>Mixed-case attributes</title></head>
			  <body>
			    <outline text="camelCase" type="rss" xmlUrl="http://example.com/a.xml"/>
			    <outline text="lowercase" type="rss" xmlurl="http://example.com/b.xml"/>
			    <outline text="UPPERCASE" type="rss" XMLURL="http://example.com/c.xml"/>
			    <outline text="MixedCase" type="rss" XmlUrl="http://example.com/d.xml"/>
			  </body>
			</opml>
			""";
		var feedUrls = OpmlParser.Parse(ParserData(opml, "http://example.com/")).Children!.Select(c => c.FeedSpecifier?.FeedUrl).ToList();
		Assert.Equal(["http://example.com/a.xml", "http://example.com/b.xml", "http://example.com/c.xml", "http://example.com/d.xml"], feedUrls);
	}

	[Fact]
	public void NonFeedOutlinesParseAsFolders()
	{
		const string opml = """
			<?xml version="1.0" encoding="UTF-8"?>
			<opml version="1.0">
			  <head><title>Mixed</title></head>
			  <body>
			    <outline text="Just a note"/>
			    <outline text="A folder">
			      <outline text="Real feed" type="rss" xmlUrl="http://example.com/feed.xml"/>
			    </outline>
			    <outline text="Another note"/>
			  </body>
			</opml>
			""";
		var children = OpmlParser.Parse(ParserData(opml, "http://example.com/")).Children!;
		Assert.Equal(3, children.Count);
		Assert.Null(children[0].FeedSpecifier);
		Assert.Null(children[2].FeedSpecifier);
		Assert.Null(children[1].FeedSpecifier);
		Assert.Equal("http://example.com/feed.xml", children[1].Children![0].FeedSpecifier?.FeedUrl);
	}
}
