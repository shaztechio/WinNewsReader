using System.Net;
using System.Text;
using WinNewsReader.Core;

namespace WinNewsReader.Store.Tests;

public sealed class FaviconDownloaderTests : IDisposable
{
	private const string HomePage = "https://example.com/blog/";

	private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
	private static readonly byte[] Ico = [0, 0, 1, 0, 1, 0, 16, 16];

	private readonly string _directory = Path.Combine(Path.GetTempPath(), "wnr-tests-" + Guid.NewGuid().ToString("N"));
	private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = [];
	private readonly List<string> _requests = [];
	private readonly FeedDownloader _downloader;

	public FaviconDownloaderTests() => _downloader = new FeedDownloader(new Handler(this));

	public void Dispose()
	{
		_downloader.Dispose();
		if (Directory.Exists(_directory))
		{
			Directory.Delete(_directory, recursive: true);
		}
	}

	[Fact]
	public async Task UsesFirstIconLinkSkippingSvg()
	{
		Page("""<link rel="icon" type="image/svg+xml" href="/icon.svg"><link rel="icon" href="/assets/icon.png"><link rel="icon" href="/other.png">""");
		Respond("https://example.com/assets/icon.png", Png);

		var path = await Downloader().FaviconPathAsync(HomePage);

		Assert.NotNull(path);
		Assert.EndsWith(".png", path);
		Assert.Equal(Png, File.ReadAllBytes(path));
		Assert.DoesNotContain("https://example.com/icon.svg", _requests);
		Assert.DoesNotContain("https://example.com/other.png", _requests);
	}

	[Fact]
	public async Task FallsBackToFaviconIcoAtSiteRoot()
	{
		Page("<title>No icons</title>");
		Respond("https://example.com/favicon.ico", Ico);

		Assert.EndsWith(".ico", await Downloader().FaviconPathAsync(HomePage));
	}

	[Fact]
	public async Task BrokenIconLinkFallsBackToFaviconIco()
	{
		Page("""<link rel="icon" href="/missing.png">""");
		Respond("https://example.com/favicon.ico", Ico);

		Assert.EndsWith(".ico", await Downloader().FaviconPathAsync(HomePage));
	}

	[Fact]
	public async Task NonImageIsRejectedAndTheMissIsRemembered()
	{
		Page("<title>No icons</title>");
		Respond("https://example.com/favicon.ico", Encoding.UTF8.GetBytes("<html>Not found</html>"));

		Assert.Null(await Downloader().FaviconPathAsync(HomePage));
		var requests = _requests.Count;
		Assert.Null(await Downloader().FaviconPathAsync(HomePage));
		Assert.Equal(requests, _requests.Count);
	}

	[Fact]
	public async Task CachedIconIsReusedWithoutDownloading()
	{
		Page("""<link rel="icon" href="/icon.png">""");
		Respond("https://example.com/icon.png", Png);
		var first = await Downloader().FaviconPathAsync(HomePage);
		_requests.Clear();

		Assert.Equal(first, await Downloader().FaviconPathAsync(HomePage));
		Assert.Empty(_requests);
	}

	[Fact]
	public async Task ConcurrentLookupsForOneSiteShareOneDownload()
	{
		Page("""<link rel="icon" href="/icon.png">""");
		Respond("https://example.com/icon.png", Png);
		var downloader = Downloader();

		var paths = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => downloader.FaviconPathAsync(HomePage)));

		Assert.Single(paths.Distinct());
		Assert.Single(_requests, url => url == HomePage);
	}

	private static readonly byte[] BigPng = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 9, 9, 9, 9];

	[Fact]
	public async Task LargestAppleTouchIconBeatsFaviconLinks()
	{
		Page("""
			<link rel="icon" href="/favicon.png">
			<link rel="apple-touch-icon" sizes="60x60" href="/touch-60.png">
			<link rel="apple-touch-icon" sizes="180x180" href="/touch-180.png">
			""");
		Respond("https://example.com/favicon.png", Png);
		Respond("https://example.com/touch-60.png", Png);
		Respond("https://example.com/touch-180.png", BigPng);

		var path = await Downloader().IconPathAsync(HomePage, declaredIconUrl: null, feedUrl: null);

		Assert.Equal(BigPng, File.ReadAllBytes(path!));
		Assert.DoesNotContain("https://example.com/favicon.png", _requests);
	}

	[Fact]
	public async Task FeedsOwnIconComesBeforeAnythingOnTheHomePage()
	{
		Page("""<link rel="apple-touch-icon" sizes="180x180" href="/touch-180.png">""");
		Respond("https://cdn.example/feed-icon.png", BigPng);
		Respond("https://example.com/touch-180.png", Png);

		var path = await Downloader().IconPathAsync(HomePage, "https://cdn.example/feed-icon.png", "https://example.com/feed");

		Assert.Equal(BigPng, File.ReadAllBytes(path!));
		Assert.DoesNotContain("https://example.com/touch-180.png", _requests);
	}

	[Fact]
	public async Task DeclaredIconThatIsNotAnImageFallsThroughToTheHomePage()
	{
		Page("""<link rel="apple-touch-icon" sizes="180x180" href="/touch-180.png">""");
		_responses["https://cdn.example/feed-icon.png"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Not found</html>") };
		Respond("https://example.com/touch-180.png", BigPng);

		var path = await Downloader().IconPathAsync(HomePage, "https://cdn.example/feed-icon.png", null);

		Assert.Equal(BigPng, File.ReadAllBytes(path!));
	}

	[Fact]
	public async Task OversizedIconIsSkipped()
	{
		Page("""<link rel="apple-touch-icon" sizes="180x180" href="/huge.png"><link rel="icon" href="/small.png">""");
		_responses["https://example.com/huge.png"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([.. Png, .. new byte[2_100_000]]) };
		Respond("https://example.com/small.png", Png);

		var path = await Downloader().IconPathAsync(HomePage, null, null);

		Assert.Equal(Png, File.ReadAllBytes(path!));
	}

	[Fact]
	public async Task WordPressUploadsLoseTheirSmallSizeQuery()
	{
		Page("<title>x</title>");
		Respond("https://example.com/wp-content/uploads/2020/logo.png", BigPng);
		Respond("https://example.com/wp-content/uploads/2020/logo.png?resize=32%2C32", Png);

		var path = await Downloader().IconPathAsync(HomePage, "https://example.com/wp-content/uploads/2020/logo.png?resize=32%2C32", null);

		Assert.Equal(BigPng, File.ReadAllBytes(path!));
		Assert.Equal("https://example.com/wp-content/uploads/2020/logo.png", FaviconDownloader.SanitizedIconUrl("https://example.com/wp-content/uploads/2020/logo.png?resize=32%2C32#x"));
		Assert.Equal("https://example.com/logo.png?w=32", FaviconDownloader.SanitizedIconUrl("https://example.com/logo.png?w=32"));
	}

	[Theory]
	[InlineData("https://xkcd.com/", "https://xkcd.com/rss.xml")]
	[InlineData("https://www.macsparky.com/blog/", "https://www.macsparky.com/blog/rss")]
	[InlineData("https://example.com/blog/", "https://propublica.org/feeds/propublica/main")]
	public async Task SitesWithWrongFeedIconsUseTheirFaviconOrHomePage(string homePage, string feedUrl)
	{
		var home = new Uri(homePage);
		_responses[homePage] = () => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("""<html><head><link rel="icon" href="/fav.png"></head></html>""", Encoding.UTF8, "text/html"),
		};
		Respond(new Uri(home, "/fav.png").AbsoluteUri, Png);
		Respond("https://cdn.example/declared.png", BigPng);

		var path = await Downloader().IconPathAsync(homePage, "https://cdn.example/declared.png", feedUrl);

		Assert.Equal(Png, File.ReadAllBytes(path!));
		Assert.DoesNotContain("https://cdn.example/declared.png", _requests);
	}

	[Fact]
	public async Task FeedsWithTheirOwnIconDontShareTheSitesCacheEntry()
	{
		Page("""<link rel="icon" href="/fav.png">""");
		Respond("https://example.com/fav.png", Png);
		Respond("https://cdn.example/feed-icon.png", BigPng);
		var downloader = Downloader();

		var site = await downloader.IconPathAsync(HomePage, null, null);
		var feed = await downloader.IconPathAsync(HomePage, "https://cdn.example/feed-icon.png", null);

		Assert.NotEqual(site, feed);
		Assert.Equal(Png, File.ReadAllBytes(site!));
		Assert.Equal(BigPng, File.ReadAllBytes(feed!));
	}

	[Theory]
	[InlineData("feed:https://example.com/")]
	[InlineData("not a url")]
	public async Task NonWebUrlsHaveNoFavicon(string url) => Assert.Null(await Downloader().FaviconPathAsync(url));

	private FaviconDownloader Downloader() => new(_downloader, _directory);

	private void Page(string head) =>
		_responses[HomePage] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"<html><head>{head}</head><body></body></html>", Encoding.UTF8, "text/html") };

	private void Respond(string url, byte[] body) =>
		_responses[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

	private sealed class Handler(FaviconDownloaderTests tests) : HttpMessageHandler
	{
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			await Task.Yield();
			var url = request.RequestUri!.AbsoluteUri;
			lock (tests._requests)
			{
				tests._requests.Add(url);
			}
			var response = tests._responses.TryGetValue(url, out var respond) ? respond() : new HttpResponseMessage(HttpStatusCode.NotFound);
			response.RequestMessage = request;
			return response;
		}
	}
}
