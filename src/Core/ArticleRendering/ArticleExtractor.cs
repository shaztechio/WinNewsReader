using System.Text;
using SmartReader;

namespace WinNewsReader.Core.ArticleRendering;

/// <summary>The readable part of an article's web page, for Reader View: NetNewsWire's <c>ExtractedArticle</c>.</summary>
/// <param name="Url">Where the page ended up, after redirects; relative links in <paramref name="Content"/> resolve against it.</param>
public sealed record ExtractedArticle(string Url, string? Title, string Content);

public sealed class ArticleExtractionException(string message) : Exception(message);

/// <summary>A page after a real browser loaded it and ran its scripts: where it ended up, and its HTML.</summary>
public sealed record RenderedPage(string Url, string Html);

/// <summary>
/// Reader View's extractor. NetNewsWire sends the article link to Feedbin's private extraction service; this downloads
/// the page and runs SmartReader, a Readability port, on this PC instead. Many sites refuse a plain download (403 and
/// bot checks) or build the page with scripts, so when that fails and <see cref="BrowserFetcher"/> is set, a browser loads it.
/// </summary>
public sealed class ArticleExtractor(FeedDownloader downloader)
{
	/// <summary>
	/// Loads a page in a real browser and returns it once it has rendered, reporting what it is doing as it goes. Set by the app, which owns a WebView2; throws if the
	/// page can't be loaded.
	/// </summary>
	public Func<string, CancellationToken, IProgress<string>?, Task<RenderedPage>>? BrowserFetcher { get; set; }

	/// <summary>Throws <see cref="ArticleExtractionException"/> when the page can't be fetched or has no readable article.</summary>
	public async Task<ExtractedArticle> ExtractAsync(string url, CancellationToken cancellationToken = default, IProgress<string>? progress = null)
	{
		var download = await downloader.DownloadAsync(url, null, null, cancellationToken);
		string failure;
		if (download is { Status: DownloadStatus.Ok, Data: { Length: > 0 } data })
		{
			var pageUrl = download.FinalUrl?.AbsoluteUri ?? url;
			// From a stream, so the HTML parser works out the page's text encoding.
			using var stream = new MemoryStream(data);
			var article = await Task.Run(() => Reader.ParseArticle(pageUrl, stream), cancellationToken);
			if (Extracted(article, pageUrl) is { } extracted)
			{
				return extracted;
			}
			failure = "Couldn’t find an article on that page.";
		}
		else
		{
			failure = $"Couldn’t load {url}: {download.Error ?? "no content"}.";
		}
		if (BrowserFetcher is not null && BrowserMayHelp(download))
		{
			try
			{
				var page = await BrowserFetcher(url, cancellationToken, progress);
				// A byte-order mark, which outranks the page's own charset tag: the browser has already decoded this text.
				using var rendered = new MemoryStream([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(page.Html)]);
				var article = await Task.Run(() => Reader.ParseArticle(page.Url, rendered), cancellationToken);
				if (Extracted(article, page.Url) is { } extracted)
				{
					return extracted;
				}
			}
			catch (Exception) when (!cancellationToken.IsCancellationRequested)
			{
				// The browser couldn't help either; the first failure is the one to report.
			}
		}
		throw new ArticleExtractionException(failure);
	}

	/// <summary>A page that came back fine but has no article might build itself with scripts; a refusal might be a bot check. A missing page or a dead connection won't change.</summary>
	public static bool BrowserMayHelp(DownloadResult download) =>
		download.Status == DownloadStatus.Ok || download.StatusCode is 401 or 403 or 406 or 429 or >= 500;

	private static ExtractedArticle? Extracted(Article article, string pageUrl) =>
		article.IsReadable && !string.IsNullOrWhiteSpace(article.Content) ? new ExtractedArticle(pageUrl, article.Title, article.Content) : null;
}
