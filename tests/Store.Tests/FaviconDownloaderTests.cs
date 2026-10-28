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
