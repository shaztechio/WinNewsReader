using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinNewsReader.Core;

namespace WinNewsReader.Sync.Feedbin;

public class FeedbinException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
	public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>Feedbin turned down the email or password.</summary>
public sealed class FeedbinAuthenticationException(string message) : FeedbinException(message, HttpStatusCode.Unauthorized);

/// <summary>One page of entries, and where the next page is.</summary>
public sealed record FeedbinEntryPage(IReadOnlyList<FeedbinEntry> Entries, string? NextPage, DateTimeOffset? ServerDate);

/// <summary>A Feedbin API client, ported from NetNewsWire's <c>FeedbinAPICaller</c>. Every request carries HTTP Basic credentials.</summary>
public sealed partial class FeedbinCaller : IDisposable
{
	public const string DefaultBaseAddress = "https://api.feedbin.com/v2/";

	private static readonly JsonSerializerOptions JsonOptions = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };

	private readonly HttpClient _http;
	private readonly string _baseAddress;
	private readonly AuthenticationHeaderValue _authorization;

	public FeedbinCaller(string email, string password, HttpMessageHandler? handler = null, string baseAddress = DefaultBaseAddress)
	{
		_baseAddress = baseAddress.EndsWith('/') ? baseAddress : baseAddress + "/";
		_authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{password}")));
		// A 302 from a subscribe request means "already subscribed", so redirects must not be followed.
		_http = handler is null ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) : new HttpClient(handler);
		_http.Timeout = TimeSpan.FromSeconds(60);
		_http.DefaultRequestHeaders.UserAgent.ParseAdd(FeedDownloader.UserAgent);
	}

	/// <summary>Feedbin's date format for <c>since</c>: UTC with six fractional digits.</summary>
	public static string FormatDate(DateTimeOffset date) => date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

	/// <summary>Checks the email and password. Throws <see cref="FeedbinAuthenticationException"/> if Feedbin says no.</summary>
	public async Task ValidateCredentialsAsync(CancellationToken cancellationToken = default)
	{
		using var response = await SendAsync(HttpMethod.Get, "authentication.json", null, cancellationToken, allowStatuses: [HttpStatusCode.Unauthorized]);
		if (response.StatusCode == HttpStatusCode.Unauthorized)
		{
			throw new FeedbinAuthenticationException("The email or password is wrong.");
		}
	}

	public async Task<IReadOnlyList<FeedbinTag>> GetTagsAsync(CancellationToken cancellationToken = default) =>
		await GetJsonAsync<List<FeedbinTag>>("tags.json", cancellationToken) ?? [];

	public async Task<IReadOnlyList<FeedbinSubscription>> GetSubscriptionsAsync(CancellationToken cancellationToken = default) =>
		await GetJsonAsync<List<FeedbinSubscription>>("subscriptions.json?mode=extended", cancellationToken) ?? [];

	public async Task<IReadOnlyList<FeedbinTagging>> GetTaggingsAsync(CancellationToken cancellationToken = default) =>
		await GetJsonAsync<List<FeedbinTagging>>("taggings.json", cancellationToken) ?? [];

	public Task RenameTagAsync(string oldName, string newName, CancellationToken cancellationToken = default) =>
		SendJsonAsync(HttpMethod.Post, "tags.json", new { old_name = oldName, new_name = newName }, cancellationToken);

	/// <summary>NetNewsWire's <c>createSubscription</c>, with its quirk that a 401 here means the feed is already subscribed.</summary>
	public async Task<FeedbinCreateResult> CreateSubscriptionAsync(string feedUrl, CancellationToken cancellationToken = default)
	{
		using var response = await SendAsync(
			HttpMethod.Post, "subscriptions.json?mode=extended", Json(new { feed_url = feedUrl }), cancellationToken,
			allowStatuses: [HttpStatusCode.Unauthorized, HttpStatusCode.NotFound, HttpStatusCode.MultipleChoices, HttpStatusCode.Found]);
		switch (response.StatusCode)
		{
			case HttpStatusCode.Created:
				return new FeedbinCreateResult.Created(await ReadJsonAsync<FeedbinSubscription>(response, cancellationToken) ?? throw new FeedbinException("Feedbin sent no subscription."));
			case HttpStatusCode.MultipleChoices:
				return new FeedbinCreateResult.Choices(await ReadJsonAsync<List<FeedbinSubscriptionChoice>>(response, cancellationToken) ?? []);
			case HttpStatusCode.Found or HttpStatusCode.Unauthorized:
				return new FeedbinCreateResult.AlreadySubscribed();
			case HttpStatusCode.NotFound:
				return new FeedbinCreateResult.NotFound();
			default:
				throw new FeedbinException($"Feedbin answered {(int)response.StatusCode} {response.StatusCode}.", response.StatusCode);
		}
	}

	public Task RenameSubscriptionAsync(long subscriptionId, string name, CancellationToken cancellationToken = default) =>
		SendJsonAsync(HttpMethod.Post, $"subscriptions/{subscriptionId}/update.json", new { title = name }, cancellationToken);

	public async Task DeleteSubscriptionAsync(long subscriptionId, CancellationToken cancellationToken = default)
	{
		using var response = await SendAsync(HttpMethod.Delete, $"subscriptions/{subscriptionId}.json", null, cancellationToken);
	}

	/// <summary>Tags a feed with a folder name; returns the tagging's ID, read from the <c>Location</c> header.</summary>
	public async Task<long> CreateTaggingAsync(long feedId, string name, CancellationToken cancellationToken = default)
	{
		using var response = await SendAsync(HttpMethod.Post, "taggings.json", Json(new { feed_id = feedId, name }), cancellationToken);
		var match = response.Headers.Location is { } location ? TaggingLocation().Match(location.ToString()) : Match.Empty;
		return match.Success
			? long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
			: throw new FeedbinException("Feedbin didn’t say where it saved the tag.");
	}

	public async Task DeleteTaggingAsync(long taggingId, CancellationToken cancellationToken = default)
	{
		using var response = await SendAsync(HttpMethod.Delete, $"taggings/{taggingId}.json", null, cancellationToken);
	}

	public async Task<IReadOnlyList<FeedbinEntry>> GetEntriesAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default)
	{
		if (ids.Count == 0)
		{
			return [];
		}
		return await GetJsonAsync<List<FeedbinEntry>>($"entries.json?ids={string.Join(',', ids)}&mode=extended", cancellationToken) ?? [];
	}

	/// <summary>Entries since a date, 100 to a page; <paramref name="feedId"/> limits it to one feed.</summary>
	public Task<FeedbinEntryPage> GetEntriesSinceAsync(DateTimeOffset since, long? feedId = null, CancellationToken cancellationToken = default) =>
		GetEntryPageAsync(
			(feedId is null ? "entries.json" : $"feeds/{feedId}/entries.json") + $"?since={Uri.EscapeDataString(FormatDate(since))}&per_page=100&mode=extended",
			cancellationToken);

	/// <summary>The page a previous page's <c>Link</c> header pointed to: a full address.</summary>
	public Task<FeedbinEntryPage> GetEntryPageAsync(string pathOrUrl, CancellationToken cancellationToken = default) => GetPageAsync(pathOrUrl, cancellationToken);

	public Task<IReadOnlyList<long>> GetUnreadEntryIdsAsync(CancellationToken cancellationToken = default) => GetIdsAsync("unread_entries.json", cancellationToken);

	public Task<IReadOnlyList<long>> GetStarredEntryIdsAsync(CancellationToken cancellationToken = default) => GetIdsAsync("starred_entries.json", cancellationToken);

	public Task AddUnreadAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default) =>
		SendJsonAsync(HttpMethod.Post, "unread_entries.json", new { unread_entries = ids }, cancellationToken);

	public Task RemoveUnreadAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default) =>
		SendJsonAsync(HttpMethod.Delete, "unread_entries.json", new { unread_entries = ids }, cancellationToken);

	public Task AddStarredAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default) =>
		SendJsonAsync(HttpMethod.Post, "starred_entries.json", new { starred_entries = ids }, cancellationToken);

	public Task RemoveStarredAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default) =>
		SendJsonAsync(HttpMethod.Delete, "starred_entries.json", new { starred_entries = ids }, cancellationToken);

	public void Dispose() => _http.Dispose();

	private async Task<IReadOnlyList<long>> GetIdsAsync(string path, CancellationToken cancellationToken) =>
		await GetJsonAsync<List<long>>(path, cancellationToken) ?? [];

	private async Task<FeedbinEntryPage> GetPageAsync(string pathOrUrl, CancellationToken cancellationToken)
	{
		using var response = await SendAsync(HttpMethod.Get, pathOrUrl, null, cancellationToken);
		var entries = await ReadJsonAsync<List<FeedbinEntry>>(response, cancellationToken) ?? [];
		return new FeedbinEntryPage(entries, NextPage(response), response.Headers.Date);
	}

	/// <summary>The <c>rel="next"</c> address from a <c>Link</c> header.</summary>
	internal static string? NextPage(HttpResponseMessage response)
	{
		if (!response.Headers.TryGetValues("Link", out var values))
		{
			return null;
		}
		var match = NextLink().Match(string.Join(",", values));
		return match.Success ? match.Groups[1].Value : null;
	}

	private async Task<T?> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
	{
		using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
		return await ReadJsonAsync<T>(response, cancellationToken);
	}

	private async Task SendJsonAsync(HttpMethod method, string path, object payload, CancellationToken cancellationToken)
	{
		using var response = await SendAsync(method, path, Json(payload), cancellationToken);
	}

	private static StringContent Json(object payload) => new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

	private async Task<HttpResponseMessage> SendAsync(
		HttpMethod method, string pathOrUrl, HttpContent? content, CancellationToken cancellationToken, HttpStatusCode[]? allowStatuses = null)
	{
		var uri = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? pathOrUrl : _baseAddress + pathOrUrl;
		using var request = new HttpRequestMessage(method, uri) { Content = content };
		request.Headers.Authorization = _authorization;
		HttpResponseMessage response;
		try
		{
			response = await _http.SendAsync(request, cancellationToken);
		}
		catch (HttpRequestException ex)
		{
			throw new FeedbinException($"Couldn’t reach Feedbin: {ex.Message}");
		}
		catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new FeedbinException("Feedbin took too long to answer.");
		}
		if (response.IsSuccessStatusCode || allowStatuses?.Contains(response.StatusCode) == true)
		{
			return response;
		}
		var status = response.StatusCode;
		response.Dispose();
		throw status switch
		{
			HttpStatusCode.Unauthorized => new FeedbinAuthenticationException("The email or password is wrong."),
			HttpStatusCode.Forbidden => new FeedbinException("Feedbin says too many requests were made. Wait five minutes and try again.", status),
			_ => new FeedbinException($"Feedbin answered {(int)status} {status}.", status),
		};
	}

	private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		try
		{
			await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
			return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
		}
		catch (JsonException)
		{
			throw new FeedbinException("Feedbin’s answer wasn’t the JSON it should send.");
		}
	}

	[GeneratedRegex(@"v2/taggings/(\d+)\.json")]
	private static partial Regex TaggingLocation();

	[GeneratedRegex("<([^>]+)>\\s*;\\s*rel=\"?next\"?")]
	private static partial Regex NextLink();
}
