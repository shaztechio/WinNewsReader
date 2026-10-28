using System.Text;
using WinNewsReader.Parser.Html;

namespace WinNewsReader.Parser.Tests;

public class HtmlRelativeUrlResolverTests
{
	private const string BaseUrl = "https://example.com/blog/post1/";

	private static string Resolved(string html) => HtmlRelativeUrlResolver.ResolvingRelativeUrls(html, BaseUrl);

	[Theory]
	[InlineData("<a href=\"page.html\">x</a>", "<a href=\"https://example.com/blog/post1/page.html\">x</a>")]
	[InlineData("<img src=\"img.png\">", "<img src=\"https://example.com/blog/post1/img.png\">")]
	[InlineData("<video poster=\"p.jpg\"></video>", "<video poster=\"https://example.com/blog/post1/p.jpg\"></video>")]
	[InlineData("<a href=\"/root\">x</a>", "<a href=\"https://example.com/root\">x</a>")]
	[InlineData("<a href=\"../up/\">x</a>", "<a href=\"https://example.com/blog/up/\">x</a>")]
	[InlineData("<a href=\"?q=1\">x</a>", "<a href=\"https://example.com/blog/post1/?q=1\">x</a>")]
	[InlineData("<a href=\"//cdn.example.com/x\">x</a>", "<a href=\"https://cdn.example.com/x\">x</a>")]
	public void RelativeValuesResolve(string input, string expected) => Assert.Equal(expected, Resolved(input));

	[Fact]
	public void AmpersandEntityInRelativeUrlSurvives() =>
		Assert.Equal("<a href=\"https://example.com/blog/post1/page?a=1&amp;b=2\">x</a>", Resolved("<a href=\"page?a=1&amp;b=2\">x</a>"));

	[Fact]
	public void AttributeNamesAreCaseInsensitive()
	{
		Assert.Equal("<a HREF=\"https://example.com/blog/post1/x\">y</a>", Resolved("<a HREF=\"x\">y</a>"));
		Assert.Equal("<img SrcSet=\"https://example.com/blog/post1/x 1x\">", Resolved("<img SrcSet=\"x 1x\">"));
	}

	[Fact]
	public void UnquotedAndSingleQuotedValuesResolve()
	{
		Assert.Equal("<a href=https://example.com/blog/post1/foo/bar>x</a>", Resolved("<a href=foo/bar>x</a>"));
		Assert.Equal("<a href='https://example.com/blog/post1/x'>y</a>", Resolved("<a href='x'>y</a>"));
	}

	[Theory]
	[InlineData("<a href=\"#fn1\">x</a>")]
	[InlineData("<a href=\"\">x</a>")]
	[InlineData("<a href=\"https://other.example.com/a\">x</a>")]
	[InlineData("<a href=\"http://other.example.com/a\">x</a>")]
	[InlineData("<img src=\"data:image/png;base64,AAAA\">")]
	[InlineData("<a href=\"mailto:x@example.com\">x</a>")]
	[InlineData("<a href=\"javascript:void(0)\">x</a>")]
	[InlineData("<a title=\"foo.png\" data-src=\"x\">not URL attributes</a>")]
	[InlineData("<script>var a = \"<img src=foo>\";</script>")]
	[InlineData("<style>.x { background: url(foo.png); }</style>")]
	[InlineData("<!-- <a href=\"x\">commented out</a> -->")]
	[InlineData("<![CDATA[<a href=\"x\">]]>")]
	public void UntouchedValues(string html) => Assert.Equal(html, Resolved(html));

	[Fact]
	public void ScriptSrcAttributeStillResolves() =>
		// The script element's own attributes are scanned; only its text content is skipped.
		Assert.Equal(
			"<script src=\"https://example.com/blog/post1/app.js\">var s = \"<img src=x>\";</script>",
			Resolved("<script src=\"app.js\">var s = \"<img src=x>\";</script>"));

	[Theory]
	[InlineData("<img srcset=\"a.png\">", "<img srcset=\"https://example.com/blog/post1/a.png\">")]
	[InlineData("<img srcset=\"a.png 1x, b.png 2x\">", "<img srcset=\"https://example.com/blog/post1/a.png 1x, https://example.com/blog/post1/b.png 2x\">")]
	[InlineData("<img srcset=\"a.png, b.png 2x\">", "<img srcset=\"https://example.com/blog/post1/a.png, https://example.com/blog/post1/b.png 2x\">")]
	[InlineData("<img srcset=\"a.png 1x,b.png 2x\">", "<img srcset=\"https://example.com/blog/post1/a.png 1x,https://example.com/blog/post1/b.png 2x\">")]
	[InlineData("<img srcset=\"https://other.example.com/a.png 1x, b.png 2x\">", "<img srcset=\"https://other.example.com/a.png 1x, https://example.com/blog/post1/b.png 2x\">")]
	public void SrcsetCandidatesResolve(string input, string expected) => Assert.Equal(expected, Resolved(input));

	[Fact]
	public void SurroundingWhitespaceInValuesIsIgnoredAndPreserved()
	{
		// Whitespace can't hide a fragment-only href from the skip rule.
		Assert.Equal("<a href=\" #fn1\">x</a>", Resolved("<a href=\" #fn1\">x</a>"));
		// A padded relative value resolves, and the padding stays.
		Assert.Equal("<a href=\" https://example.com/blog/post1/page.html \">x</a>", Resolved("<a href=\" page.html \">x</a>"));
	}

	[Fact]
	public void SimilarlyNamedCloseTagDoesNotEndRawText()
	{
		var html = "<script>if (a</scripty>b) { s = '<img src=\"x.png\">'; }</script>";
		Assert.Equal(html, Resolved(html));
	}

	[Fact]
	public void MalformedInputIsSafe()
	{
		var unterminated = "<a href=\"never closed";
		Assert.Equal(unterminated, Resolved(unterminated));

		Assert.Equal("<img src=\"https://example.com/blog/post1/a.png\"><a href=\"never closed", Resolved("<img src=\"a.png\"><a href=\"never closed"));

		var atEof = "<a href=";
		Assert.Equal(atEof, Resolved(atEof));

		Assert.Equal("1 < 2 and <a href=\"https://example.com/blog/post1/x\">y</a>", Resolved("1 < 2 and <a href=\"x\">y</a>"));
	}

	[Fact]
	public void AllAbsoluteDocumentIsUnchanged()
	{
		var html = """
			<p>Intro text with an <a href="https://example.org/a">absolute link</a>.</p>
			<img src="https://example.org/img.png" srcset="https://example.org/img.png 1x">
			<p>More text, an <a href="#fn1">footnote</a>, and an <a href="">empty href</a>.</p>
			""";
		Assert.Equal(html, Resolved(html));
	}

	[Fact]
	public void MixedDocumentIsSurgical()
	{
		var html = """
			<figure class='pic'>
				<picture>
					<source srcset="shot_light.png" media="(prefers-color-scheme: light)" width="1280" height="720"/>
					<img src="shot.png" width="1280" height="720" alt="A screenshot"/>
				</picture>
				<figcaption>Caption &amp; more</figcaption>
			</figure>
			""";
		var expected = """
			<figure class='pic'>
				<picture>
					<source srcset="https://example.com/blog/post1/shot_light.png" media="(prefers-color-scheme: light)" width="1280" height="720"/>
					<img src="https://example.com/blog/post1/shot.png" width="1280" height="720" alt="A screenshot"/>
				</picture>
				<figcaption>Caption &amp; more</figcaption>
			</figure>
			""";
		Assert.Equal(expected, Resolved(html));
	}

	[Fact]
	public void BytesEntryPointMatchesStringEntryPoint()
	{
		var html = "<img src=\"img.png\">";
		Assert.Equal(Resolved(html), HtmlRelativeUrlResolver.ResolvingRelativeUrls(Encoding.UTF8.GetBytes(html), BaseUrl));

		var absolute = "<img src=\"https://example.org/img.png\">";
		Assert.Equal(absolute, HtmlRelativeUrlResolver.ResolvingRelativeUrls(Encoding.UTF8.GetBytes(absolute), BaseUrl));
	}
}
