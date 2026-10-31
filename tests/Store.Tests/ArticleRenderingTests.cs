using WinNewsReader.Core.ArticleRendering;
using WinNewsReader.Parser.Feeds;

namespace WinNewsReader.Store.Tests;

/// <summary>NetNewsWire's MacroProcessorTests.</summary>
public class MacroProcessorTests
{
	private static readonly Dictionary<string, string> Substitutions = new() { ["one"] = "1", ["two"] = "2" };

	[Theory]
	[InlineData("foo [[one]] bar [[two]] baz", "foo 1 bar 2 baz")]
	[InlineData("[[one]] foo [[two]] bar", "1 foo 2 bar")]
	[InlineData("foo [[one]] bar [[two]]", "foo 1 bar 2")]
	[InlineData("foo [[nonexistent]] bar", "foo [[nonexistent]] bar")]
	public void Substitutes(string template, string expected) => Assert.Equal(expected, MacroProcessor.RenderedText(template, Substitutions));

	[Fact]
	public void EqualDelimiters() => Assert.Equal("foo 1 bar 2 baz", MacroProcessor.RenderedText("foo |one| bar |two| baz", Substitutions, "|", "|"));

	[Fact]
	public void EmptyDelimitersThrow()
	{
		Assert.Throws<ArgumentException>(() => MacroProcessor.RenderedText("foo bar", Substitutions, macroStart: ""));
		Assert.Throws<ArgumentException>(() => MacroProcessor.RenderedText("foo bar", Substitutions, macroEnd: ""));
	}

	[Fact]
	public void ReplacementIsNotRecursive() =>
		Assert.Equal("foo [[two]] bar", MacroProcessor.RenderedText("foo [[one]] bar", new Dictionary<string, string> { ["one"] = "[[two]]", ["two"] = "2" }));
}

/// <summary>NetNewsWire's PlainTextToHTMLTests and SanitizedTitleTests.</summary>
public class ArticleTextTests
{
	[Fact]
	public void YouTubeDescription()
	{
		var text = """
			HomeView recognizes up to six people & keeps <everyone> in the loop.

			Learn more and shop Apple HomeView: https://apple.co/4s4jhly
			Collect 200 by Collect 200: https://music.apple/collect-200

			Siri AI is available in beta.
			""";
		var expected = """
			<p>HomeView recognizes up to six people &amp; keeps &lt;everyone&gt; in the loop.</p>
			<p>Learn more and shop Apple HomeView: <a href="https://apple.co/4s4jhly">https://apple.co/4s4jhly</a><br>
			Collect 200 by Collect 200: <a href="https://music.apple/collect-200">https://music.apple/collect-200</a></p>
			<p>Siri AI is available in beta.</p>
			""";
		Assert.Equal(expected.ReplaceLineEndings("\n"), ArticleText.PlainTextToHtml(text.ReplaceLineEndings("\n")));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void PlainEmptyAndNilTitles(bool forHtml)
	{
		Assert.Equal("Hello World", ArticleText.SanitizedTitle("Hello World", forHtml));
		Assert.Equal("", ArticleText.SanitizedTitle("", forHtml));
		Assert.Null(ArticleText.SanitizedTitle(null, forHtml));
		Assert.Equal("Café résumé with emoji 🎉", ArticleText.SanitizedTitle("Café résumé with emoji 🎉", forHtml));
		Assert.Equal("foo ", ArticleText.SanitizedTitle("foo <", forHtml));
	}

	[Theory]
	// Angle-bracketed text that isn't HTML displays as written.
	[InlineData("<hello-world>", true, "&lt;hello-world&gt;")]
	[InlineData("<hello-world>", false, "<hello-world>")]
	[InlineData("It All Starts with a Humble <textarea>", true, "It All Starts with a Humble &lt;textarea&gt;")]
	// Allowed tags.
	[InlineData("Use <b>bold</b>", true, "Use <b>bold</b>")]
	[InlineData("Use <b>bold</b>", false, "Use bold")]
	[InlineData("<b>Bold</b> and <i>italic</i>", true, "<b>Bold</b> and <i>italic</i>")]
	[InlineData("<b>Bold</b> and <i>italic</i>", false, "Bold and italic")]
	// Disallowed tags.
	[InlineData("Use <script>evil</script>", true, "Use &lt;script&gt;evil&lt;/script&gt;")]
	[InlineData("Use <script>evil</script>", false, "Use <script>evil</script>")]
	// Tags are matched by name, with attributes ignored.
	[InlineData("[<abbr title=\"Not Safe For Work\">NSFW</abbr>]", true, "[<abbr title=\"Not Safe For Work\">NSFW</abbr>]")]
	[InlineData("[<abbr title=\"Not Safe For Work\">NSFW</abbr>]", false, "[NSFW]")]
	[InlineData("<cite id=\"x\">Book</cite>", true, "<cite id=\"x\">Book</cite>")]
	[InlineData("<cite id=\"x\">Book</cite>", false, "Book")]
	[InlineData("X<b/>Y", true, "X<b/>Y")]
	[InlineData("X<b/>Y", false, "XY")]
	[InlineData("X<br/>Y", true, "X&lt;br/&gt;Y")]
	[InlineData("X<br/>Y", false, "X<br/>Y")]
	// An unclosed tag gets no closing > added.
	[InlineData("foo <bar", false, "foo <bar")]
	[InlineData("foo <bar", true, "foo &lt;bar")]
	[InlineData("No place in children's hands: <16s in UK to be banned", true, "No place in children's hands: &lt;16s in UK to be banned")]
	[InlineData("No place in children's hands: <16s in UK to be banned", false, "No place in children's hands: <16s in UK to be banned")]
	[InlineData("<b>Café</b>", true, "<b>Café</b>")]
	[InlineData("<b>Café</b>", false, "Café")]
	public void SanitizedTitle(string title, bool forHtml, string expected) => Assert.Equal(expected, ArticleText.SanitizedTitle(title, forHtml));
}

/// <summary>NetNewsWire's EnclosuresHTMLTests.</summary>
public class EnclosuresHtmlTests
{
	[Fact]
	public void PodcastEpisodeGetsAudioPlayerAndArtwork()
	{
		var attachment = ParsedAttachment.Create("http://traffic.libsyn.com/atpfm/atp311.mp3", "audio/mpeg", null, 54919661, 6849, medium: AttachmentMedium.Audio)!;
		const string body = "<ul><li>Facebook’s Onavo scam</li><li>Apple’s FaceTime bug</li></ul>";
		const string artworkUrl = "http://static1.squarespace.com/static/513abd71e4b0fe58c655c105/t/52c45a37e4b0a77a5034aa84/1388599866232/1500w/Artwork.jpg";

		var html = ArticleRenderer.EnclosuresHtml([attachment], body, artworkUrl);

		Assert.Contains("<audio controls preload=\"none\" src=\"http://traffic.libsyn.com/atpfm/atp311.mp3\"></audio>", html);
		Assert.Contains("atp311.mp3 · 1:54:09 · 54.9 MB", html);
		Assert.Contains($"<img src=\"{artworkUrl}\" alt=\"\">", html);
	}

	[Fact]
	public void ImageEnclosureAlreadyInBodyIsSkipped()
	{
		const string imageUrl = "https://storage.googleapis.com/gweb-cloudblog-publish/images/hero.max-1000x1000.png?w=1000&h=500";
		var attachment = ParsedAttachment.Create(imageUrl, width: 1000, height: 500, medium: AttachmentMedium.Image)!;
		const string body = "<p><img src=\"https://storage.googleapis.com/gweb-cloudblog-publish/images/hero.max-1000x1000.png?w=1000&amp;h=500\"></p>";

		Assert.Empty(ArticleRenderer.EnclosuresHtml([attachment], body, imageUrl));
	}

	[Fact]
	public void YouTubeFlashUrlBecomesEmbed()
	{
		var attachment = ParsedAttachment.Create("https://www.youtube.com/v/dQw4w9WgXcQ?version=3", width: 640, height: 390)!;
		Assert.Contains("<iframe src=\"https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ\" width=\"640\" height=\"390\" allowfullscreen>", ArticleRenderer.EnclosuresHtml([attachment], "", null));
	}
}

/// <summary>NetNewsWire's ArticleRenderingSpecialCasesTests.</summary>
public class ArticleRenderingSpecialCasesTests
{
	[Fact]
	public void RedirectAssignmentIsRemoved()
	{
		var filtered = ArticleRenderingSpecialCases.FilterHtmlIfNeeded("https://example.com/post/", "<p>Before</p><script>location.href='https://example.com/full/';</script><p>After</p>");
		Assert.DoesNotContain("location.href", filtered);
		Assert.Contains("<p>Before</p>", filtered);
		Assert.Contains("<p>After</p>", filtered);
	}

	[Fact]
	public void SpacedAssignmentAndWindowLocationRemoved()
	{
		var filtered = ArticleRenderingSpecialCases.FilterHtmlIfNeeded("https://example.com/a/", "<script> window.location.href = 'https://example.com/x/' ;</script>KEEP");
		Assert.DoesNotContain("location.href", filtered);
		Assert.Contains("KEEP", filtered);
	}

	[Theory]
	[InlineData("<script>if (location.href.includes('x')) { doThing(); }</script>")]
	[InlineData("<script>if (location.href == 'x') { doThing(); }</script>")]
	[InlineData("<p>Just an article.</p><script>console.log('hi');</script>")]
	public void ReadsComparisonsAndOtherScriptsAreKept(string html) => Assert.Equal(html, ArticleRenderingSpecialCases.FilterHtmlIfNeeded("https://example.com/a/", html));

	[Theory]
	[InlineData("https://yro.slashdot.org/story/26/09/18/0023251/will-california-gut-its-net-neutrality-law?utm_source=rss1.0mainlinkanon&utm_medium=feed", null, true)]
	[InlineData("https://slashdot.org/", null, true)]
	[InlineData(null, "https://rss.slashdot.org/Slashdot/slashdotMain", true)]
	[InlineData("https://example.com/post/", "https://example.com/feed.xml", false)]
	[InlineData("https://notslashdot.org/story/", null, false)]
	[InlineData(null, null, false)]
	public void JavaScriptDisabledByDomain(string? link, string? feedUrl, bool expected) =>
		Assert.Equal(expected, ArticleRenderingSpecialCases.ShouldDisableJavaScript(link, feedUrl, null));

	[Fact]
	public void SlashdotArticleBodyGetsParagraphTags()
	{
		const string paragraph1 = "An anonymous reader quotes a report from Reuters: A growing number of people think social media manipulates and divides people and harms democracy, Pew Research Center said on Thursday, echoing rising concern and tighter scrutiny across the world.";
		const string paragraph2 = "Australia has proposed rules to let users opt out of algorithmically recommended feeds, after becoming the first country to ban social media for children under 16.";
		const string shareLinks = "<p><div class=\"share_submission\" style=\"position:relative;\">\n<a class=\"slashpop\" href=\"http://twitter.com/home?status=x\"><img src=\"https://a.fsdn.com/sd/twitter_icon_large.png\"></a>";
		const string readMore = "</div></p><p><a href=\"https://tech.slashdot.org/story/26/10/02/0632243/?utm_source=rss1.0moreanon&amp;utm_medium=feed\">Read more of this story</a> at Slashdot.</p>";
		var html = paragraph1 + "\n \n" + paragraph2 + shareLinks + "\n\n\n\n" + readMore;

		var result = ArticleRenderingSpecialCases.InsertParagraphTagsIfNeeded(html, "https://rss.slashdot.org/Slashdot/slashdotMain", null);

		Assert.Equal(paragraph1 + "<p>" + paragraph2 + shareLinks + "<p>" + readMore, result);
	}

	[Fact]
	public void SlashdotHomePageGetsParagraphTagsForProxiedFeed() =>
		Assert.Equal("First paragraph.<p>Second paragraph.",
			ArticleRenderingSpecialCases.InsertParagraphTagsIfNeeded("First paragraph.\n\nSecond paragraph.", "https://feeds.feedburner.com/Slashdot/slashdot", "https://slashdot.org/"));

	[Fact]
	public void OtherFeedsDontGetParagraphTags() =>
		Assert.Equal("First paragraph.\n\nSecond paragraph.",
			ArticleRenderingSpecialCases.InsertParagraphTagsIfNeeded("First paragraph.\n\nSecond paragraph.", "https://example.com/feed.xml", "https://example.com/"));

	[Fact]
	public void MacRumorsListLosesStrayLineBreaks()
	{
		const string html = "GM said the rollout will occur as follows.\n<br/>\n\n<br/>\n<p><h2>September–October 2026</h2></p>\n<br/>\n<ul>\n<br/>\n  <li>Cadillac Vistiq (2027)</li>\n<br/>\n</ul>GM said customers will see a notification.";
		const string expected = "GM said the rollout will occur as follows.\n<br/>\n\n<br/>\n<p><h2>September–October 2026</h2></p>\n<br/>\n<ul>\n  <li>Cadillac Vistiq (2027)</li>\n</ul>GM said customers will see a notification.";
		Assert.Equal(expected, ArticleRenderingSpecialCases.RemoveStrayLineBreaksIfNeeded(html, "https://feeds.macrumors.com/MacRumors-All", null));
	}

	[Fact]
	public void MacRumorsTableLosesStrayLineBreaksButKeepsCellLineBreaks()
	{
		const string html = "<table class=\"specs\">\n<br/>\n<tr>\n<br/>\n<th>Height</th>\n<br/>\n<td>5.91 inches</td>\n<br/>\n<td>closed<br/>open</td>\n<br/>\n</tr>\n<br/>\n</table>\n<br/>\nAfter.";
		const string expected = "<table class=\"specs\">\n<tr>\n<th>Height</th>\n<td>5.91 inches</td>\n<td>closed<br/>open</td>\n</tr>\n</table>\n<br/>\nAfter.";
		Assert.Equal(expected, ArticleRenderingSpecialCases.RemoveStrayLineBreaksIfNeeded(html, null, "https://www.macrumors.com/"));
	}

	[Fact]
	public void OtherFeedsKeepLineBreaksInListsAndTables()
	{
		const string html = "<ul>\n<br/>\n<li>Item</li>\n<br/>\n</ul><table>\n<br/>\n<tr><td>Cell</td></tr></table>";
		Assert.Equal(html, ArticleRenderingSpecialCases.RemoveStrayLineBreaksIfNeeded(html, "https://example.com/feed.xml", "https://example.com/"));
	}

	[Fact]
	public void YouTubeArticleGetsNetNewsWireBaseUrl()
	{
		Assert.Equal("https://netnewswire.com/", ArticleRenderingSpecialCases.BaseUrlForRendering("https://www.youtube.com/watch?v=dQw4w9WgXcQ"));
		const string hackaday = "https://hackaday.com/2025/12/01/necroprinting-isnt-as-bad-as-it-sounds/";
		Assert.Equal(hackaday, ArticleRenderingSpecialCases.BaseUrlForRendering(hackaday));
	}

	[Fact]
	public void FreshRssContentGetsIdsAndClassesBack()
	{
		const string sanitized = "<div data-sanitized-class=\"footnotes\">\n\t<li data-sanitized-id=\"footnote-1\"><p>About `data-sanitized-class`.<a href=\"#footnote-1-ref\" data-sanitized-class=\"footnoteBackLink\">Back</a></p></li>\n</div>";
		const string expected = "<div class=\"footnotes\">\n\t<li id=\"footnote-1\"><p>About `data-sanitized-class`.<a href=\"#footnote-1-ref\" class=\"footnoteBackLink\">Back</a></p></li>\n</div>";
		Assert.Equal(expected, ArticleRenderingSpecialCases.DesanitizeFreshRssContent(sanitized));
	}

	[Fact]
	public void WholeDocumentContentRendersOnlyItsBody()
	{
		Assert.Equal("<p>Hi</p>", ArticleRenderingSpecialCases.ExtractBodyFragmentIfNeeded("<!doctype html><html lang=\"en\"><body class=\"x\"><p>Hi</p></body></html>"));
		Assert.Equal("<p>Hi</p>", ArticleRenderingSpecialCases.ExtractBodyFragmentIfNeeded("<html><head><title>T</title></head><p>Hi</p></html>"));
		Assert.Equal("<header>Kept</header>", ArticleRenderingSpecialCases.ExtractBodyFragmentIfNeeded("<header>Kept</header>"));
	}
}

/// <summary>The built-in themes and the renderer filling a template.</summary>
public class ArticleRendererTests
{
	private static readonly string ResourcesDirectory = Path.Combine(AppContext.BaseDirectory, "ArticleRendering", "Resources");

	private static readonly ArticleThemes Themes = new(ResourcesDirectory, Path.Combine(Path.GetTempPath(), "wnr-no-user-themes"));

	[Fact]
	public void BuiltInThemesLoad()
	{
		Assert.Equal(
			["Default", "Appanoose", "Biblioteca", "Hyperlegible", "NewsFax", "Promenade", "Sepia", "Tiqoe Dark", "Verdana Revival"],
			Themes.All.Select(theme => theme.Name));
		var sepia = Themes.Named("sepia");
		Assert.Equal("Ranchero Software", sepia.CreatorName);
		Assert.True(sepia.IsAppTheme);
		// Every stylesheet starts with core.css.
		Assert.All(Themes.All, theme => Assert.StartsWith(File.ReadAllText(Path.Combine(ResourcesDirectory, "core.css")), theme.Css));
		Assert.Same(Themes.Default, Themes.Named("No Such Theme"));
	}

	[Fact]
	public void ToastShowsTheMessageEscapedAndStylesItself()
	{
		var toasted = ArticleRenderer.WithToast(ArticleRenderer.NoSelection(Themes.Default), "Couldn’t load <b>x</b>");
		Assert.Contains("<div class='wnrToast' role='alert'>Couldn’t load &lt;b&gt;x&lt;/b&gt;</div>", toasted.Html);
		Assert.Contains("@keyframes wnrToast", toasted.Style);
	}

	[Fact]
	public void LoadingShowsASpinner()
	{
		var loading = ArticleRenderer.Loading(Themes.Default);
		Assert.Contains("class='wnrSpinner'", loading.Html);
		Assert.Contains("Loading…", loading.Html);
		Assert.Contains("@keyframes wnrSpin", loading.Style);
		Assert.Contains("Starting the browser…", ArticleRenderer.Loading(Themes.Default, "Starting the browser…").Html);
	}

	[Fact]
	public void NoSelectionSaysSo() => Assert.Contains("No Article Selected", ArticleRenderer.NoSelection(Themes.Default).Html);

	[Fact]
	public void MultipleSelectionIsAStackOfTheFirstFewArticles()
	{
		var articles = Enumerable.Range(1, 6).Select(i => ("Feed <b>", $"Article {i} & more")).ToList();

		var rendering = ArticleRenderer.MultipleSelection(Themes.Default, articles);

		Assert.Contains("6 Articles Selected", rendering.Html);
		Assert.Contains("Article 1 &amp; more", rendering.Html);
		Assert.Contains("Article 4 &amp; more", rendering.Html);
		Assert.DoesNotContain("Article 5", rendering.Html);
		Assert.DoesNotContain("<b>", rendering.Html);
		// The first article is drawn last, so it's on top.
		Assert.True(rendering.Html.IndexOf("Article 1 ", StringComparison.Ordinal) > rendering.Html.IndexOf("Article 2 ", StringComparison.Ordinal));
		Assert.Contains("height: 200px !important", rendering.Style);
	}

	[Fact]
	public void AppleFontsBecomeWindowsFonts()
	{
		Assert.All(Themes.All, theme => Assert.DoesNotContain("-apple-system", theme.Css));
		Assert.Contains("font-family: \"Segoe UI Variable Text\", \"Segoe UI\", system-ui, sans-serif;", Themes.Default.Css);
		Assert.Equal(
			"a { font-family: \"Segoe UI Variable Text\", \"Segoe UI\", system-ui, sans-serif; font-size: 12px; } "
				+ "pre { font-family: \"SF Mono\", Menlo, \"Cascadia Mono\", Consolas, monospace; } h1 { font-family: Charter, Georgia, serif; }",
			ArticleTheme.WithWindowsFonts("a { font: -apple-system-body; font-size: 12px; } pre { font-family: \"SF Mono\", Menlo, monospace; } h1 { font-family: Charter, ui-serif; }"));
	}

	[Fact]
	public void UserThemeReplacesBuiltInOfSameName()
	{
		var userDirectory = Path.Combine(Path.GetTempPath(), "wnr-themes-" + Guid.NewGuid().ToString("N"));
		var folder = Directory.CreateDirectory(Path.Combine(userDirectory, "Sepia.nnwtheme")).FullName;
		try
		{
			File.WriteAllText(Path.Combine(folder, "Info.plist"), """
				<?xml version="1.0" encoding="UTF-8"?>
				<plist version="1.0"><dict><key>Name</key><string>Sepia</string><key>CreatorName</key><string>Me</string><key>Version</key><integer>2</integer></dict></plist>
				""");
			File.WriteAllText(Path.Combine(folder, "stylesheet.css"), "body { color: sepia; }");
			var themes = new ArticleThemes(ResourcesDirectory, userDirectory);

			var sepia = themes.Named("Sepia");

			Assert.False(sepia.IsAppTheme);
			Assert.Equal("Me", sepia.CreatorName);
			Assert.Equal(2, sepia.Version);
			Assert.EndsWith("body { color: sepia; }", sepia.Css);
			// No template.html: the default template.
			Assert.Equal(themes.Default.Template, sepia.Template);
		}
		finally
		{
			Directory.Delete(userDirectory, recursive: true);
		}
	}

	[Fact]
	public void RenderFillsTheTemplate()
	{
		var article = new StoredArticle("id", "https://a.example/feed", "u1", "Hello <b>bold</b> <script>x</script>", "https://a.example/post#frag",
			"https://elsewhere.example/story", "<p>Body</p>", null, null, new DateTimeOffset(2026, 10, 28, 14, 5, 0, TimeSpan.Zero), null, DateTimeOffset.UnixEpoch, false, false)
		{
			AuthorsJson = """[{"Name":"Ana","Url":"https://ana.example/","AvatarUrl":null,"EmailAddress":null}]""",
		};
		var feed = new StoredFeed("https://a.example/feed", "https://a.example/feed", "A Blog", "https://a.example/", null, null, null);

		var rendering = ArticleRenderer.Render(article, new ArticleRenderContext(feed, IsFreshRss: false, ArticleTextSize.Large.CssClass(), "https://icons/a.png"), Themes.Default);
		var page = ArticleRenderer.PageHtml(rendering, Themes.PageTemplate);

		Assert.Equal("Hello <b>bold</b> &lt;script&gt;x&lt;/script&gt;", rendering.Title);
		Assert.Equal("https://a.example/post", rendering.BaseUrl);
		Assert.Contains("<a class=\"feedlink\" href=\"https://a.example/\">A Blog</a><br /><a href=\"https://ana.example/\">Ana</a>", rendering.Html);
		Assert.Contains("src=\"https://icons/a.png\"", rendering.Html);
		Assert.Contains("class=\"articleBody largeText\"><p>Body</p></div>", rendering.Html);
		Assert.Contains("Link: <a href=\"https://elsewhere.example/story\">elsewhere.example/story</a>", rendering.Html);
		Assert.DoesNotContain("[[", rendering.Html);
		Assert.Contains("<base href=\"https://a.example/post\">", page);
		Assert.Contains("<title>Hello <b>bold</b>", page);
	}

	[Fact]
	public void PlainTextBodyIsConvertedAndFreshRssContentDesanitized()
	{
		var textArticle = new StoredArticle("id", "f", "u", "T", null, null, null, "Line one\n\nLine two", null, null, null, DateTimeOffset.UnixEpoch, false, false);
		Assert.Contains("<p>Line one</p>\n<p>Line two</p>", ArticleRenderer.Render(textArticle, new ArticleRenderContext(null, false, "largeText", ""), Themes.Default).Html);

		var freshArticle = new StoredArticle("id", "f", "u", "T", null, null, "<sup data-sanitized-id=\"r1\">1</sup>", null, null, null, null, DateTimeOffset.UnixEpoch, false, false);
		Assert.Contains("<sup id=\"r1\">1</sup>", ArticleRenderer.Render(freshArticle, new ArticleRenderContext(null, true, "largeText", ""), Themes.Default).Html);
	}

	[Fact]
	public void SingleAuthorNamedLikeTheFeedIsLeftOut()
	{
		var article = new StoredArticle("id", "f", "u", "T", null, null, "<p>x</p>", null, null, null, null, DateTimeOffset.UnixEpoch, false, false)
		{
			AuthorsJson = """[{"Name":"A Blog","Url":null,"AvatarUrl":null,"EmailAddress":null}]""",
		};
		var feed = new StoredFeed("f", "f", "A Blog", null, null, null, null);
		Assert.Contains("A Blog</a><br /></td>", ArticleRenderer.Render(article, new ArticleRenderContext(feed, false, "largeText", ""), Themes.Default).Html);
	}

	[Fact]
	public void HeaderMetadataAndBylineEscapeHtmlAndAttributeCharacters()
	{
		var article = new StoredArticle("id", "https://a.example/feed", "u1", "Title", "https://a.example/post?a=1&b=\"2",
			"https://elsewhere.example/story?x=1&y=<tag>", null, "Plain text", null, new DateTimeOffset(2026, 10, 28, 14, 5, 0, TimeSpan.Zero), null, DateTimeOffset.UnixEpoch, false, false)
		{
			AuthorsJson = """[{"Name":"Ana <script>evil()</script>","Url":"https://ana.example/?q=\"x\"","AvatarUrl":null,"EmailAddress":null}]""",
		};
		var feed = new StoredFeed("https://a.example/feed", "https://a.example/feed", "Blog <b>& \"More\"", "https://a.example/?h=\"1\"", null, null, null);

		var rendering = ArticleRenderer.Render(article, new ArticleRenderContext(feed, IsFreshRss: false, ArticleTextSize.Large.CssClass(), "https://icons/a.png?v=\"1\""), Themes.Default);

		Assert.DoesNotContain("<script>", rendering.Html);
		Assert.Contains("Blog &lt;b&gt;&amp; &quot;More&quot;", rendering.Html);
		Assert.Contains("Ana &lt;script&gt;evil()&lt;/script&gt;", rendering.Html);
		Assert.Contains("href=\"https://ana.example/?q=&quot;x&quot;\"", rendering.Html);
		Assert.Contains("href=\"https://a.example/post?a=1&amp;b=&quot;2\"", rendering.Html);
		Assert.Contains("href=\"https://elsewhere.example/story?x=1&amp;y=&lt;tag&gt;\"", rendering.Html);
	}
}
