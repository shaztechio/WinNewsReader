using static WinNewsReader.Parser.Tests.TestHelpers;

namespace WinNewsReader.Parser.Tests;

/// <summary>Ported from NetNewsWire's AtomParserTests.</summary>
public class AtomParserTests
{
	[Theory]
	[InlineData("allthis", "http://leancrew.com/all-this", "http://leancrew.com/all-this")]
	[InlineData("qemu", "https://www.qemu.org/feed.xml", "https://www.qemu.org/")]
	[InlineData("yakubin", "https://yakubin.com/notes/atom.xml", "https://yakubin.com/notes")]
	[InlineData("4fsodonline", "http://4fsodonline.blogspot.com/feeds/posts/default", "http://4fsodonline.blogspot.com/")]
	[InlineData("DaringFireball", "https://daringfireball.net/feeds/main", "https://daringfireball.net/")]
	[InlineData("neverworkintheory", "https://neverworkintheory.org/atom.xml", "https://neverworkintheory.org/")]
	public void HomePageLink(string file, string feedUrl, string expectedHomePage) =>
		Assert.Equal(expectedHomePage, Parse(ParserData(file, "atom", feedUrl)).HomePageUrl);

	[Theory]
	[InlineData("qemu", "https://www.qemu.org/feed.xml", "QEMU version 10.1.0 released", "https://www.qemu.org/2025/08/26/qemu-10-1-0/")]
	[InlineData("DaringFireball", "https://daringfireball.net/feeds/main", "Virgin Mobile Partners With Apple to Go iPhone-Only With $1 Service", "https://daringfireball.net/linked/2017/06/26/virgin-mobile-iphone-only")]
	[InlineData("neverworkintheory", "https://neverworkintheory.org/atom.xml", "Andreas Zeller on Creating Nasty Test Inputs", "https://neverworkintheory.org/2023/06/13/zeller-andreas.html")]
	public void ArticlePermalinks(string file, string feedUrl, string title, string expectedUrl)
	{
		var item = Assert.Single(Parse(ParserData(file, "atom", feedUrl)).Items, i => i.Title == title);
		Assert.Equal(expectedUrl, item.Url);
	}

	[Fact]
	public void XmlBaseResolvesRelativeUrlsInXhtmlContent()
	{
		var feed = Parse(ParserData("pappacoda", "atom", "https://andrea.pappacoda.it/blog/feed.atom"));
		Assert.Equal("https://andrea.pappacoda.it/assets/debian-button.gif", feed.IconUrl);
		Assert.Equal("https://andrea.pappacoda.it/andrea_pappacoda.jpg", feed.FaviconUrl);
		var item = Assert.Single(feed.Items, i => i.Title == "C su Windows");
		Assert.Equal("tag:andrea.pappacoda.it,2023-06-09:c_su_windows", item.UniqueId);
		Assert.Equal("https://andrea.pappacoda.it/blog/c_su_windows/", item.Url);
		var html = item.ContentHtml!;
		Assert.Contains("src=\"https://andrea.pappacoda.it/blog/c_su_windows/c_su_windows_visual_studio_componenti.png\"", html);
		Assert.Contains("srcset=\"https://andrea.pappacoda.it/blog/c_su_windows/c_su_windows_visual_studio_componenti_light.png\"", html);
		Assert.Contains("src=\"https://andrea.pappacoda.it/blog/c_su_windows/clangd_path.png\"", html);
		Assert.DoesNotContain("src=\"clangd_path.png\"", html);
		Assert.Contains("xmlns=\"http://www.w3.org/1999/xhtml\"", html);
	}

	[Fact]
	public void NestedRelativeXmlBase()
	{
		const string xml = """
			<?xml version="1.0" encoding="utf-8"?>
			<feed xmlns="http://www.w3.org/2005/Atom" xml:base="https://example.com/blog/">
			  <title>t</title><id>urn:t</id>
			  <link rel="alternate" href="https://homepage.example.org/"/>
			  <entry>
			    <id>urn:t:1</id>
			    <title>nested base</title>
			    <updated>2020-05-01T00:00:00Z</updated>
			    <link rel="alternate" href="post1/"/>
			    <link rel="related" xml:base="https://elsewhere.example.net/dir/" href="related.html"/>
			    <content type="html" xml:base="post1/">&lt;a href="pic.png"&gt;pic&lt;/a&gt;</content>
			  </entry>
			</feed>
			""";
		var item = Assert.Single(Parse(ParserData(xml, "https://example.com/blog/feed.atom")).Items);
		Assert.Equal("https://example.com/blog/post1/", item.Url);
		Assert.Equal("https://elsewhere.example.net/dir/related.html", item.ExternalUrl);
		Assert.Contains("href=\"https://example.com/blog/post1/pic.png\"", item.ContentHtml);
	}

	[Fact]
	public void MimeTypeHtmlContentWithXmlBaseResolved()
	{
		const string xml = """
			<?xml version="1.0" encoding="utf-8"?>
			<feed xmlns="http://www.w3.org/2005/Atom">
			  <title>t</title><id>urn:t</id>
			  <entry>
			    <id>urn:t:1</id>
			    <title>mime type html</title>
			    <updated>2020-05-01T00:00:00Z</updated>
			    <content type="text/html" xml:base="https://example.com/post/">&lt;img src="pic.png"&gt;</content>
			  </entry>
			</feed>
			""";
		var item = Assert.Single(Parse(ParserData(xml, "https://example.com/feed.atom")).Items);
		Assert.Contains("src=\"https://example.com/post/pic.png\"", item.ContentHtml);
	}

	[Fact]
	public void RelativeXmlBaseResolvesAgainstFeedUrl()
	{
		const string xml = """
			<?xml version="1.0" encoding="utf-8"?>
			<feed xmlns="http://www.w3.org/2005/Atom">
			  <title>t</title><id>urn:t</id>
			  <entry>
			    <id>/2024/04/23/qemu-9-0-0</id>
			    <title>release</title>
			    <updated>2024-04-23T23:02:00Z</updated>
			    <content type="html" xml:base="/2024/04/23/qemu-9-0-0/">&lt;a href="changelog.html"&gt;changelog&lt;/a&gt;</content>
			  </entry>
			</feed>
			""";
		var item = Assert.Single(Parse(ParserData(xml, "https://www.qemu.org/feed.xml")).Items);
		Assert.Equal("/2024/04/23/qemu-9-0-0", item.UniqueId);
		Assert.Contains("href=\"https://www.qemu.org/2024/04/23/qemu-9-0-0/changelog.html\"", item.ContentHtml);
	}

	[Fact]
	public void FragmentAndEmptyHrefsSurviveXmlBase()
	{
		var feed = Parse(ParserData("OneFootTsunami", "atom", "http://onefoottsunami.com/feed/atom/"));
		Assert.Contains(feed.Items, item => item.ContentHtml?.Contains("href=\"#fn") == true);
		Assert.DoesNotContain(feed.Items, item => item.Url?.EndsWith("wp-atom.php") == true);
	}

	[Fact]
	public void SummaryWithXmlBaseResolved()
	{
		const string xml = """
			<?xml version="1.0" encoding="utf-8"?>
			<feed xmlns="http://www.w3.org/2005/Atom">
			  <title>t</title><id>urn:t</id>
			  <entry>
			    <id>urn:t:1</id>
			    <title>summary only</title>
			    <updated>2020-05-01T00:00:00Z</updated>
			    <summary type="html" xml:base="https://example.com/post/">&lt;img src="pic.png"&gt;</summary>
			  </entry>
			</feed>
			""";
		var item = Assert.Single(Parse(ParserData(xml, "https://example.com/feed.atom")).Items);
		Assert.Contains("src=\"https://example.com/post/pic.png\"", item.ContentHtml);
	}

	[Fact]
	public void RelativeAuthorUriResolvesAgainstXmlBase()
	{
		const string xml = """
			<?xml version="1.0" encoding="utf-8"?>
			<feed xmlns="http://www.w3.org/2005/Atom" xml:base="https://example.com/blog/">
			  <title>t</title><id>urn:t</id>
			  <entry>
			    <id>urn:t:1</id>
			    <title>relative author uri</title>
			    <updated>2020-05-01T00:00:00Z</updated>
			    <author><name>Alice</name><uri>about/</uri></author>
			  </entry>
			</feed>
			""";
		var item = Assert.Single(Parse(ParserData(xml, "https://example.com/blog/feed.atom")).Items);
		Assert.Equal("https://example.com/blog/about/", item.Authors!.First().Url);
	}

	[Fact]
	public void TextContentWithXmlBaseUntouched()
	{
		const string xml = """
			<?xml version="1.0" encoding="utf-8"?>
			<feed xmlns="http://www.w3.org/2005/Atom">
			  <title>t</title><id>urn:t</id>
			  <entry>
			    <id>urn:t:1</id>
			    <title>text content</title>
			    <updated>2020-05-01T00:00:00Z</updated>
			    <content type="text" xml:base="https://example.com/post/">See src="pic.png" for details.</content>
			  </entry>
			</feed>
			""";
		var item = Assert.Single(Parse(ParserData(xml, "https://example.com/feed.atom")).Items);
		Assert.Contains("src=\"pic.png\"", item.ContentHtml);
	}

	[Fact]
	public void ArticleExternalLinks()
	{
		var df = Parse(ParserData("DaringFireball", "atom", "https://daringfireball.net/feeds/main"));
		var item = Assert.Single(df.Items, i => i.Title == "Kara Swisher: ‘Susan Fowler Proved That One Person Can Make a Difference’");
		Assert.Equal("https://www.recode.net/2017/6/21/15844852/uber-toxic-bro-company-culture-susan-fowler-blog-post", item.ExternalUrl);

		var qemu = Parse(ParserData("qemu", "atom", "https://www.qemu.org/feed.xml"));
		Assert.NotEmpty(qemu.Items);
		Assert.All(qemu.Items, i => Assert.Null(i.ExternalUrl));
	}

	[Fact]
	public void DaringFireball()
	{
		var feed = Parse(ParserData("DaringFireball", "atom", "https://daringfireball.net/feeds/main"));
		foreach (var article in feed.Items)
		{
			Assert.NotNull(article.Url);
			Assert.StartsWith("tag:daringfireball.net,2017:/", article.UniqueId);
			var author = Assert.Single(article.Authors!);
			if (author.Name == "Daring Fireball Department of Commerce")
			{
				Assert.Null(author.Url);
			}
			else
			{
				Assert.Equal("John Gruber", author.Name);
				Assert.Equal("http://daringfireball.net/", author.Url);
			}
			Assert.NotNull(article.DatePublished);
			Assert.Null(article.Attachments);
			Assert.Equal("en", article.Language);
		}
	}

	[Fact]
	public void FsodonlineAttachments()
	{
		var feed = Parse(ParserData("4fsodonline", "atom", "http://4fsodonline.blogspot.com/"));
		foreach (var article in feed.Items)
		{
			Assert.NotNull(article.Attachments);
			Assert.NotEmpty(article.Attachments);
			var attachment = article.Attachments[0];
			Assert.StartsWith("http://www.blogger.com/video-play.mp4?", attachment.Url);
			Assert.Null(attachment.SizeInBytes);
			Assert.Equal("video/mp4", attachment.MimeType);
		}
	}

	[Fact]
	public void ExpertOpinionEntAttachments()
	{
		var feed = Parse(ParserData("expertopinionent", "atom", "http://expertopinionent.typepad.com/my-blog/"));
		foreach (var attachments in feed.Items.Select(item => item.Attachments).OfType<IReadOnlyList<WinNewsReader.Parser.Feeds.ParsedAttachment>>())
		{
			var attachment = Assert.Single(attachments);
			Assert.EndsWith(".mp3", attachment.Url);
			Assert.Null(attachment.SizeInBytes);
			Assert.Equal("audio/mpeg", attachment.MimeType);
		}
	}

	[Fact]
	public void FeedIconUrl() =>
		Assert.Equal("https://fvsch.com/assets/images/icon.png?v=ql0r5y", Parse(ParserData("root-author", "atom", "https://fvsch.com/feed.xml")).IconUrl);

	[Fact]
	public void AuthorAtRoot()
	{
		var feed = Parse(ParserData("root-author", "atom", "https://fvsch.com/feed.xml"));
		foreach (var article in feed.Items)
		{
			var author = article.Authors!.First();
			Assert.Equal("Florens Verschelde", author.Name);
			Assert.Null(author.Url);
			Assert.Null(author.AvatarUrl);
			Assert.Null(author.EmailAddress);
		}
	}

	[Fact]
	public void XhtmlContentCapturedAsRawBytes()
	{
		var feed = Parse(ParserData("expertopinionent", "atom", "http://expertopinionent.typepad.com/my-blog/"));
		var bodies = feed.Items.Select(item => item.ContentHtml).OfType<string>().ToList();
		Assert.Contains(bodies, body => body.Contains("xmlns=\"http://www.w3.org/1999/xhtml\""));
		Assert.Contains(bodies, body => body.Contains("<br />"));
	}

	[Fact]
	public void QemuAtomItemIsByteStable()
	{
		var feed = Parse(ParserData("qemu", "atom", "https://www.qemu.org/feed.xml"));
		var item = Assert.Single(feed.Items, i => i.Title == "QEMU version 9.0.0 released");
		Assert.Equal("/2024/04/23/qemu-9-0-0", item.UniqueId);
		Assert.Equal(UnixSeconds(1_713_913_320), item.DatePublished); // 2024-04-23 23:02:00 UTC
		var html = item.ContentHtml!;
		Assert.Equal(2221, GraphemeCount(html));
		Assert.StartsWith("<p>We’d like to announce the availability of the QEMU 9.0.0 release", html);
		Assert.Contains("2700+ commits from 220 authors", html);
	}

	[Fact]
	public void QemuAtomSecondItemDateParses()
	{
		var feed = Parse(ParserData("qemu", "atom", "https://www.qemu.org/feed.xml"));
		var item = Assert.Single(feed.Items, i => i.Title == "QEMU version 9.1.0 released");
		Assert.Equal(UnixSeconds(1_725_404_880), item.DatePublished); // 2024-09-03 23:08:00 UTC
	}

	[Fact]
	public void SelfClosingLinkAccepted()
	{
		var item = Assert.Single(Parse(ParserData(Entry("""<link rel="alternate" type="text/html" href="http://example.com/article"/>"""), "http://example.com/")).Items);
		Assert.Equal("http://example.com/article", item.Url);
	}

	[Fact]
	public void MultipleAuthorsPerEntry()
	{
		const string xml = """
			<?xml version="1.0" encoding="utf-8"?>
			<feed xmlns="http://www.w3.org/2005/Atom">
			  <title>Multi-author</title>
			  <link href="http://example.com/"/>
			  <id>urn:example:multi</id>
			  <entry>
			    <id>urn:example:multi:1</id>
			    <title>Joint post</title>
			    <updated>2020-05-01T00:00:00Z</updated>
			    <author><name>Alice</name></author>
			    <author><name>Bob</name><email>bob@example.com</email></author>
			    <author><name>Carol</name><uri>http://carol.example.com/</uri></author>
			  </entry>
			</feed>
			""";
		var authors = Assert.Single(Parse(ParserData(xml, "http://example.com/")).Items).Authors!;
		Assert.Equal(3, authors.Count);
		Assert.Equal(["Alice", "Bob", "Carol"], authors.Select(a => a.Name).Order());
		Assert.Contains(authors, a => a.EmailAddress == "bob@example.com");
		Assert.Contains(authors, a => a.Url == "http://carol.example.com/");
	}

	[Fact]
	public void ContentTypeHtml()
	{
		var item = Assert.Single(Parse(ParserData(Entry("""<content type="html">&lt;p&gt;Hello &amp; welcome&lt;/p&gt;</content>"""), "http://example.com/")).Items);
		Assert.Contains("<p>Hello & welcome</p>", item.ContentHtml);
	}

	[Fact]
	public void ContentTypeText()
	{
		var item = Assert.Single(Parse(ParserData(Entry("""<content type="text">Just plain &amp; text.</content>"""), "http://example.com/")).Items);
		Assert.Contains("Just plain & text.", item.ContentText ?? item.ContentHtml);
	}

	[Fact]
	public void SummaryAndContentBothPreserved()
	{
		var feed = Parse(ParserData("russcox", "atom", "http://research.swtch.com/feed.atom"));
		var item = Assert.Single(feed.Items, i => i.Title == "Transparent Logs for Skeptical Clients");
		Assert.Equal("How an untrusted server can publish a verifiably append-only log.", item.Summary);
		Assert.Contains("Suppose we want to maintain and publish a public, append-only log of data.", item.ContentHtml);
	}

	[Fact]
	public void SummaryOnlyPromotedToContentHtml()
	{
		var item = Assert.Single(Parse(ParserData(Entry("<summary>Just a summary.</summary>"), "http://example.com/")).Items);
		Assert.Equal("Just a summary.", item.ContentHtml);
		Assert.Null(item.Summary);
	}

	[Fact]
	public void ContentOnlyLeavesSummaryNull()
	{
		var item = Assert.Single(Parse(ParserData(Entry("<content>Just content.</content>"), "http://example.com/")).Items);
		Assert.Equal("Just content.", item.ContentHtml);
		Assert.Null(item.Summary);
	}

	[Fact]
	public void SummaryAndContentKeptSeparate()
	{
		var item = Assert.Single(Parse(ParserData(Entry("<summary>The summary.</summary><content>The content.</content>"), "http://example.com/")).Items);
		Assert.Equal("The content.", item.ContentHtml);
		Assert.Equal("The summary.", item.Summary);
	}

	[Fact]
	public void XhtmlSummaryCapturedAsRawBytes()
	{
		var entry = """<summary type="xhtml"><div xmlns="http://www.w3.org/1999/xhtml"><p>Hello <em>world</em>.</p></div></summary><content>Body.</content>""";
		var item = Assert.Single(Parse(ParserData(Entry(entry), "http://example.com/")).Items);
		Assert.Equal("Body.", item.ContentHtml);
		Assert.Contains("<p>Hello <em>world</em>.</p>", item.Summary);
	}

	[Fact]
	public void ContentTypeDefaultsToText()
	{
		var item = Assert.Single(Parse(ParserData(Entry("<content>Default is text.</content>"), "http://example.com/")).Items);
		Assert.Contains("Default is text.", item.ContentText ?? item.ContentHtml);
	}

	[Fact]
	public void ShopifyNamespaceDoesNotOverwriteTitle()
	{
		var feed = Parse(ParserData("draw-down", "atom", "https://draw-down.com/collections/magazines.atom"));
		Assert.Equal(25, feed.Items.Count);
		Assert.DoesNotContain(feed.Items, item => item.Title == "Default Title");
		var product = Assert.Single(feed.Items, i => i.UniqueId == "https://draw-down.com/products/9225111929086");
		Assert.Equal("Revue Faire — no. 56 (Reading Until I Sleep: Manuel Raeder)", product.Title);
	}

	[Fact]
	public void YouTubeMediaGroup()
	{
		var feed = Parse(ParserData("youtube", "atom", "https://www.youtube.com/feeds/videos.xml?channel_id=UCexamplechannel000000"));
		var item = feed.Items.First();
		Assert.Equal("Real Video Title", item.Title);
		Assert.Equal("https://www.youtube.com/watch?v=abcdefghijk", item.Url);
		Assert.Equal("https://i1.ytimg.com/vi/abcdefghijk/hqdefault.jpg", item.ThumbnailUrl);
		Assert.Null(item.ContentHtml);
		Assert.Equal("The video’s description.", item.ContentText);
		var attachment = Assert.Single(item.Attachments!);
		Assert.Equal("https://www.youtube.com/v/abcdefghijk?version=3", attachment.Url);
		Assert.Equal("application/x-shockwave-flash", attachment.MimeType);
		Assert.Equal(640, attachment.Width);
		Assert.Equal(390, attachment.Height);
	}

	/// <summary>A one-entry Atom feed around <paramref name="entryBody"/>.</summary>
	private static string Entry(string entryBody) => $"""
		<?xml version="1.0" encoding="utf-8"?>
		<feed xmlns="http://www.w3.org/2005/Atom">
		  <title>t</title><id>urn:t</id>
		  <entry>
		    <id>urn:t:1</id>
		    <title>entry</title>
		    <updated>2020-05-01T00:00:00Z</updated>
		    {entryBody}
		  </entry>
		</feed>
		""";
}
