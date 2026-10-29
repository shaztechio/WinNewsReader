using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinNewsReader.Sync.Feedbin.Tests;

/// <summary>An in-memory Feedbin API v2 server, answering the calls the client makes.</summary>
internal sealed partial class FakeFeedbinServer : HttpMessageHandler
{
	public const string Email = "alice@example.com";
	public const string Password = "secret";

	private long _nextId = 1000;

	public sealed class Sub
	{
		public required long SubscriptionId { get; init; }
		public required long FeedId { get; init; }
		public required string Title { get; set; }
		public required string Url { get; init; }
	}

	public sealed class Entry
	{
		public required long Id { get; init; }
		public required long FeedId { get; init; }
		public required string Title { get; init; }
		public DateTimeOffset Published { get; init; } = DateTimeOffset.UtcNow.AddHours(-1);
		public bool Unread { get; set; }
		public bool Starred { get; set; }
	}

	public List<Sub> Subscriptions { get; } = [];

	/// <summary>Tagging ID, feed ID, tag name.</summary>
	public List<(long Id, long FeedId, string Name)> Taggings { get; } = [];

	public List<Entry> Entries { get; } = [];

	public List<string> Requests { get; } = [];

	/// <summary>Entries per page of an entries request, to exercise the <c>Link</c> header.</summary>
	public int PageSize { get; set; } = 100;

	/// <summary>The feeds a subscribe request can find, by address typed.</summary>
	public Dictionary<string, string> KnownFeeds { get; } = [];

	/// <summary>Addresses that Feedbin answers with a choice of feeds (a 300), to the feed address offered.</summary>
	public Dictionary<string, string> Choices { get; } = [];

	public bool FailStatusChanges { get; set; }

	public Sub AddSubscription(string title, string url, params string[] tags)
	{
		var sub = new Sub { SubscriptionId = _nextId++, FeedId = _nextId++, Title = title, Url = url };
		Subscriptions.Add(sub);
		foreach (var tag in tags)
		{
			Taggings.Add((_nextId++, sub.FeedId, tag));
		}
		return sub;
	}

	public Entry AddEntry(long id, Sub sub, string title, bool unread = false, bool starred = false, DateTimeOffset? published = null)
	{
		var entry = new Entry { Id = id, FeedId = sub.FeedId, Title = title, Unread = unread, Starred = starred, Published = published ?? DateTimeOffset.UtcNow.AddHours(-1) };
		Entries.Add(entry);
		return entry;
	}

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var uri = request.RequestUri!;
		var path = uri.AbsolutePath.Replace("/v2/", "", StringComparison.Ordinal);
		var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
		Requests.Add($"{request.Method} {path}{uri.Query}");

		var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Email}:{Password}"));
		if (request.Headers.Authorization?.ToString() != expected)
		{
			return new HttpResponseMessage(HttpStatusCode.Unauthorized);
		}
		var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

		if (path == "authentication.json")
		{
			return Ok("");
		}
		if (path == "tags.json" && request.Method == HttpMethod.Get)
		{
			return Json(Taggings.Select(t => t.Name).Distinct().Select((name, index) => new { id = index + 1, name }));
		}
		if (path == "tags.json")
		{
			using var rename = JsonDocument.Parse(body);
			var (oldName, newName) = (rename.RootElement.GetProperty("old_name").GetString(), rename.RootElement.GetProperty("new_name").GetString());
			for (var i = 0; i < Taggings.Count; i++)
			{
				if (Taggings[i].Name == oldName)
				{
					Taggings[i] = Taggings[i] with { Name = newName! };
				}
			}
			return Ok("[]");
		}
		if (path == "subscriptions.json" && request.Method == HttpMethod.Get)
		{
			return Json(Subscriptions.Select(s => new { id = s.SubscriptionId, feed_id = s.FeedId, title = s.Title, feed_url = s.Url, site_url = "https://site.example/" }));
		}
		if (path == "subscriptions.json")
		{
			using var create = JsonDocument.Parse(body);
			var url = create.RootElement.GetProperty("feed_url").GetString()!;
			if (Subscriptions.Any(s => s.Url == url))
			{
				return new HttpResponseMessage(HttpStatusCode.Found);
			}
			if (Choices.TryGetValue(url, out var actual))
			{
				return Json(new[] { new { title = "Main", feed_url = actual } }, HttpStatusCode.MultipleChoices);
			}
			if (!KnownFeeds.ContainsKey(url))
			{
				return new HttpResponseMessage(HttpStatusCode.NotFound);
			}
			var sub = AddSubscription(KnownFeeds[url], url);
			return Json(new { id = sub.SubscriptionId, feed_id = sub.FeedId, title = sub.Title, feed_url = sub.Url, site_url = "https://site.example/" }, HttpStatusCode.Created);
		}
		if (SubscriptionUpdate().Match(path) is { Success: true } update)
		{
			using var title = JsonDocument.Parse(body);
			Subscriptions.Single(s => s.SubscriptionId == long.Parse(update.Groups[1].Value)).Title = title.RootElement.GetProperty("title").GetString()!;
			return Ok("{}");
		}
		if (SubscriptionDelete().Match(path) is { Success: true } delete && request.Method == HttpMethod.Delete)
		{
			var id = long.Parse(delete.Groups[1].Value);
			var feedId = Subscriptions.Single(s => s.SubscriptionId == id).FeedId;
			Subscriptions.RemoveAll(s => s.SubscriptionId == id);
			Taggings.RemoveAll(t => t.FeedId == feedId);
			return new HttpResponseMessage(HttpStatusCode.NoContent);
		}
		if (path == "taggings.json" && request.Method == HttpMethod.Get)
		{
			return Json(Taggings.Select(t => new { id = t.Id, feed_id = t.FeedId, name = t.Name }));
		}
		if (path == "taggings.json")
		{
			using var tag = JsonDocument.Parse(body);
			var id = _nextId++;
			Taggings.Add((id, tag.RootElement.GetProperty("feed_id").GetInt64(), tag.RootElement.GetProperty("name").GetString()!));
			var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") };
			response.Headers.Location = new Uri($"https://api.feedbin.com/v2/taggings/{id}.json");
			return response;
		}
		if (TaggingDelete().Match(path) is { Success: true } untag)
		{
			Taggings.RemoveAll(t => t.Id == long.Parse(untag.Groups[1].Value));
			return new HttpResponseMessage(HttpStatusCode.NoContent);
		}
		if (path == "unread_entries.json" || path == "starred_entries.json")
		{
			return StatusList(request.Method, path == "unread_entries.json", body);
		}
		if (path == "entries.json" || FeedEntries().IsMatch(path))
		{
			return EntriesResponse(path, query);
		}
		return new HttpResponseMessage(HttpStatusCode.NotFound);
	}

	private HttpResponseMessage StatusList(HttpMethod method, bool unread, string body)
	{
		if (method == HttpMethod.Get)
		{
			return Json(Entries.Where(e => unread ? e.Unread : e.Starred).Select(e => e.Id));
		}
		if (FailStatusChanges)
		{
			return new HttpResponseMessage(HttpStatusCode.InternalServerError);
		}
		using var document = JsonDocument.Parse(body);
		var ids = document.RootElement.GetProperty(unread ? "unread_entries" : "starred_entries").EnumerateArray().Select(id => id.GetInt64()).ToHashSet();
		foreach (var entry in Entries.Where(e => ids.Contains(e.Id)))
		{
			if (unread)
			{
				entry.Unread = method == HttpMethod.Post;
			}
			else
			{
				entry.Starred = method == HttpMethod.Post;
			}
		}
		return Json(ids);
	}

	private HttpResponseMessage EntriesResponse(string path, System.Collections.Specialized.NameValueCollection query)
	{
		IEnumerable<Entry> entries = Entries;
		if (FeedEntries().Match(path) is { Success: true } feed)
		{
			entries = entries.Where(e => e.FeedId == long.Parse(feed.Groups[1].Value));
		}
		if (query["ids"] is { } ids)
		{
			var wanted = ids.Split(',').Select(long.Parse).ToHashSet();
			entries = entries.Where(e => wanted.Contains(e.Id));
		}
		var page = int.TryParse(query["page"], out var number) ? number : 1;
		var all = entries.ToList();
		var pageEntries = all.Skip((page - 1) * PageSize).Take(PageSize).ToList();
		var response = Json(pageEntries.Select(e => new
		{
			id = e.Id,
			feed_id = e.FeedId,
			title = e.Title,
			url = $"https://site.example/{e.Id}",
			author = "Pat",
			content = $"<p>{e.Title}</p>",
			summary = e.Title,
			published = e.Published.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'"),
		}));
		response.Headers.Date = DateTimeOffset.UtcNow;
		if (page * PageSize < all.Count)
		{
			var next = $"https://api.feedbin.com/v2/{path}?page={page + 1}" + (query["ids"] is { } i ? $"&ids={i}" : "");
			response.Headers.TryAddWithoutValidation("Link", $"<{next}>; rel=\"next\"");
		}
		return response;
	}

	private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
		new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

	[GeneratedRegex(@"^subscriptions/(\d+)/update\.json$")]
	private static partial Regex SubscriptionUpdate();

	[GeneratedRegex(@"^subscriptions/(\d+)\.json$")]
	private static partial Regex SubscriptionDelete();

	[GeneratedRegex(@"^taggings/(\d+)\.json$")]
	private static partial Regex TaggingDelete();

	[GeneratedRegex(@"^feeds/(\d+)/entries\.json$")]
	private static partial Regex FeedEntries();
}
