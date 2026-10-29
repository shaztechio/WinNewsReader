using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using WinNewsReader.Core;

namespace WinNewsReader.Sync.NewsBlur;

public class NewsBlurException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
	public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>NewsBlur turned down the username or password.</summary>
public sealed class NewsBlurAuthenticationException(string message) : NewsBlurException(message, HttpStatusCode.Unauthorized);

public sealed record NewsBlurFeed(long Id, string Name, string Url, string? HomePageUrl);

/// <summary>A folder as NewsBlur lists it: by name, with the feeds in it. The name " " is the top level.</summary>
public sealed record NewsBlurFolder(string Name, IReadOnlyList<long> FeedIds);

public sealed record NewsBlurStory(
	string Hash, long FeedId, string? Title, string? Url, string? Author, string? Content, DateTimeOffset? Published, string? ImageUrl, IReadOnlyList<string> Tags);

/// <summary>
/// A NewsBlur API client, ported from NetNewsWire's <c>NewsBlurAPICaller</c>. It logs in with the username and password,
/// keeps the <c>newsblur_sessionid</c> cookie it's given, and logs in again if the session runs out.
/// </summary>
public sealed class NewsBlurCaller : IDisposable
{
	public const string DefaultBaseAddress = "https://www.newsblur.com/";

	/// <summary>The name NewsBlur gives the top level in its folder list.</summary>
	public const string TopLevelFolder = " ";

	private const string SessionCookie = "newsblur_sessionid";

	private readonly HttpClient _http;
	private readonly string _baseAddress;
	private readonly string _username;
	private readonly string _password;
	private string? _session;

	public NewsBlurCaller(string username, string password, HttpMessageHandler? handler = null, string baseAddress = DefaultBaseAddress)
	{
		_baseAddress = baseAddress.EndsWith('/') ? baseAddress : baseAddress + "/";
		_username = username;
		_password = password;
		_http = handler is null ? new HttpClient(new SocketsHttpHandler { UseCookies = false }) : new HttpClient(handler);
		_http.Timeout = TimeSpan.FromSeconds(60);
		_http.DefaultRequestHeaders.UserAgent.ParseAdd(FeedDownloader.UserAgent);
	}

	/// <summary>Logs in, so the account can check the username and password when it's added.</summary>
	public async Task LoginAsync(CancellationToken cancellationToken = default)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, _baseAddress + "api/login") { Content = Form(("username", _username), ("password", _password)) };
		using var response = await SendRawAsync(request, cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			throw new NewsBlurException($"NewsBlur answered {(int)response.StatusCode} {response.StatusCode}.", response.StatusCode);
		}
		using var document = await ReadDocumentAsync(response, cancellationToken);
		var root = document.RootElement;
		if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() == -1)
		{
			var message = FirstError(root, "username") ?? FirstError(root, "__all__") ?? "NewsBlur turned down the username or password.";
			throw new NewsBlurAuthenticationException(message);
		}
		_session = SessionFrom(response) ?? throw new NewsBlurAuthenticationException("NewsBlur didn’t start a session.");
	}

	public async Task<(IReadOnlyList<NewsBlurFeed> Feeds, IReadOnlyList<NewsBlurFolder> Folders)> GetFeedsAsync(CancellationToken cancellationToken = default)
	{
		using var document = await GetAsync("reader/feeds?flat=true&update_counts=true", cancellationToken);
		var root = document.RootElement;
		var folders = new List<NewsBlurFolder>();
		if (root.TryGetProperty("flat_folders", out var flat) && flat.ValueKind == JsonValueKind.Object)
		{
			folders.AddRange(flat.EnumerateObject().Select(folder => new NewsBlurFolder(folder.Name, [.. folder.Value.EnumerateArray().Select(id => id.GetInt64())])));
		}
		// Some feeds are listed that no folder holds; NewsBlur's own apps don't show them, so neither does NetNewsWire.
		var visible = folders.SelectMany(folder => folder.FeedIds).ToHashSet();
		var feeds = new List<NewsBlurFeed>();
		if (root.TryGetProperty("feeds", out var feedObject) && feedObject.ValueKind == JsonValueKind.Object)
		{
			foreach (var property in feedObject.EnumerateObject())
			{
				var feed = ReadFeed(property.Value);
				if (feed is not null && visible.Contains(feed.Id))
				{
					feeds.Add(feed);
				}
			}
		}
		return (feeds, folders);
	}

	public async Task<IReadOnlyList<string>> GetUnreadStoryHashesAsync(CancellationToken cancellationToken = default)
	{
		using var document = await GetAsync("reader/unread_story_hashes?include_timestamps=true", cancellationToken);
		var hashes = new List<string>();
		if (document.RootElement.TryGetProperty("unread_feed_story_hashes", out var byFeed) && byFeed.ValueKind == JsonValueKind.Object)
		{
			foreach (var feed in byFeed.EnumerateObject())
			{
				hashes.AddRange(feed.Value.EnumerateArray().Select(pair => pair[0].GetString()!));
			}
		}
		return hashes;
	}

	public async Task<IReadOnlyList<string>> GetStarredStoryHashesAsync(CancellationToken cancellationToken = default)
	{
		using var document = await GetAsync("reader/starred_story_hashes?include_timestamps=true", cancellationToken);
		return document.RootElement.TryGetProperty("starred_story_hashes", out var starred) && starred.ValueKind == JsonValueKind.Array
			? [.. starred.EnumerateArray().Select(pair => pair[0].GetString()!)]
			: [];
	}

	/// <summary>The stories for up to 100 hashes — NewsBlur's limit.</summary>
	public async Task<IReadOnlyList<NewsBlurStory>> GetStoriesAsync(IEnumerable<string> hashes, CancellationToken cancellationToken = default)
	{
		var query = "include_hidden=false" + string.Concat(hashes.Select(hash => "&h=" + Uri.EscapeDataString(hash)));
		using var document = await GetAsync("reader/river_stories?" + query, cancellationToken);
		return document.RootElement.TryGetProperty("stories", out var stories) && stories.ValueKind == JsonValueKind.Array
			? [.. stories.EnumerateArray().Select(ReadStory)]
			: [];
	}

	public Task MarkAsUnreadAsync(IEnumerable<string> hashes, CancellationToken cancellationToken = default) => PostAsync("reader/mark_story_hash_as_unread", hashes.Select(Hash), cancellationToken);

	public Task MarkAsReadAsync(IEnumerable<string> hashes, CancellationToken cancellationToken = default) => PostAsync("reader/mark_story_hashes_as_read", hashes.Select(Hash), cancellationToken);

	public Task StarAsync(IEnumerable<string> hashes, CancellationToken cancellationToken = default) => PostAsync("reader/mark_story_hash_as_starred", hashes.Select(Hash), cancellationToken);

	public Task UnstarAsync(IEnumerable<string> hashes, CancellationToken cancellationToken = default) => PostAsync("reader/mark_story_hash_as_unstarred", hashes.Select(Hash), cancellationToken);

	public Task AddFolderAsync(string name, CancellationToken cancellationToken = default) =>
		PostAsync("reader/add_folder", [("folder", name), ("parent_folder", "")], cancellationToken);

	public Task RenameFolderAsync(string from, string to, CancellationToken cancellationToken = default) =>
		PostAsync("reader/rename_folder", [("folder_to_rename", from), ("new_folder_name", to), ("in_folder", "")], cancellationToken);

	public Task RemoveFolderAsync(string name, IEnumerable<long> feedIds, CancellationToken cancellationToken = default) =>
		PostAsync("reader/delete_folder", [("folder_to_delete", name), ("in_folder", ""), .. feedIds.Select(id => ("feed_id", Id(id)))], cancellationToken);

	/// <summary>Subscribes, in a folder if one is given. Returns the feed NewsBlur made, or null if it found none.</summary>
	public async Task<NewsBlurFeed?> AddUrlAsync(string url, string? folder, CancellationToken cancellationToken = default)
	{
		var fields = new List<(string, string)> { ("url", url) };
		if (folder is not null)
		{
			fields.Add(("folder", folder));
		}
		using var document = await SendJsonAsync("reader/add_url", fields, cancellationToken);
		return document.RootElement.TryGetProperty("feed", out var feed) && feed.ValueKind == JsonValueKind.Object ? ReadFeed(feed) : null;
	}

	public Task RenameFeedAsync(long feedId, string name, CancellationToken cancellationToken = default) =>
		PostAsync("reader/rename_feed", [("feed_id", Id(feedId)), ("feed_title", name)], cancellationToken);

	/// <summary>Unsubscribes; with a folder, only from that folder.</summary>
	public Task DeleteFeedAsync(long feedId, string? folder, CancellationToken cancellationToken = default) =>
		PostAsync("reader/delete_feed", folder is null ? [("feed_id", Id(feedId))] : [("feed_id", Id(feedId)), ("in_folder", folder)], cancellationToken);

	public Task MoveFeedAsync(long feedId, string? from, string? to, CancellationToken cancellationToken = default) =>
		PostAsync("reader/move_feed_to_folder", [("feed_id", Id(feedId)), ("in_folder", from ?? ""), ("to_folder", to ?? "")], cancellationToken);

	public void Dispose() => _http.Dispose();

	private static (string, string) Hash(string hash) => ("story_hash", hash);

	private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

	private static NewsBlurFeed? ReadFeed(JsonElement element) =>
		element.TryGetProperty("id", out var id) && element.TryGetProperty("feed_address", out var address)
			? new NewsBlurFeed(
				id.GetInt64(), Text(element, "feed_title") ?? "", address.GetString() ?? "", Text(element, "feed_link"))
			: null;

	private static NewsBlurStory ReadStory(JsonElement story)
	{
		var published = double.TryParse(Text(story, "story_timestamp"), CultureInfo.InvariantCulture, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds((long)seconds) : (DateTimeOffset?)null;
		string? image = null;
		if (story.TryGetProperty("image_urls", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
		{
			var first = images[0].GetString();
			image = first is not null && story.TryGetProperty("secure_image_urls", out var secure) && secure.ValueKind == JsonValueKind.Object
				&& secure.TryGetProperty(first, out var proxied) ? proxied.GetString() : first;
		}
		var tags = story.TryGetProperty("story_tags", out var tagArray) && tagArray.ValueKind == JsonValueKind.Array
			? tagArray.EnumerateArray().Select(tag => tag.GetString()).OfType<string>().ToList()
			: [];
		return new NewsBlurStory(
			Text(story, "story_hash") ?? "", story.GetProperty("story_feed_id").GetInt64(), Text(story, "story_title"), Text(story, "story_permalink"),
			Text(story, "story_authors"), Text(story, "story_content"), published, image, tags);
	}

	private static string? Text(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) ? value.ValueKind switch
		{
			JsonValueKind.String => value.GetString(),
			JsonValueKind.Number => value.GetRawText(),
			_ => null,
		} : null;

	private static string? FirstError(JsonElement root, string name) =>
		root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object && errors.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
			? list.EnumerateArray().Select(message => message.GetString()).FirstOrDefault()
			: null;

	private static string? SessionFrom(HttpResponseMessage response)
	{
		if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
		{
			return null;
		}
		var prefix = SessionCookie + "=";
		return cookies.Select(cookie => cookie.Trim()).FirstOrDefault(cookie => cookie.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..].Split(';')[0];
	}

	private Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken) => SendJsonAsync(path, null, cancellationToken);

	private async Task PostAsync(string path, IEnumerable<(string, string)> fields, CancellationToken cancellationToken)
	{
		using var document = await SendJsonAsync(path, fields, cancellationToken);
	}

	/// <summary>A GET, or a form POST if there are fields, logging in first if needed and again once if the session is refused.</summary>
	private async Task<JsonDocument> SendJsonAsync(string path, IEnumerable<(string, string)>? fields, CancellationToken cancellationToken)
	{
		var fieldList = fields?.ToList();
		if (_session is null)
		{
			await LoginAsync(cancellationToken);
		}
		var response = await SendWithSessionAsync(path, fieldList, cancellationToken);
		if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
		{
			response.Dispose();
			await LoginAsync(cancellationToken);
			response = await SendWithSessionAsync(path, fieldList, cancellationToken);
		}
		using (response)
		{
			if (!response.IsSuccessStatusCode)
			{
				throw new NewsBlurException($"NewsBlur answered {(int)response.StatusCode} {response.StatusCode}.", response.StatusCode);
			}
			return await ReadDocumentAsync(response, cancellationToken);
		}
	}

	private async Task<HttpResponseMessage> SendWithSessionAsync(string path, List<(string, string)>? fields, CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(fields is null ? HttpMethod.Get : HttpMethod.Post, _baseAddress + path) { Content = fields is null ? null : Form([.. fields]) };
		request.Headers.TryAddWithoutValidation("Cookie", $"{SessionCookie}={_session}");
		return await SendRawAsync(request, cancellationToken);
	}

	private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		try
		{
			return await _http.SendAsync(request, cancellationToken);
		}
		catch (HttpRequestException ex)
		{
			throw new NewsBlurException($"Couldn’t reach NewsBlur: {ex.Message}");
		}
		catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new NewsBlurException("NewsBlur took too long to answer.");
		}
	}

	private static async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		try
		{
			await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
			return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
		}
		catch (JsonException)
		{
			throw new NewsBlurException("NewsBlur’s answer wasn’t the JSON it should send.");
		}
	}

	private static FormUrlEncodedContent Form(params IEnumerable<(string Name, string Value)> fields) =>
		new(fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)));
}
