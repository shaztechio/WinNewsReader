using System.Net;
using System.Text;
using System.Text.Json;

namespace WinNewsReader.Sync.NewsBlur.Tests;

/// <summary>An in-memory NewsBlur server, answering the calls the client makes.</summary>
internal sealed class FakeNewsBlurServer : HttpMessageHandler
{
	public const string Username = "alice";
	public const string Password = "secret";
	public const string Session = "session-1";

	private long _nextFeed = 100;

	public sealed class Feed
	{
		public required long Id { get; init; }
		public required string Title { get; set; }
		public required string Url { get; init; }
		public string Folder { get; set; } = " ";
	}

	public sealed class Story
	{
		public required string Hash { get; init; }
		public required long FeedId { get; init; }
		public required string Title { get; init; }
		public bool Unread { get; set; }
		public bool Starred { get; set; }
	}

	public List<Feed> Feeds { get; } = [];

	public HashSet<string> Folders { get; } = [];

	public List<Story> Stories { get; } = [];

	public List<string> Requests { get; } = [];

	/// <summary>Feeds that <c>add_url</c> can find, by address.</summary>
	public Dictionary<string, string> KnownFeeds { get; } = [];

	/// <summary>How many times the session has been refused, to test logging in again.</summary>
	public int RefuseNextRequests { get; set; }

	public int LoginCount { get; private set; }

	public Feed AddFeed(string title, string url, string folder = " ")
	{
		var feed = new Feed { Id = _nextFeed++, Title = title, Url = url, Folder = folder };
		Feeds.Add(feed);
		if (folder != " ")
		{
			Folders.Add(folder);
		}
		return feed;
	}

	public Story AddStory(string hash, Feed feed, string title, bool unread = false, bool starred = false)
	{
		var story = new Story { Hash = hash, FeedId = feed.Id, Title = title, Unread = unread, Starred = starred };
		Stories.Add(story);
		return story;
	}

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var path = request.RequestUri!.AbsolutePath.TrimStart('/');
		var form = request.Content is null ? [] : System.Web.HttpUtility.ParseQueryString(await request.Content.ReadAsStringAsync(cancellationToken));
		var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
		Requests.Add($"{request.Method} {path}");

		if (path == "api/login")
		{
			if (form["username"] != Username || form["password"] != Password)
			{
				return Json(new { code = -1, errors = new { __all__ = new[] { "Wrong username or password." } } });
			}
			LoginCount++;
			var ok = Json(new { code = 1, authenticated = true });
			ok.Headers.TryAddWithoutValidation("Set-Cookie", $"newsblur_sessionid={Session}; Path=/; HttpOnly");
			return ok;
		}
		if (RefuseNextRequests > 0)
		{
			RefuseNextRequests--;
			return new HttpResponseMessage(HttpStatusCode.Forbidden);
		}
		if (!request.Headers.TryGetValues("Cookie", out var cookies) || !cookies.Any(cookie => cookie.Contains($"newsblur_sessionid={Session}", StringComparison.Ordinal)))
		{
			return new HttpResponseMessage(HttpStatusCode.Forbidden);
		}

		switch (path)
		{
			case "reader/feeds":
				var flat = Folders.ToDictionary(folder => folder, folder => Feeds.Where(f => f.Folder == folder).Select(f => f.Id).ToArray());
				var top = Feeds.Where(f => f.Folder == " ").Select(f => f.Id).ToArray();
				if (top.Length > 0)
				{
					flat[" "] = top;
				}
				return Json(new
				{
					feeds = Feeds.ToDictionary(f => f.Id.ToString(), f => new { id = f.Id, feed_title = f.Title, feed_address = f.Url, feed_link = "https://site.example/" }),
					flat_folders = flat,
				});
			case "reader/unread_story_hashes":
				return Json(new { unread_feed_story_hashes = Stories.Where(s => s.Unread).GroupBy(s => s.FeedId).ToDictionary(g => g.Key.ToString(), g => g.Select(s => new object[] { s.Hash, 1700000000 }).ToArray()) });
			case "reader/starred_story_hashes":
				return Json(new { starred_story_hashes = Stories.Where(s => s.Starred).Select(s => new object[] { s.Hash, "1700000000.0" }).ToArray() });
			case "reader/river_stories":
				var wanted = query.GetValues("h")?.ToHashSet() ?? [];
				return Json(new
				{
					stories = Stories.Where(s => wanted.Contains(s.Hash)).Select(s => new
					{
						story_hash = s.Hash,
						story_feed_id = s.FeedId,
						story_title = s.Title,
						story_permalink = $"https://site.example/{s.Hash}",
						story_authors = "Pat",
						story_content = $"<p>{s.Title}</p>",
						story_timestamp = "1700000000",
						story_tags = new[] { "tag" },
					}),
				});
			case "reader/mark_story_hash_as_unread" or "reader/mark_story_hashes_as_read" or "reader/mark_story_hash_as_starred" or "reader/mark_story_hash_as_unstarred":
				foreach (var hash in form.GetValues("story_hash") ?? [])
				{
					var story = Stories.Single(s => s.Hash == hash);
					switch (path)
					{
						case "reader/mark_story_hash_as_unread": story.Unread = true; break;
						case "reader/mark_story_hashes_as_read": story.Unread = false; break;
						case "reader/mark_story_hash_as_starred": story.Starred = true; break;
						default: story.Starred = false; break;
					}
				}
				return Json(new { result = "ok" });
			case "reader/add_folder":
				Folders.Add(form["folder"]!);
				return Json(new { result = "ok" });
			case "reader/rename_folder":
				Folders.Remove(form["folder_to_rename"]!);
				Folders.Add(form["new_folder_name"]!);
				foreach (var feed in Feeds.Where(f => f.Folder == form["folder_to_rename"]))
				{
					feed.Folder = form["new_folder_name"]!;
				}
				return Json(new { result = "ok" });
			case "reader/delete_folder":
				Folders.Remove(form["folder_to_delete"]!);
				var gone = (form.GetValues("feed_id") ?? []).Select(long.Parse).ToHashSet();
				Feeds.RemoveAll(f => gone.Contains(f.Id));
				return Json(new { result = "ok" });
			case "reader/add_url":
				var url = form["url"]!;
				if (!KnownFeeds.TryGetValue(url, out var title))
				{
					return Json(new { result = "error", code = -1 });
				}
				var added = AddFeed(title, url, form["folder"] ?? " ");
				return Json(new { result = "ok", feed = new { id = added.Id, feed_title = added.Title, feed_address = added.Url, feed_link = "https://site.example/" } });
			case "reader/rename_feed":
				Feeds.Single(f => f.Id == long.Parse(form["feed_id"]!)).Title = form["feed_title"]!;
				return Json(new { result = "ok" });
			case "reader/delete_feed":
				Feeds.RemoveAll(f => f.Id == long.Parse(form["feed_id"]!));
				return Json(new { result = "ok" });
			case "reader/move_feed_to_folder":
				Feeds.Single(f => f.Id == long.Parse(form["feed_id"]!)).Folder = string.IsNullOrEmpty(form["to_folder"]) ? " " : form["to_folder"]!;
				return Json(new { result = "ok" });
			default:
				return new HttpResponseMessage(HttpStatusCode.NotFound);
		}
	}

	private static HttpResponseMessage Json(object value) =>
		new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}
