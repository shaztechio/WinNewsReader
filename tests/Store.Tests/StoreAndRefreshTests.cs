using System.Net;
using System.Text;
using WinNewsReader.Core;

namespace WinNewsReader.Store.Tests;

public sealed class StoreAndRefreshTests : IDisposable
{
	private const string FeedUrl = "https://example.com/feed.xml";

	private readonly string _directory = Path.Combine(Path.GetTempPath(), "wnr-tests-" + Guid.NewGuid().ToString("N"));
	private readonly FakeHandler _handler = new();
	private readonly ArticlesStore _store;
	private readonly FeedDownloader _downloader;
	private readonly LocalAccount _account;

	public StoreAndRefreshTests()
	{
		_store = new ArticlesStore(Path.Combine(_directory, "articles.db"));
		_downloader = new FeedDownloader(_handler);
		_account = new LocalAccount(_store, _downloader);
	}

	public void Dispose()
	{
		_downloader.Dispose();
		_store.Dispose();
		Directory.Delete(_directory, recursive: true);
	}

	[Fact]
	public void ArticleIdMatchesNetNewsWireFormula() =>
		// md5("https://example.com/feed.xml guid-1")
		Assert.Equal(
			Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("https://example.com/feed.xml guid-1"))),
			ArticlesStore.CalculatedArticleId(FeedUrl, "guid-1"));

	[Fact]
	public async Task SubscribeStoresFeedAndUnreadArticles()
	{
		_handler.Respond(Rss("One", "Two"), etag: "\"v1\"");
		var (feed, newArticles) = await _account.SubscribeAsync(FeedUrl);

		Assert.Equal("Test Feed", feed.Name);
		Assert.Equal("https://example.com/", feed.HomePageUrl);
		Assert.Equal("\"v1\"", feed.ETag);
		Assert.Equal(2, newArticles);
		Assert.Equal(["Two", "One"], _store.GetUnreadArticles().Select(a => a.Title)); // newest first
	}

	[Fact]
	public async Task SubscribeRejectsNonFeeds()
	{
		_handler.Respond("<html><head><title>Not a feed</title></head><body>" + new string('x', 200) + "</body></html>");
		await Assert.ThrowsAsync<SubscribeException>(() => _account.SubscribeAsync(FeedUrl));
		Assert.Empty(_store.GetFeeds());
	}

	[Fact]
	public async Task RefreshAddsOnlyNewArticlesAndKeepsReadState()
	{
		_handler.Respond(Rss("One"));
		await _account.SubscribeAsync(FeedUrl);
		var first = Assert.Single(_store.GetUnreadArticles());
		_store.MarkRead([first.ArticleId]);

		_handler.Respond(Rss("One", "Two"));
		var result = Assert.Single(await _account.RefreshAllAsync());

		Assert.Equal(DownloadStatus.Ok, result.Status);
		Assert.Equal(1, result.NewArticles);
		Assert.Equal("Two", Assert.Single(_store.GetUnreadArticles()).Title);
		Assert.True(_store.GetArticle(first.ArticleId)!.Read);
	}

	[Fact]
	public async Task RefreshSendsConditionalGetHeaders()
	{
		_handler.Respond(Rss("One"), etag: "\"v1\"", lastModified: "Tue, 27 Oct 2026 10:00:00 GMT");
		await _account.SubscribeAsync(FeedUrl);

		_handler.RespondNotModified();
		var result = Assert.Single(await _account.RefreshAllAsync());

		Assert.Equal(DownloadStatus.NotModified, result.Status);
		var request = _handler.Requests[^1];
		Assert.Equal("\"v1\"", request.Headers.IfNoneMatch.Single().ToString());
		Assert.Equal("Tue, 27 Oct 2026 10:00:00 GMT", request.Headers.GetValues("If-Modified-Since").Single());
	}

	[Fact]
	public async Task UpdatedArticleContentReplacesOld()
	{
		_handler.Respond(Rss("One"));
		await _account.SubscribeAsync(FeedUrl);
		_handler.Respond(Rss("One").Replace("Body of One", "Edited body"));
		await _account.RefreshAllAsync();

		Assert.Equal("Edited body", Assert.Single(_store.GetUnreadArticles()).ContentHtml);
	}

	[Fact]
	public async Task MarkAllReadAndUnsubscribe()
	{
		_handler.Respond(Rss("One", "Two"));
		await _account.SubscribeAsync(FeedUrl);

		Assert.Equal(2, _store.MarkAllRead(FeedUrl));
		Assert.Equal(0, _store.UnreadCount());
		Assert.True(_store.RemoveFeed(FeedUrl));
		Assert.Empty(_store.GetFeeds());
		Assert.Empty(_store.GetArticles(FeedUrl));
	}

	[Fact]
	public async Task FollowsHttpsToHttpRedirects()
	{
		// http → https → http, as owensd.io and imore.com do. HttpClient alone refuses the downgrade.
		var feed = Rss("One");
		_handler.RespondWith(request => request.RequestUri!.ToString() switch
		{
			"http://example.com/rss.xml" => Redirect("https://example.com/rss.xml"),
			"https://example.com/rss.xml" => Redirect("http://example.com/feed/"),
			"http://example.com/feed/" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(feed) },
			_ => new HttpResponseMessage(HttpStatusCode.NotFound),
		});

		var (subscribed, newArticles) = await _account.SubscribeAsync("http://example.com/rss.xml");

		Assert.Equal("http://example.com/rss.xml", subscribed.Url);
		Assert.Equal(1, newArticles);
	}

	[Fact]
	public async Task RedirectLoopsFail()
	{
		_handler.RespondWith(_ => Redirect("https://example.com/loop"));
		var result = await _downloader.DownloadAsync("https://example.com/loop", null, null);
		Assert.Equal(DownloadStatus.Failed, result.Status);
		Assert.Equal(FeedDownloader.MaxRedirects + 1, _handler.Requests.Count);
	}

	private static HttpResponseMessage Redirect(string location) =>
		new(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri(location) } };

	[Fact]
	public async Task ConcurrentRefreshesShareTheStoreSafely()
	{
		// Regression: overlapping refreshes once collided on the shared SQLite connection.
		_handler.Delay = TimeSpan.FromMilliseconds(20);
		_handler.Respond(Rss("One", "Two"));
		for (var i = 0; i < 24; i++)
		{
			_store.AddFeed($"https://host{i % 6}.example/feed{i}.xml");
		}

		var results = await _account.RefreshAllAsync();

		Assert.Equal(24, results.Count);
		Assert.All(results, result => Assert.Equal(DownloadStatus.Ok, result.Status));
		Assert.Equal(48, _store.UnreadCount());
	}

	[Fact]
	public void ImportOpmlAddsFeedsInFolders()
	{
		var opmlPath = Path.Combine(_directory, "subs.opml");
		File.WriteAllText(opmlPath, """
			<?xml version="1.0" encoding="UTF-8"?>
			<opml version="1.0">
			  <head><title>Subs</title></head>
			  <body>
			    <outline text="A" xmlUrl="https://a.example/feed"/>
			    <outline text="Folder">
			      <outline text="B" xmlUrl="https://b.example/feed"/>
			    </outline>
			  </body>
			</opml>
			""");
		Assert.Equal(2, _account.ImportOpml(opmlPath));
		Assert.Equal(0, _account.ImportOpml(opmlPath));
		Assert.Equal(["A", "B"], _store.GetFeeds().Select(f => f.Name));
	}

	/// <summary>An RSS feed whose items are titled <paramref name="titles"/>, one day apart, oldest first.</summary>
	private static string Rss(params string[] titles)
	{
		var items = titles.Select((title, i) => $"""
			<item>
			  <title>{title}</title>
			  <guid isPermaLink="false">guid-{title}</guid>
			  <link>https://example.com/{title}</link>
			  <description>Body of {title}</description>
			  <pubDate>{new DateTimeOffset(2026, 10, 1 + i, 12, 0, 0, TimeSpan.Zero):R}</pubDate>
			</item>
			""");
		return $"""
			<?xml version="1.0" encoding="UTF-8"?>
			<rss version="2.0"><channel>
			<title>Test Feed</title><link>https://example.com/</link>
			{string.Join("\n", items)}
			</channel></rss>
			""";
	}

	private sealed class FakeHandler : HttpMessageHandler
	{
		private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

		public void RespondWith(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

		public List<HttpRequestMessage> Requests { get; } = [];

		/// <summary>Makes responses arrive asynchronously, so refreshes overlap.</summary>
		public TimeSpan Delay { get; set; }

		public void Respond(string body, string? etag = null, string? lastModified = null) => _respond = _ =>
		{
			var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/rss+xml") };
			if (etag is not null)
			{
				response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag);
			}
			if (lastModified is not null)
			{
				response.Content.Headers.TryAddWithoutValidation("Last-Modified", lastModified);
			}
			return response;
		};

		public void RespondNotModified() => _respond = _ => new HttpResponseMessage(HttpStatusCode.NotModified);

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			lock (Requests)
			{
				Requests.Add(request);
			}
			if (Delay > TimeSpan.Zero)
			{
				await Task.Delay(Delay, cancellationToken);
			}
			var response = _respond(request);
			response.RequestMessage = request;
			return response;
		}
	}
}
