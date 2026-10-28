using SmartReader;

namespace WinNewsReader.Core.ArticleRendering;

/// <summary>The readable part of an article's web page, for Reader View: NetNewsWire's <c>ExtractedArticle</c>.</summary>
/// <param name="Url">Where the page ended up, after redirects; relative links in <paramref name="Content"/> resolve against it.</param>
public sealed record ExtractedArticle(string Url, string? Title, string Content);

public sealed class ArticleExtractionException(string message) : Exception(message);

/// <summary>
/// Reader View's extractor. NetNewsWire sends the article link to Feedbin's private extraction service; this downloads
/// the page and runs SmartReader, a Readability port, on this PC instead.
/// </summary>
public sealed class ArticleExtractor(FeedDownloader downloader)
{
	/// <summary>Throws <see cref="ArticleExtractionException"/> when the page can't be fetched or has no readable article.</summary>
	public async Task<ExtractedArticle> ExtractAsync(string url, CancellationToken cancellationToken = default)
	{
		var download = await downloader.DownloadAsync(url, null, null, cancellationToken);
		if (download is not { Status: DownloadStatus.Ok, Data: { Length: > 0 } data })
		{
			throw new ArticleExtractionException($"Couldn’t load {url}: {download.Error ?? "no content"}.");
		}
		var pageUrl = download.FinalUrl?.AbsoluteUri ?? url;
		// From a stream, so the HTML parser works out the page's text encoding.
		using var stream = new MemoryStream(data);
		var article = await Task.Run(() => Reader.ParseArticle(pageUrl, stream), cancellationToken);
		if (!article.IsReadable || string.IsNullOrWhiteSpace(article.Content))
		{
			throw new ArticleExtractionException("Couldn’t find an article on that page.");
		}
		return new ExtractedArticle(pageUrl, article.Title, article.Content);
	}
}
