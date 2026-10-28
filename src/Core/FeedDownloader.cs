using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace WinNewsReader.Core;

public enum DownloadStatus
{
	Ok,
	NotModified,
	Failed,
}

public sealed record DownloadResult(DownloadStatus Status, byte[]? Data, string? ETag, string? LastModified, Uri? FinalUrl, string? Error)
{
	/// <summary>The final response's HTTP status, or 0 if there was no response.</summary>
	public int StatusCode { get; init; }
}

/// <summary>
/// HTTP fetches for feeds: conditional GET with ETag and Last-Modified, and at most
/// <see cref="MaxConcurrentRequestsPerHost"/> requests in flight per host.
/// Redirects are followed here rather than by HttpClient, which refuses https-to-http hops. URLSession
/// follows them, and plenty of feeds bounce http → https → http, so NetNewsWire reads those feeds.
/// </summary>
public sealed class FeedDownloader : IDisposable
{
	public const int MaxConcurrentRequestsPerHost = 2;

	public const int MaxRedirects = 10;

	public const string UserAgent = "WinNewsReader/0.1 (RSS Reader; https://github.com/shaztechio/WinNewsReader)";

	private readonly HttpClient _httpClient;
	private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostGates = new(StringComparer.OrdinalIgnoreCase);

	public FeedDownloader(HttpMessageHandler? handler = null)
	{
		_httpClient = handler is null
			? new HttpClient(new SocketsHttpHandler
			{
				AllowAutoRedirect = false,
				AutomaticDecompression = DecompressionMethods.All,
				PooledConnectionLifetime = TimeSpan.FromMinutes(5),
			})
			: new HttpClient(handler);
		_httpClient.Timeout = TimeSpan.FromSeconds(60);
		_httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
	}

	public async Task<DownloadResult> DownloadAsync(string url, string? etag, string? lastModified, CancellationToken cancellationToken = default)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
		{
			return new DownloadResult(DownloadStatus.Failed, null, null, null, null, $"Not an http(s) URL: {url}");
		}
		var gate = _hostGates.GetOrAdd(uri.Host, _ => new SemaphoreSlim(MaxConcurrentRequestsPerHost));
		await gate.WaitAsync(cancellationToken);
		try
		{
			var requestUri = uri;
			var response = await SendAsync(requestUri, etag, lastModified, cancellationToken);
			for (var hop = 0; IsRedirect(response.StatusCode) && response.Headers.Location is { } location; hop++)
			{
				response.Dispose();
				if (hop == MaxRedirects)
				{
					return new DownloadResult(DownloadStatus.Failed, null, null, null, requestUri, "Too many redirects");
				}
				requestUri = new Uri(requestUri, location);
				if (requestUri.Scheme is not ("http" or "https"))
				{
					return new DownloadResult(DownloadStatus.Failed, null, null, null, requestUri, $"Redirected to a non-http(s) URL: {requestUri}");
				}
				response = await SendAsync(requestUri, etag, lastModified, cancellationToken);
			}
			using var _ = response;
			if (response.StatusCode == HttpStatusCode.NotModified)
			{
				return new DownloadResult(DownloadStatus.NotModified, null, etag, lastModified, requestUri, null) { StatusCode = 304 };
			}
			if (!response.IsSuccessStatusCode)
			{
				return new DownloadResult(DownloadStatus.Failed, null, null, null, requestUri, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}")
				{
					StatusCode = (int)response.StatusCode,
				};
			}
			var data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
			response.Content.Headers.TryGetValues("Last-Modified", out var lastModifiedValues);
			return new DownloadResult(DownloadStatus.Ok, data, response.Headers.ETag?.ToString(), lastModifiedValues?.FirstOrDefault(), requestUri, null)
			{
				StatusCode = (int)response.StatusCode,
			};
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
		{
			return new DownloadResult(DownloadStatus.Failed, null, null, null, uri, ex.Message);
		}
		finally
		{
			gate.Release();
		}
	}

	public void Dispose() => _httpClient.Dispose();

	private Task<HttpResponseMessage> SendAsync(Uri uri, string? etag, string? lastModified, CancellationToken cancellationToken)
	{
		var request = new HttpRequestMessage(HttpMethod.Get, uri);
		if (!string.IsNullOrEmpty(etag) && EntityTagHeaderValue.TryParse(etag, out var entityTag))
		{
			request.Headers.IfNoneMatch.Add(entityTag);
		}
		if (!string.IsNullOrEmpty(lastModified))
		{
			request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
		}
		return _httpClient.SendAsync(request, cancellationToken);
	}

	private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
		or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
