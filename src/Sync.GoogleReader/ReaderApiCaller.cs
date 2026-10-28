using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WinNewsReader.Core;

namespace WinNewsReader.Sync.GoogleReader;

public class ReaderApiException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
	public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>The server turned down the username or password.</summary>
public sealed class ReaderApiAuthenticationException(string message) : ReaderApiException(message, HttpStatusCode.Unauthorized);

public enum ReaderApiItemIdType
{
	Unread,
	Starred,
	AllForAccount,
	AllForFeed,
}

/// <summary>
/// A Google Reader API client, ported from NetNewsWire's <c>ReaderAPICaller</c> for FreshRSS and other servers that
/// speak the same API. Logs in with <c>ClientLogin</c>, and fetches a write token for changes.
/// </summary>
public sealed class ReaderApiCaller : IDisposable
{
	/// <summary>Item IDs per request; the server pages beyond this with a continuation.</summary>
	private const int ItemIdsPageSize = 1000;

	private const string ReadState = "user/-/state/com.google/read";
	private const string StarredState = "user/-/state/com.google/starred";
	private const string ReadingList = "user/-/state/com.google/reading-list";

	private static readonly JsonSerializerOptions JsonOptions = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };

	private readonly HttpClient _http;
	private readonly string _endpoint;
	private readonly string _username;
	private readonly string _password;
	private string? _auth;
	private string? _writeToken;

	public ReaderApiCaller(string endpoint, string username, string password, HttpMessageHandler? handler = null)
	{
		_endpoint = endpoint.TrimEnd('/');
		_username = username;
		_password = password;
		_http = handler is null ? new HttpClient() : new HttpClient(handler);
		_http.Timeout = TimeSpan.FromSeconds(60);
		_http.DefaultRequestHeaders.UserAgent.ParseAdd(FeedDownloader.UserAgent);
	}

	/// <summary>
	/// The API address for what someone typed: a FreshRSS server's address gets <c>/api/greader.php</c> added,
	/// and an address without a scheme gets <c>https://</c>.
	/// </summary>
	public static string NormalizedEndpoint(string serverUrl)
	{
		var url = serverUrl.Trim().TrimEnd('/');
		if (!url.Contains("://", StringComparison.Ordinal))
		{
			url = "https://" + url;
		}
		return url.EndsWith("greader.php", StringComparison.OrdinalIgnoreCase) ? url : url + "/api/greader.php";
	}

	/// <summary>
	/// The long form of an article ID for requests: zero-padded 16-digit hex, two's complement for negative IDs.
	/// Null for an ID that isn't a number, which this server can't take.
	/// </summary>
	public static string? LongItemId(string articleId) =>
		long.TryParse(articleId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id)
			? "tag:google.com,2005:reader/item/" + id.ToString("x16", CultureInfo.InvariantCulture)
			: null;

	/// <summary>NetNewsWire's <c>uniqueID</c>: the article ID, in decimal, from an entry's long-form hex ID.</summary>
	public static string ArticleIdFromEntryId(string entryId)
	{
		var hex = entryId[(entryId.LastIndexOf('/') + 1)..];
		return ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
			? unchecked((long)value).ToString(CultureInfo.InvariantCulture)
			: entryId;
	}

	/// <summary>Logs in, so the account can check the username and password when it's added.</summary>
	public async Task LoginAsync(CancellationToken cancellationToken = default)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint + "/accounts/ClientLogin")
		{
			Content = Form(("Email", _username), ("Passwd", _password)),
		};
		using var response = await SendRawAsync(request, cancellationToken);
		if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
		{
			throw new ReaderApiAuthenticationException("The username or API password is wrong.");
		}
		if (response.StatusCode == HttpStatusCode.NotFound)
		{
			throw new ReaderApiException($"There’s no Reader API at {_endpoint}. Check the address, and that the API is turned on in the server’s settings.", response.StatusCode);
		}
		EnsureSuccess(response);
		var body = await response.Content.ReadAsStringAsync(cancellationToken);
		_auth = body.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("Auth=", StringComparison.Ordinal))?["Auth=".Length..]
			?? throw new ReaderApiAuthenticationException("The server didn’t return a login token.");
	}

	public async Task<IReadOnlyList<ReaderApiTag>> GetTagsAsync(CancellationToken cancellationToken = default) =>
		(await GetJsonAsync<ReaderApiTagList>("/reader/api/0/tag/list?output=json", cancellationToken))?.Tags ?? [];

	public async Task<IReadOnlyList<ReaderApiSubscription>> GetSubscriptionsAsync(CancellationToken cancellationToken = default) =>
		(await GetJsonAsync<ReaderApiSubscriptionList>("/reader/api/0/subscription/list?output=json", cancellationToken))?.Subscriptions ?? [];

	/// <summary>
	/// Article IDs of one kind, following continuations. For <see cref="ReaderApiItemIdType.AllForAccount"/> and
	/// <see cref="ReaderApiItemIdType.AllForFeed"/>, only articles newer than <paramref name="since"/>. Also returns the
	/// server's clock from the first response, which NetNewsWire uses as the next <paramref name="since"/>.
	/// </summary>
	public async Task<(IReadOnlyList<string> Ids, DateTimeOffset? ServerDate)> GetItemIdsAsync(
		ReaderApiItemIdType type, DateTimeOffset since = default, string? feedId = null, CancellationToken cancellationToken = default)
	{
		var query = new List<(string, string)> { ("n", ItemIdsPageSize.ToString(CultureInfo.InvariantCulture)), ("output", "json") };
		switch (type)
		{
			case ReaderApiItemIdType.AllForAccount:
				query.Add(("ot", since.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
				query.Add(("s", ReadingList));
				break;
			case ReaderApiItemIdType.AllForFeed:
				query.Add(("ot", since.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
				query.Add(("s", feedId ?? throw new ArgumentNullException(nameof(feedId))));
				break;
			case ReaderApiItemIdType.Unread:
				query.Add(("s", ReadingList));
				query.Add(("xt", ReadState));
				break;
			case ReaderApiItemIdType.Starred:
				query.Add(("s", StarredState));
				break;
		}

		var ids = new List<string>();
		DateTimeOffset? serverDate = null;
		string? continuation = null;
		do
		{
			var pageQuery = continuation is null ? query : [.. query, ("c", continuation)];
			using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, _endpoint + "/reader/api/0/stream/items/ids?" + QueryString(pageQuery)), cancellationToken);
			serverDate ??= response.Headers.Date;
			var page = await ReadJsonAsync<ReaderApiItemRefs>(response, cancellationToken);
			ids.AddRange(page?.ItemRefs?.Select(itemRef => itemRef.Id).OfType<string>() ?? []);
			continuation = page?.Continuation is { Length: > 0 } next ? next : null;
		}
		while (continuation is not null);
		return (ids, serverDate);
	}

	/// <summary>The articles with these IDs. IDs that aren't numbers are skipped.</summary>
	public async Task<IReadOnlyList<ReaderApiEntry>> GetEntriesAsync(IReadOnlyCollection<string> articleIds, CancellationToken cancellationToken = default)
	{
		var itemParameters = articleIds.Select(LongItemId).OfType<string>().Select(id => ("i", id)).ToList();
		if (itemParameters.Count == 0)
		{
			return [];
		}
		return await WithWriteTokenAsync(async token =>
		{
			using var response = await SendAsync(
				() => new HttpRequestMessage(HttpMethod.Post, _endpoint + "/reader/api/0/stream/items/contents")
				{
					Content = Form([("T", token), ("output", "json"), .. itemParameters]),
				},
				cancellationToken);
			return (IReadOnlyList<ReaderApiEntry>?)(await ReadJsonAsync<ReaderApiEntryList>(response, cancellationToken))?.Items ?? [];
		}, cancellationToken);
	}

	/// <summary>Marks articles read or unread, starred or unstarred.</summary>
	public Task SetStateAsync(IReadOnlyCollection<string> articleIds, bool starred, bool add, CancellationToken cancellationToken = default)
	{
		var itemParameters = articleIds.Select(LongItemId).OfType<string>().Select(id => ("i", id)).ToList();
		return itemParameters.Count == 0
			? Task.CompletedTask
			: PostWithTokenAsync("/reader/api/0/edit-tag", [.. itemParameters, (add ? "a" : "r", starred ? StarredState : ReadState)], cancellationToken);
	}

	/// <summary>Subscribes to a feed URL. Returns the new subscription's ID, or null when the server couldn't add it.</summary>
	public async Task<string?> QuickAddAsync(string feedUrl, CancellationToken cancellationToken = default)
	{
		var result = await WithWriteTokenAsync(async token =>
		{
			using var response = await SendAsync(
				() => new HttpRequestMessage(HttpMethod.Post, _endpoint + "/reader/api/0/subscription/quickadd") { Content = Form(("T", token), ("quickadd", feedUrl)) },
				cancellationToken);
			return await ReadJsonAsync<ReaderApiQuickAddResult>(response, cancellationToken);
		}, cancellationToken);
		return result is { NumResults: > 0 } ? result.StreamId : null;
	}

	public Task UnsubscribeAsync(string subscriptionId, CancellationToken cancellationToken = default) =>
		PostWithTokenAsync("/reader/api/0/subscription/edit", [("s", subscriptionId), ("ac", "unsubscribe")], cancellationToken);

	/// <summary>Adds a subscription to a folder or takes it out of one, by folder name.</summary>
	public Task EditSubscriptionFoldersAsync(string subscriptionId, string? addFolder, string? removeFolder, CancellationToken cancellationToken = default)
	{
		var parameters = new List<(string, string)> { ("s", subscriptionId), ("ac", "edit") };
		if (removeFolder is not null)
		{
			parameters.Add(("r", "user/-/label/" + removeFolder));
		}
		if (addFolder is not null)
		{
			parameters.Add(("a", "user/-/label/" + addFolder));
		}
		return PostWithTokenAsync("/reader/api/0/subscription/edit", parameters, cancellationToken);
	}

	public Task RenameFolderAsync(string oldName, string newName, CancellationToken cancellationToken = default) =>
		PostWithTokenAsync("/reader/api/0/rename-tag", [("s", "user/-/label/" + oldName), ("dest", "user/-/label/" + newName)], cancellationToken);

	public Task DeleteFolderAsync(string name, CancellationToken cancellationToken = default) =>
		PostWithTokenAsync("/reader/api/0/disable-tag", [("s", "user/-/label/" + name)], cancellationToken);

	public void Dispose() => _http.Dispose();

	private async Task PostWithTokenAsync(string path, IEnumerable<(string, string)> parameters, CancellationToken cancellationToken) =>
		await WithWriteTokenAsync(async token =>
		{
			using var response = await SendAsync(
				() => new HttpRequestMessage(HttpMethod.Post, _endpoint + path) { Content = Form([("T", token), .. parameters]) },
				cancellationToken);
			return true;
		}, cancellationToken);

	/// <summary>
	/// Runs a change with the write token, fetching a fresh one and retrying once if the server turns it down:
	/// tokens are short-lived, as NetNewsWire's <c>withWriteToken</c> notes.
	/// </summary>
	private async Task<T> WithWriteTokenAsync<T>(Func<string, Task<T>> operation, CancellationToken cancellationToken)
	{
		_writeToken ??= await GetWriteTokenAsync(cancellationToken);
		try
		{
			return await operation(_writeToken);
		}
		catch (ReaderApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
		{
			_writeToken = await GetWriteTokenAsync(cancellationToken);
			return await operation(_writeToken);
		}
	}

	private async Task<string> GetWriteTokenAsync(CancellationToken cancellationToken)
	{
		using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, _endpoint + "/reader/api/0/token"), cancellationToken);
		return (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
	}

	private async Task<T?> GetJsonAsync<T>(string pathAndQuery, CancellationToken cancellationToken)
	{
		using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, _endpoint + pathAndQuery), cancellationToken);
		return await ReadJsonAsync<T>(response, cancellationToken);
	}

	/// <summary>Sends with the login token, logging in first if needed and again once if the server says the token expired.</summary>
	private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> makeRequest, CancellationToken cancellationToken)
	{
		if (_auth is null)
		{
			await LoginAsync(cancellationToken);
		}
		var response = await SendAuthorizedAsync(makeRequest, cancellationToken);
		if (response.StatusCode == HttpStatusCode.Unauthorized)
		{
			response.Dispose();
			await LoginAsync(cancellationToken);
			response = await SendAuthorizedAsync(makeRequest, cancellationToken);
		}
		EnsureSuccess(response);
		return response;
	}

	private async Task<HttpResponseMessage> SendAuthorizedAsync(Func<HttpRequestMessage> makeRequest, CancellationToken cancellationToken)
	{
		using var request = makeRequest();
		request.Headers.Authorization = new AuthenticationHeaderValue("GoogleLogin", "auth=" + _auth);
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
			throw new ReaderApiException($"Couldn’t reach {_endpoint}: {ex.Message}");
		}
		catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new ReaderApiException($"{_endpoint} took too long to answer.");
		}
	}

	private static void EnsureSuccess(HttpResponseMessage response)
	{
		if (!response.IsSuccessStatusCode)
		{
			var status = response.StatusCode;
			response.Dispose();
			throw new ReaderApiException($"The server answered {(int)status} {status}.", status);
		}
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
			throw new ReaderApiException("The server’s answer wasn’t the JSON the Reader API sends.");
		}
	}

	private static FormUrlEncodedContent Form(params IEnumerable<(string Name, string Value)> fields) =>
		new(fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)));

	private static string QueryString(IEnumerable<(string Name, string Value)> fields) =>
		string.Join("&", fields.Select(field => $"{Uri.EscapeDataString(field.Name)}={Uri.EscapeDataString(field.Value)}"));
}
