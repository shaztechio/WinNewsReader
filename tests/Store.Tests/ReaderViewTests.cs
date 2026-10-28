using System.Net;
using System.Text;
using WinNewsReader.Core;
using WinNewsReader.Core.ArticleRendering;

namespace WinNewsReader.Store.Tests;

public sealed class ReaderViewTests : IDisposable
{
	private const string ArticleUrl = "https://blog.example/2026/10/post";

	private static readonly string ResourcesDirectory = Path.Combine(AppContext.BaseDirectory, "ArticleRendering", "Resources");

	private readonly Dictionary<string, string> _pages = [];
	private readonly FeedDownloader _downloader;

	public ReaderViewTests() => _downloader = new FeedDownloader(new Handler(_pages));

	public void Dispose() => _downloader.Dispose();

	[Fact]
	public async Task ExtractsTheArticleAndDropsThePageAround()
	{
		_pages[ArticleUrl] = Page("""
			<nav><a href="/">Home</a> <a href="/about">About</a> <a href="/archive">Archive</a></nav>
			<article>
			<h1>On Reading Feeds</h1>
			<p>Feed readers are a quiet way to follow the writers you care about. Instead of checking a dozen sites, the reader brings each new post to you, in order, without an algorithm deciding what you see first.</p>
			<p>That calm is the point. A feed reader doesn't count your attention or pad the page with recommendations; it shows the article, and then it gets out of the way so you can read it from start to finish.</p>
			<p>Some feeds send only a summary, though, which is where a reader view earns its keep: it fetches the page, finds the article in it, and shows that, styled like everything else you read. <img src="/images/reader.png" alt="A reader"></p>
			</article>
			<footer>Copyright Blog Example. <a href="/privacy">Privacy</a></footer>
			""");

		var extracted = await new ArticleExtractor(_downloader).ExtractAsync(ArticleUrl);

		Assert.Equal(ArticleUrl, extracted.Url);
		Assert.Contains("Feed readers are a quiet way", extracted.Content);
		Assert.Contains("styled like everything else you read", extracted.Content);
		Assert.DoesNotContain("Privacy", extracted.Content);
		Assert.DoesNotContain("Archive", extracted.Content);
	}

	[Fact]
	public async Task PageWithNoArticleIsReported()
	{
		_pages[ArticleUrl] = Page("<nav><a href=\"/\">Home</a></nav><p>Sign in.</p>");
		await Assert.ThrowsAsync<ArticleExtractionException>(() => new ArticleExtractor(_downloader).ExtractAsync(ArticleUrl));
	}

	[Fact]
	public async Task MissingPageIsReported()
	{
		var error = await Assert.ThrowsAsync<ArticleExtractionException>(() => new ArticleExtractor(_downloader).ExtractAsync(ArticleUrl));
		Assert.Contains(ArticleUrl, error.Message);
	}

	[Fact]
	public void ExtractedArticleRendersWithThePageAsBase()
	{
		var themes = new ArticleThemes(ResourcesDirectory, Path.Combine(Path.GetTempPath(), "wnr-no-user-themes"));
		var article = new StoredArticle("id", "f", "u", "Title", ArticleUrl, null, "<p>Summary only</p>", null, null, null, null, DateTimeOffset.UnixEpoch, false, false);
		var extracted = new ExtractedArticle("https://blog.example/2026/10/post/", "Title", "<div><p>The whole article.</p></div>");

		var rendering = ArticleRenderer.Render(article, new ArticleRenderContext(null, false, "largeText", ""), themes.Default, extracted);

		Assert.Contains("<p>The whole article.</p>", rendering.Html);
		Assert.DoesNotContain("Summary only", rendering.Html);
		Assert.Equal("https://blog.example/2026/10/post/", rendering.BaseUrl);
		Assert.Equal("Title", rendering.Title);
	}

	private static string Page(string body) => $"<!doctype html><html><head><meta charset=\"utf-8\"><title>On Reading Feeds</title></head><body>{body}</body></html>";

	private sealed class Handler(Dictionary<string, string> pages) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var response = pages.TryGetValue(request.RequestUri!.AbsoluteUri, out var html)
				? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") }
				: new HttpResponseMessage(HttpStatusCode.NotFound);
			response.RequestMessage = request;
			return Task.FromResult(response);
		}
	}
}
