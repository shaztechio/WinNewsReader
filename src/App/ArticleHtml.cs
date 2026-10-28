using System.Net;
using WinNewsReader.Parser.Html;
using WinNewsReader.Parser.Xml;
using WinNewsReader.Store;

namespace WinNewsReader.App;

/// <summary>
/// Builds the page shown in the article pane. A plain stand-in until NetNewsWire's templates and themes are
/// ported in Phase 3 (parity rows AV-01 and AV-02).
/// </summary>
public static class ArticleHtml
{
	private const string Style = """
		:root { color-scheme: light dark; }
		body { font: 16px/1.55 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; margin: 0; padding: 24px 32px 48px; background: transparent; }
		main { max-width: 44em; margin: 0 auto; }
		header { margin-bottom: 1.5em; }
		h1 { font-size: 1.6em; line-height: 1.25; margin: 0 0 0.3em; }
		h1 a { color: inherit; text-decoration: none; }
		.meta { opacity: 0.7; font-size: 0.9em; }
		img, video, iframe { max-width: 100%; height: auto; }
		pre { overflow-x: auto; padding: 0.8em; background: color-mix(in srgb, currentColor 7%, transparent); border-radius: 6px; }
		code { font-family: "Cascadia Mono", Consolas, monospace; font-size: 0.9em; }
		blockquote { margin-left: 0; padding-left: 1em; border-left: 3px solid color-mix(in srgb, currentColor 25%, transparent); }
		a { color: #0a66d8; }
		@media (prefers-color-scheme: dark) { a { color: #6cb4ff; } }
		""";

	public static string Empty => $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>{Style}</style></head><body></body></html>";

	public static string Render(StoredArticle article, StoredFeed? feed)
	{
		var link = article.Url ?? article.ExternalUrl;
		var title = WebUtility.HtmlEncode((article.Title ?? "").DecodingHtmlEntities());
		var heading = link is null ? title : $"<a href=\"{WebUtility.HtmlEncode(link)}\">{title}</a>";
		var localDate = (article.DatePublished ?? article.DateArrived).ToLocalTime();
		var date = $"{localDate:D}, {localDate:t}";
		var feedName = WebUtility.HtmlEncode(feed is null ? "" : Core.NameSorting.DisplayName(feed));
		var baseUrl = link ?? feed?.HomePageUrl ?? feed?.Url;

		string body;
		if (article.ContentHtml is { Length: > 0 } html)
		{
			body = baseUrl is null ? html : HtmlRelativeUrlResolver.ResolvingRelativeUrls(html, baseUrl);
		}
		else
		{
			var text = article.ContentText ?? article.Summary ?? "";
			body = string.Concat(text.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries).Select(paragraph => $"<p>{WebUtility.HtmlEncode(paragraph)}</p>"));
		}

		return $"""
			<!DOCTYPE html>
			<html>
			<head><meta charset="utf-8"><style>{Style}</style></head>
			<body><main>
			<header><h1>{heading}</h1><div class="meta">{feedName} · {date}</div></header>
			<article>{body}</article>
			</main></body>
			</html>
			""";
	}
}
