using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace WinNewsReader.Sync.GoogleReader.Tests;

/// <summary>
/// An in-memory Google Reader API server, behaving like FreshRSS's <c>greader.php</c> for the calls the client makes.
/// Other hosts serve <see cref="Sites"/>, so the feed finder works in subscribe tests.
/// </summary>
internal sealed class FakeReaderServer : HttpMessageHandler
{
	public const string Endpoint = "https://rss.example/api/greader.php";
	public const string Username = "alice";
	public const string Password = "api-password";

	private int _nextFeed = 1;
	private int _loginCount;

	public sealed class Feed
	{
		public required string Id { get; init; }
		public required string Title { get; set; }
		public required string Url { get; init; }
		public string? HomePageUrl { get; init; }
		public HashSet<string> Labels { get; } = [];
	}

	public sealed class Item
	{
		public required long Id { get; init; }
		public required string FeedId { get; init; }
		public required string Title { get; init; }
		public DateTimeOffset Published { get; init; } = DateTimeOffset.UtcNow.AddHours(-1);
		public bool Read { get; set; }
		public bool Starred { get; set; }
	}

	public List<Feed> Feeds { get; } = [];

	public HashSet<string> Labels { get; } = [];

	public List<Item> Items { get; } = [];

	public Dictionary<string, string> Sites { get; } = [];

	public List<string> Requests { get; } = [];

	public string AuthToken { get; set; } = "auth-1";

	public string WriteToken { get; set; } = "write-1";

	/// <summary>Item IDs per page of <c>stream/items/ids</c>, to exercise continuations.</summary>
	public int IdsPageSize { get; set; } = 1000;

	/// <summary>Runs after <c>quickadd</c>, to give the new feed articles as the server's first fetch would.</summary>
	public Action<Feed>? QuickAddItems { get; set; }

	/// <summary>Makes <c>edit-tag</c> fail, as a server error would.</summary>
	public bool FailEditTag { get; set; }

	public int LoginCount => _loginCount;

	public Feed AddFeed(string title, string url, params string[] labels)
	{
		var feed = new Feed { Id = $"feed/{_nextFeed++}", Title = title, Url = url, HomePageUrl = new Uri(url).GetLeftPart(UriPartial.Authority) + "/" };
		feed.Labels.UnionWith(labels);
		Labels.UnionWith(labels);
		Feeds.Add(feed);
		return feed;
	}

	public Item AddItem(long id, Feed feed, string title, bool read = false, bool starred = false, DateTimeOffset? published = null)
	{
		var item = new Item { Id = id, FeedId = feed.Id, Title = title, Read = read, Starred = starred, Published = published ?? DateTimeOffset.UtcNow.AddHours(-1) };
		Items.Add(item);
		return item;
	}

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var url = request.RequestUri!;
		var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
		lock (Requests)
		{
			Requests.Add($"{request.Method} {url.AbsolutePath}");
		}
		if (!url.AbsoluteUri.StartsWith(Endpoint, StringComparison.Ordinal))
		{
			return Sites.TryGetValue(url.AbsoluteUri, out var site)
				? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(site, Encoding.UTF8, site.StartsWith("<html", StringComparison.Ordinal) ? "text/html" : "application/rss+xml") }
				: new HttpResponseMessage(HttpStatusCode.NotFound);
		}

		var path = url.AbsolutePath["/api/greader.php".Length..];
		var query = Parse(url.Query.TrimStart('?'));
		var form = Parse(body);

		if (path == "/accounts/ClientLogin")
		{
			if (First(form, "Email") != Username || First(form, "Passwd") != Password)
			{
				return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("Error=BadAuthentication") };
			}
			Interlocked.Increment(ref _loginCount);
			return Text($"SID={Username}/{AuthToken}\nLSID=null\nAuth={Username}/{AuthToken}\n");
		}
		if (request.Headers.Authorization?.ToString() != $"GoogleLogin auth={Username}/{AuthToken}")
		{
			return new HttpResponseMessage(HttpStatusCode.Unauthorized);
		}
		if (path == "/reader/api/0/token")
		{
			return Text(WriteToken + "\n");
		}
		if (request.Method == HttpMethod.Post && First(form, "T") != WriteToken)
		{
			return new HttpResponseMessage(HttpStatusCode.Unauthorized);
		}

		lock (this)
		{
			switch (path)
			{
				case "/reader/api/0/tag/list":
					return Json(new
					{
						tags = new[] { new { id = "user/-/state/com.google/starred", type = (string?)null } }
							.Concat(Labels.Select(label => new { id = "user/-/label/" + label, type = (string?)"folder" })),
					});
				case "/reader/api/0/subscription/list":
					return Json(new
					{
						subscriptions = Feeds.Select(feed => new
						{
							id = feed.Id,
							title = feed.Title,
							categories = feed.Labels.Select(label => new { id = "user/-/label/" + label, label }),
							url = feed.Url,
							htmlUrl = feed.HomePageUrl,
						}),
					});
				case "/reader/api/0/stream/items/ids":
					return ItemIds(query);
				case "/reader/api/0/stream/items/contents":
					var ids = All(form, "i").Select(ParseLongId).ToHashSet();
					return Json(new
					{
						items = Items.Where(item => ids.Contains(item.Id)).Select(item => new
						{
							id = "tag:google.com,2005:reader/item/" + item.Id.ToString("x16", CultureInfo.InvariantCulture),
							title = item.Title,
							published = item.Published.ToUnixTimeSeconds(),
							summary = new { content = $"<p>{item.Title}</p>" },
							alternate = new[] { new { href = $"https://site.example/{item.Id}" } },
							origin = new { streamId = item.FeedId, title = "Feed" },
						}),
					});
				case "/reader/api/0/edit-tag":
					if (FailEditTag)
					{
						return new HttpResponseMessage(HttpStatusCode.InternalServerError);
					}
					foreach (var item in Items.Where(item => All(form, "i").Select(ParseLongId).Contains(item.Id)))
					{
						Apply(item, First(form, "a"), true);
						Apply(item, First(form, "r"), false);
					}
					return Text("OK");
				case "/reader/api/0/subscription/quickadd":
					var feedUrl = First(form, "quickadd")!;
					var added = AddFeed(feedUrl, feedUrl);
					QuickAddItems?.Invoke(added);
					return Json(new { numResults = 1, query = feedUrl, streamId = added.Id, streamName = feedUrl });
				case "/reader/api/0/subscription/edit":
					var target = Feeds.Single(feed => feed.Id == First(form, "s"));
					if (First(form, "ac") == "unsubscribe")
					{
						Feeds.Remove(target);
					}
					else
					{
						if (First(form, "a") is { } add)
						{
							target.Labels.Add(add["user/-/label/".Length..]);
							Labels.Add(add["user/-/label/".Length..]);
						}
						if (First(form, "r") is { } remove)
						{
							target.Labels.Remove(remove["user/-/label/".Length..]);
						}
						if (First(form, "t") is { } title)
						{
							target.Title = title;
						}
					}
					return Text("OK");
				case "/reader/api/0/rename-tag":
					var from = First(form, "s")!["user/-/label/".Length..];
					var to = First(form, "dest")!["user/-/label/".Length..];
					Labels.Remove(from);
					Labels.Add(to);
					foreach (var feed in Feeds.Where(feed => feed.Labels.Remove(from)))
					{
						feed.Labels.Add(to);
					}
					return Text("OK");
				case "/reader/api/0/disable-tag":
					var name = First(form, "s")!["user/-/label/".Length..];
					Labels.Remove(name);
					foreach (var feed in Feeds)
					{
						feed.Labels.Remove(name);
					}
					return Text("OK");
			}
		}
		return new HttpResponseMessage(HttpStatusCode.NotFound);
	}

	private HttpResponseMessage ItemIds(List<(string Name, string Value)> query)
	{
		var stream = First(query, "s");
		// Unsubscribing deletes a feed's articles, as FreshRSS does.
		var subscribed = Feeds.Select(feed => feed.Id).ToHashSet();
		IEnumerable<Item> items = Items.Where(item => subscribed.Contains(item.FeedId));
		items = stream switch
		{
			"user/-/state/com.google/starred" => items.Where(item => item.Starred),
			"user/-/state/com.google/reading-list" => items,
			_ => items.Where(item => item.FeedId == stream),
		};
		if (First(query, "xt") == "user/-/state/com.google/read")
		{
			items = items.Where(item => !item.Read);
		}
		if (First(query, "ot") is { } since)
		{
			items = items.Where(item => item.Published.ToUnixTimeSeconds() > long.Parse(since, CultureInfo.InvariantCulture));
		}
		var all = items.OrderByDescending(item => item.Id).ToList();
		var offset = First(query, "c") is { } continuation ? int.Parse(continuation, CultureInfo.InvariantCulture) : 0;
		var page = all.Skip(offset).Take(IdsPageSize).ToList();
		var next = offset + page.Count < all.Count ? (offset + page.Count).ToString(CultureInfo.InvariantCulture) : null;
		var response = Json(new { itemRefs = page.Select(item => new { id = item.Id.ToString(CultureInfo.InvariantCulture) }), continuation = next });
		response.Headers.Date = DateTimeOffset.UtcNow;
		return response;
	}

	private static void Apply(Item item, string? state, bool value)
	{
		if (state == "user/-/state/com.google/read")
		{
			item.Read = value;
		}
		else if (state == "user/-/state/com.google/starred")
		{
			item.Starred = value;
		}
	}

	private static long ParseLongId(string id) => unchecked((long)ulong.Parse(id[(id.LastIndexOf('/') + 1)..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));

	private static List<(string Name, string Value)> Parse(string encoded) =>
		[.. encoded.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split('=', 2)).Select(parts =>
			(Decode(parts[0]), parts.Length > 1 ? Decode(parts[1]) : ""))];

	private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

	private static string? First(List<(string Name, string Value)> fields, string name) => fields.FirstOrDefault(field => field.Name == name).Value;

	private static IEnumerable<string> All(List<(string Name, string Value)> fields, string name) => fields.Where(field => field.Name == name).Select(field => field.Value);

	private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

	private static HttpResponseMessage Json(object value) =>
		new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}
