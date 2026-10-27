using WinNewsReader.Parser.Feeds;
using static WinNewsReader.Parser.Tests.TestHelpers;

namespace WinNewsReader.Parser.Tests;

/// <summary>Ported from NetNewsWire's RSSParserTests.</summary>
public class RssParserTests
{
	[Fact]
	public void NatashaTheRobot()
	{
		var feed = Parse(ParserData("natasha", "xml", "https://www.natashatherobot.com/"));
		Assert.Equal(10, feed.Items.Count);
	}

	[Fact]
	public void TheOmniShowAttachments()
	{
		var feed = Parse(ParserData("theomnishow", "rss", "https://theomnishow.omnigroup.com/"));
		foreach (var article in feed.Items)
		{
			var attachment = Assert.Single(article.Attachments!);
			Assert.NotNull(attachment.MimeType);
			Assert.NotNull(attachment.SizeInBytes);
			Assert.Contains("cloudfront", attachment.Url);
			Assert.True(attachment.SizeInBytes >= 22275279);
			Assert.Equal("audio/mpeg", attachment.MimeType);
			Assert.NotNull(attachment.DurationInSeconds);
		}
	}

	[Fact]
	public void AtpEnclosureAndMediaContentMerged()
	{
		// Each atp.fm item has the same file as both <enclosure> and <media:content>.
		var feed = Parse(ParserData("atp", "rss", "http://atp.fm/"));
		Assert.Equal(100, feed.Items.Count);
		foreach (var article in feed.Items)
		{
			var attachment = Assert.Single(article.Attachments!);
			// Some items have no type attribute, only medium.
			Assert.Equal(AttachmentMedium.Audio, attachment.Medium);
			Assert.NotNull(attachment.SizeInBytes);
		}
		var item = Assert.Single(feed.Items, i => i.Title == "311: Mutually Assured Destruction");
		var first = item.Attachments![0];
		Assert.Equal("http://traffic.libsyn.com/atpfm/atp311.mp3", first.Url);
		Assert.Equal("audio/mpeg", first.MimeType);
		Assert.Equal(54919661, first.SizeInBytes);
		Assert.Equal(6849, first.DurationInSeconds); // itunes:duration 01:54:09
		Assert.Equal("http://static1.squarespace.com/static/513abd71e4b0fe58c655c105/t/52c45a37e4b0a77a5034aa84/1388599866232/1500w/Artwork.jpg", item.ImageUrl);
	}

	[Fact]
	public void CloudBlogMediaContentImages()
	{
		var feed = Parse(ParserData("cloudblog", "rss", "https://cloud.google.com/blog/"));
		var withAttachments = feed.Items.Where(item => item.Attachments is not null).ToList();
		Assert.Equal(18, withAttachments.Count);
		foreach (var article in withAttachments)
		{
			var attachment = Assert.Single(article.Attachments!);
			Assert.StartsWith("https://storage.googleapis.com/", attachment.Url);
			Assert.NotNull(attachment.Width);
			Assert.NotNull(attachment.Height);
		}
	}

	[Fact]
	public void MediaGroupKeepsDefaultRendition()
	{
		var item = Assert.Single(Parse(ParserData("media-group", "rss", "https://example.com/feed")).Items);
		Assert.Equal("Real Article Title", item.Title);
		Assert.Equal("https://example.com/videos/1.jpg", item.ThumbnailUrl);
		var attachment = Assert.Single(item.Attachments!);
		Assert.Equal("https://example.com/videos/1-high.mp4", attachment.Url);
		Assert.Equal("video/mp4", attachment.MimeType);
		Assert.Equal(AttachmentMedium.Video, attachment.Medium);
		Assert.Equal(1920, attachment.Width);
		Assert.Equal(1080, attachment.Height);
		Assert.Equal(125, attachment.DurationInSeconds);
	}

	[Fact]
	public void GravatarBecomesAuthorAvatar()
	{
		// WordPress adds the author's Gravatar as a media:content titled with the author's name.
		var feed = Parse(ParserData("postpunkmonk", "rss", "https://postpunkmonk.com/"));
		var item = feed.Items.First(i => i.Title?.StartsWith("Automatic Honor") == true);
		var author = Assert.Single(item.Authors!);
		Assert.Equal("postpunkmonk", author.Name);
		Assert.Equal("https://0.gravatar.com/avatar/fb95b7a51c1ce5897b406c1e656c6eb5f1ecfb4f537d200249ad0c3c53d6b7a1?s=96&d=&r=PG", author.AvatarUrl);
		// In feed order.
		Assert.Equal(
			[
				"https://postpunkmonk.com/wp-content/uploads/2026/10/automatic-shadowplay.jpg",
				"https://postpunkmonk.com/wp-content/uploads/2026/10/automatic40eulogy9-26.jpg",
				"https://postpunkmonk.com/wp-content/uploads/2022/10/8-ball-yes.jpg",
			],
			item.Attachments!.Select(a => a.Url));
	}

	[Fact]
	public void TheOmniShowUniqueIds()
	{
		var feed = Parse(ParserData("theomnishow", "rss", "https://theomnishow.omnigroup.com/"));
		Assert.All(feed.Items, article => Assert.StartsWith("https://theomnishow.omnigroup.com/episode/", article.UniqueId));
	}

	[Fact]
	public void MacworldUniqueIdsAreCalculated()
	{
		// Macworld's feed has no guids, so unique IDs are MD5 hashes.
		var feed = Parse(ParserData("macworld", "rss", "https://www.macworld.com/"));
		Assert.All(feed.Items, article => Assert.Equal(32, article.UniqueId.Length));
	}

	[Fact]
	public void MacworldAuthorsAreNames()
	{
		// Macworld uses names, despite the RSS spec saying authors should be email addresses.
		var feed = Parse(ParserData("macworld", "rss", "https://www.macworld.com/"));
		foreach (var article in feed.Items)
		{
			var author = article.Authors!.First();
			Assert.Null(author.EmailAddress);
			Assert.Null(author.Url);
			Assert.NotNull(author.Name);
		}
	}

	[Fact]
	public void MonkeyDomGuidsThatArentPermalinks()
	{
		// coding.monkeydom.de's guids claim to be permalinks but aren't.
		var feed = Parse(ParserData("monkeydom", "rss", "https://coding.monkeydom.de/"));
		Assert.All(feed.Items, article => Assert.Null(article.Url));
	}

	[Fact]
	public void EmptyContentEncodedIsIgnored()
	{
		// https://github.com/brentsimmons/NetNewsWire/issues/529
		var feed = Parse(ParserData("atp", "rss", "http://atp.fm/"));
		Assert.All(feed.Items, article => Assert.NotNull(article.ContentHtml));
	}

	[Fact]
	public void FeedKnownToHaveGuidsThatArentPermalinks()
	{
		var feed = Parse(ParserData("livemint", "xml", "https://www.livemint.com/rss/news"));
		Assert.All(feed.Items, article => Assert.Null(article.Url));
	}

	[Fact]
	public void AuthorsWithTitlesInside()
	{
		// Atom authors inside RSS: author/title must not become the item title.
		// https://github.com/brentsimmons/NetNewsWire/issues/943
		var feed = Parse(ParserData("cloudblog", "rss", "https://cloudblog.withgoogle.com/"));
		foreach (var article in feed.Items)
		{
			Assert.NotEqual("Product Manager, Office of the CTO", article.Title);
			Assert.NotEqual("Developer Programs Engineer", article.Title);
			Assert.NotEqual("Product Director", article.Title);
		}
	}

	[Fact]
	public void TitlesWithImageElementsInsideItems()
	{
		// This invalid feed has <image> elements inside <item>s.
		var feed = Parse(ParserData("aktuality", "rss", "https://www.aktuality.sk/"));
		Assert.All(feed.Items, article => Assert.NotNull(article.Title));
	}

	[Fact]
	public void FeedLanguage() =>
		Assert.Equal("en-US", Parse(ParserData("manton", "rss", "http://manton.org/")).Language);

	[Theory]
	[InlineData("markdown1", "https://wordland.social/scripting/237777565/rss.xml")]
	[InlineData("markdown2", "https://wordland.social/scripting/246529703/rss.xml")]
	public void SourceMarkdown(string filename, string url)
	{
		var feed = Parse(ParserData(filename, "rss", url));
		Assert.All(feed.Items, article => Assert.NotNull(article.Markdown));
	}

	[Fact]
	public void FeedIconUrl() =>
		Assert.Equal("https://static.feedpress.it/logo/katiefloyd.png", Parse(ParserData("KatieFloyd", "rss", "http://katiefloyd.com/")).IconUrl);

	[Fact]
	public void FeedIconUrlNotSetByItemLevelImages() =>
		Assert.Null(Parse(ParserData("aktuality", "rss", "https://www.aktuality.sk/")).IconUrl);

	[Fact]
	public void MedscapeExternalUrls()
	{
		var feed = Parse(ParserData("medscape", "rss", "https://www.medscape.com/cx/rssfeeds/2674.xml"));
		Assert.All(feed.Items, article => Assert.NotNull(article.ExternalUrl));
	}

	[Fact]
	public void FeedWithGb2312Encoding()
	{
		// https://github.com/Ranchero-Software/NetNewsWire/issues/1477
		var feed = Parse(ParserData("kc0011", "rss", "http://kc0011.net/"));
		Assert.NotEmpty(feed.Items);
		Assert.All(feed.Items, article => Assert.NotNull(article.ContentHtml));
	}

	[Fact]
	public void CdataBodyPreservesHtmlNamedEntities()
	{
		// CDATA is raw: `&nbsp;` stays literal and is never decoded to U+00A0.
		var feed = Parse(ParserData("KatieFloyd", "rss", "http://katiefloyd.com/"));
		Assert.Contains(feed.Items, item => item.ContentHtml?.Contains("&nbsp;") == true);
		Assert.All(feed.Items, item => Assert.DoesNotContain(" ", item.ContentHtml ?? ""));
	}

	[Fact]
	public void CdataBodyPreservesNumericEntities()
	{
		var feed = Parse(ParserData("DaringFireball", "rss", "http://daringfireball.net/"));
		Assert.Contains(feed.Items, item => item.ContentHtml?.Contains("&#8217;") == true);
	}

	[Fact]
	public void Gb2312FeedDecodesChineseCorrectly()
	{
		// Declared gb2312, really GBK. Strict GB 2312-80 or Latin-1 would garble it.
		var feed = Parse(ParserData("kc0011", "rss", "http://kc0011.net/"));
		Assert.Equal("投资资讯网交易在线--流通纪念币最新20篇论坛主题-全文", feed.Title);
	}

	[Fact]
	public void CjkItemTitlesPreserveMultiByteUtf8()
	{
		var feed = Parse(ParserData("kc0011", "rss", "http://kc0011.net/"));
		var sawChinese = false;
		foreach (var title in feed.Items.Select(item => item.Title).OfType<string>())
		{
			sawChinese |= title.Any(c => c is >= '一' and <= '鿿');
			Assert.DoesNotContain("�", title);
		}
		Assert.True(sawChinese);
	}

	[Fact]
	public void RdfFeedParsesAsRss()
	{
		// RSS 1.0: each item's rdf:about is its uniqueID and permalink, keeping IDs stable across syncs.
		var feed = Parse(ParserData("bio", "rdf", "http://biorxiv.org/"));
		Assert.Equal("bioRxiv Subject Collection: Plant Biology", feed.Title);
		Assert.Equal("http://biorxiv.org", feed.HomePageUrl);
		Assert.Equal(30, feed.Items.Count);
		foreach (var item in feed.Items)
		{
			Assert.NotNull(item.Title);
			Assert.NotNull(item.Url);
			Assert.StartsWith("http://biorxiv.org/cgi/content/", item.UniqueId);
		}
	}

	[Fact]
	public void MacworldCalculatedUniqueIdsArePinnedMd5s()
	{
		// Byte-stability pin: a change here would turn every existing article into a new one.
		var feed = Parse(ParserData("macworld", "rss", "https://www.macworld.com/"));
		var expected = new Dictionary<string, string>
		{
			["Black Friday Exclusive: Save Over 40% On The Nix Mini Color Sensor - Deal Alert"] = "7ee68b71282bbf0f9a203f5c362e048a",
			["The iMac Pro might be the first ARM Mac, but it won’t be the last"] = "d2d30947445157657f425924d96fd139",
			["Cyber Monday Steal: Get 120+ Hours of iOS 11 Dev Training for $15"] = "1da507ba09dfd933733211e182f95db7",
		};
		foreach (var (title, expectedId) in expected)
		{
			var item = Assert.Single(feed.Items, i => i.Title == title);
			Assert.Equal(expectedId, item.UniqueId);
		}
	}

	[Fact]
	public void DaringFireballRssItemIsByteStable()
	{
		var feed = Parse(ParserData("DaringFireball", "rss", "http://daringfireball.net/"));
		var item = Assert.Single(feed.Items, i => i.Title == "The Dangerous All Writs Act Precedent in the Apple Encryption Case");
		Assert.Equal("tag:daringfireball.net,2016:/linked//6.32163", item.UniqueId);
		Assert.Equal(UnixSeconds(1_456_420_033), item.DatePublished); // 2016-02-25 17:07:13 UTC
		var html = item.ContentHtml!;
		Assert.Equal(1360, GraphemeCount(html));
		Assert.StartsWith("<p>Amy Davidson, writing for The New Yorker:</p>", html);
		Assert.Contains("“all writs necessary", html);
	}

	[Fact]
	public void EmarleyAuthorIsNameOnly()
	{
		var feed = Parse(ParserData("EMarley", "rss", "https://emarley.com/"));
		var author = feed.Items.First().Authors!.First();
		Assert.Equal("Liz Marley", author.Name);
		Assert.Null(author.EmailAddress);
		Assert.Null(author.Url);
	}

	[Fact]
	public void NonStandardSlashDateFormatParses()
	{
		// kc0011 uses `YYYY/M/D HH:MM:SS` pubDates (Dvbbs.Net-style).
		var feed = Parse(ParserData("kc0011", "rss", "http://kc0011.net/"));
		var jan10 = Utc(2020, 1, 10);
		Assert.Contains(feed.Items, item => item.DatePublished >= jan10 && item.DatePublished < jan10.AddDays(1));
	}

	[Fact]
	public void ShopifyNamespaceDoesNotOverwriteTitle()
	{
		// https://github.com/Ranchero-Software/NetNewsWire/issues/4422
		var item = Assert.Single(Parse(ParserData("shopify-namespace", "rss", "https://example.com/feed")).Items);
		Assert.Equal("Real Product Title", item.Title);
		Assert.Equal("<p>Body HTML</p>", item.ContentHtml);
		Assert.Equal("Jane Maker", item.Authors!.First().Name);
	}

	[Fact]
	public void SlashdotTextInputDoesNotOverwriteFeedTitle()
	{
		var feed = Parse(ParserData("slashdot", "rss", "https://rss.slashdot.org/Slashdot/slashdotMain"));
		Assert.Equal("Slashdot", feed.Title);
		Assert.Equal("https://slashdot.org/", feed.HomePageUrl);
		Assert.Equal(15, feed.Items.Count);
	}

	[Fact]
	public void TextInputInsideChannelIsIgnored()
	{
		var feed = Parse(ParserData("textinput-rss2", "rss", "https://example.com/feed"));
		Assert.Equal("Test Feed", feed.Title);
		Assert.Equal("https://example.com", feed.HomePageUrl);
		var item = Assert.Single(feed.Items);
		Assert.Equal("Real Article Title", item.Title);
		Assert.Equal("https://example.com/articles/1", item.Url);
	}
}
