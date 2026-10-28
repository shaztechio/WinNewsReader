using System.Text.RegularExpressions;

namespace WinNewsReader.Core.ArticleRendering;

/// <summary>NetNewsWire's <c>ArticleRenderingSpecialCases</c>: fixes for particular feeds and kinds of content.</summary>
public static partial class ArticleRenderingSpecialCases
{
	/// <summary>Content JavaScript is always off for articles from these domains. Subdomains match too.</summary>
	private static readonly string[] DomainsWithJavaScriptDisabled = ["slashdot.org"];

	private static readonly string[] FeedDomainsWithParagraphsSeparatedByReturns = ["slashdot.org"];

	private static readonly string[] FeedDomainsWithStrayLineBreaks = ["macrumors.com"];

	/// <summary>YouTube won't play an embed whose page is on youtube.com, so YouTube articles render with NetNewsWire's site as the base URL.</summary>
	private const string BaseUrlForYouTubeArticles = "https://netnewswire.com/";

	private const string YouTubeFlashPlayerPathPrefix = "/v/";
	private const string YouTubeEmbedUrlPrefix = "https://www.youtube-nocookie.com/embed/";

	/// <summary>True when any of the URLs (article link, feed URL, feed home page) is on a domain whose articles render without JavaScript.</summary>
	public static bool ShouldDisableJavaScript(params string?[] urlStrings) => AnyUrlMatchesAnyDomain(urlStrings, DomainsWithJavaScriptDisabled);

	/// <summary>Slashdot separates paragraphs with blank lines. Checks the home page too, which catches a feed through a proxy such as FeedBurner.</summary>
	public static string InsertParagraphTagsIfNeeded(string html, string? feedUrl, string? homePageUrl) =>
		AnyUrlMatchesAnyDomain([feedUrl, homePageUrl], FeedDomainsWithParagraphsSeparatedByReturns) && html.Contains('\n')
			? ConsecutiveReturns().Replace(html, "<p>")
			: html;

	/// <summary>
	/// MacRumors puts a &lt;br/&gt; after every line of its feed HTML, including inside lists and tables, where each one
	/// renders as a gap. Removes those, keeping the ones that are content inside cells and items.
	/// </summary>
	public static string RemoveStrayLineBreaksIfNeeded(string html, string? feedUrl, string? homePageUrl) =>
		AnyUrlMatchesAnyDomain([feedUrl, homePageUrl], FeedDomainsWithStrayLineBreaks) && html.Contains("<br", StringComparison.OrdinalIgnoreCase)
			? StrayLineBreaks().Replace(html, "$1")
			: html;

	public static string FilterHtmlIfNeeded(string baseUrl, string html)
	{
		var filtered = RemoveLocationHrefRedirectScripts(html);
		return Uri.TryCreate(baseUrl, UriKind.Absolute, out var url) && url.Host.Contains("theverge.com", StringComparison.OrdinalIgnoreCase)
			? FilterVergeHtml(filtered)
			: filtered;
	}

	/// <summary>Removes <c>&lt;script&gt;</c> elements that assign to <c>location.href</c>, which would navigate away from the article.</summary>
	public static string RemoveLocationHrefRedirectScripts(string html) =>
		html.Contains("location.href", StringComparison.Ordinal) ? RedirectScript().Replace(html, "") : html;

	/// <summary>
	/// Some feeds embed a whole HTML document as an item's content; its &lt;html&gt; and &lt;body&gt; attributes would land on the
	/// page's own elements and clobber the theme. Renders only the body.
	/// </summary>
	public static string ExtractBodyFragmentIfNeeded(string html)
	{
		if (!html.Contains("<body", StringComparison.OrdinalIgnoreCase) && !html.Contains("<html", StringComparison.OrdinalIgnoreCase)
			&& !html.Contains("<!doctype", StringComparison.OrdinalIgnoreCase))
		{
			return html;
		}
		return BodyFragment(html) ?? RemovingDocumentWrapper(html);
	}

	public static string BaseUrlForRendering(string url) => IsYouTubeUrl(url) ? BaseUrlForYouTubeArticles : url;

	/// <summary>
	/// YouTube channel feeds list each video as a dead Flash player URL (youtube.com/v/ID). The privacy-enhanced embed URL
	/// for the same video; null for anything else.
	/// </summary>
	public static string? YouTubeEmbedUrl(string attachmentUrl)
	{
		if (!Uri.TryCreate(attachmentUrl, UriKind.Absolute, out var url) || !IsYouTubeUrl(attachmentUrl)
			|| !url.AbsolutePath.StartsWith(YouTubeFlashPlayerPathPrefix, StringComparison.Ordinal))
		{
			return null;
		}
		var videoId = url.AbsolutePath[YouTubeFlashPlayerPathPrefix.Length..];
		return videoId.Length > 0 && videoId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? YouTubeEmbedUrlPrefix + videoId : null;
	}

	/// <summary>
	/// FreshRSS renames <c>id</c> and <c>class</c> attributes to <c>data-sanitized-…</c>, which breaks inline footnotes; put them back.
	/// </summary>
	public static string DesanitizeFreshRssContent(string content) =>
		SanitizedId().Replace(SanitizedClass().Replace(content, "${open}class${close}"), "${open}id${close}");

	public static string FilterVergeHtml(string html) => html
		// Right curly single quote
		.Replace("â€™", "’").Replace("&acirc;&#128;&#153;", "’")
		// Left curly double quote
		.Replace("â€œ", "“").Replace("â&#128;&#156;", "“").Replace("&acirc;&#128;&#156;", "“")
		// Right curly double quote
		.Replace("â€", "”").Replace("â&#128;&#157;", "”").Replace("&acirc;&#128;&#157;", "”")
		// Em dash
		.Replace("â€”", "—").Replace("&acirc;&#128;&#148;", "—")
		.Replace("Â", "").Replace("&Acirc;&nbsp;", "")
		.Replace(" &amp;hellip;", "…").Replace("&amp;hellip;", "…");

	private static bool IsYouTubeUrl(string url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase);

	private static bool AnyUrlMatchesAnyDomain(IEnumerable<string?> urlStrings, string[] domains) =>
		urlStrings.Any(url => url is not null && FeedSpecifier.UrlMatchesDomain(url, domains));

	/// <summary>The content between a real &lt;body …&gt; tag and &lt;/body&gt;, or the end. Null when there's no body tag.</summary>
	private static string? BodyFragment(string html)
	{
		if (IndexOfTag("<body", html, 0) is not { } bodyOpen)
		{
			return null;
		}
		var tagClose = html.IndexOf('>', bodyOpen + "<body".Length);
		if (tagClose < 0)
		{
			return null;
		}
		var contentStart = tagClose + 1;
		var contentEnd = IndexOfTag("</body", html, contentStart) ?? html.Length;
		return html[contentStart..contentEnd];
	}

	/// <summary>A document with &lt;html&gt; and &lt;head&gt; but no &lt;body&gt;: removes the wrapper markup, keeping the rest.</summary>
	private static string RemovingDocumentWrapper(string html)
	{
		var s = html;
		var doctype = s.IndexOf("<!doctype", StringComparison.OrdinalIgnoreCase);
		if (doctype >= 0 && s.IndexOf('>', doctype) is var doctypeClose and >= 0)
		{
			s = s.Remove(doctype, doctypeClose + 1 - doctype);
		}
		if (IndexOfTag("<head", s, 0) is { } headOpen && IndexOfTag("</head", s, headOpen) is { } headClose && s.IndexOf('>', headClose) is var headEnd and >= 0)
		{
			s = s.Remove(headOpen, headEnd + 1 - headOpen);
		}
		if (IndexOfTag("<html", s, 0) is { } htmlOpen && s.IndexOf('>', htmlOpen) is var htmlOpenEnd and >= 0)
		{
			s = s.Remove(htmlOpen, htmlOpenEnd + 1 - htmlOpen);
		}
		if (IndexOfTag("</html", s, 0) is { } htmlClose && s.IndexOf('>', htmlClose) is var htmlCloseEnd and >= 0)
		{
			s = s.Remove(htmlClose, htmlCloseEnd + 1 - htmlClose);
		}
		return s;
	}

	/// <summary>Case-insensitive position of a real tag: "&lt;head" mustn't match "&lt;header".</summary>
	private static int? IndexOfTag(string tag, string html, int start)
	{
		var searchStart = start;
		while (html.IndexOf(tag, searchStart, StringComparison.OrdinalIgnoreCase) is var index and >= 0)
		{
			var after = index + tag.Length;
			if (after == html.Length || html[after] is '>' or '/' || char.IsWhiteSpace(html[after]))
			{
				return index;
			}
			searchStart = after;
		}
		return null;
	}

	[GeneratedRegex(@"(?:\r?\n[ \t]*){2,}")]
	private static partial Regex ConsecutiveReturns();

	/// <summary>One or more &lt;br&gt;s right after a tag where one can't be content: between list items, table rows or cells.</summary>
	[GeneratedRegex(@"(<(?:ul|ol|table|thead|tbody|tfoot|tr)\b[^>]*>|</(?:li|tr|td|th|thead|tbody|tfoot)>)(?:\s*<br\s*/?>)+", RegexOptions.IgnoreCase)]
	private static partial Regex StrayLineBreaks();

	/// <summary>A <c>&lt;script&gt;</c> assigning to <c>location.href</c>: <c>=</c> but not <c>==</c>.</summary>
	[GeneratedRegex(@"<script[^>]*>[^<]*location\.href\s*=(?!=)[^<]*</script>", RegexOptions.IgnoreCase)]
	private static partial Regex RedirectScript();

	[GeneratedRegex(@"(?<open><[^>]*?)data-sanitized-class(?<close>[^<]*?>)")]
	private static partial Regex SanitizedClass();

	[GeneratedRegex(@"(?<open><[^>]*?)data-sanitized-id(?<close>[^<]*?>)")]
	private static partial Regex SanitizedId();
}
