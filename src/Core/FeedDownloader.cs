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

	/// <summary>Failed because the body was over the limit passed to the download.</summary>
	public bool IsTooLarge { get; init; }
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

	public Task<DownloadResult> DownloadAsync(string url, string? etag, string? lastModified, CancellationToken cancellationToken = default) =>
		DownloadAsync(url, etag, lastModified, maxBytes: null, cancellationToken);

	/// <summary>A download that stops reading, and fails, once the body (after decompression) passes <paramref name="maxBytes"/>.</summary>
	public Task<DownloadResult> DownloadAsync(string url, long maxBytes, CancellationToken cancellationToken = default) =>
		DownloadAsync(url, null, null, maxBytes, cancellationToken);

	private async Task<DownloadResult> DownloadAsync(string url, string? etag, string? lastModified, long? maxBytes, CancellationToken cancellationToken)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
		{
			return new DownloadResult(DownloadStatus.Failed, null, null, null, null, $"Not an http(s) URL: {url}");
		}
		var gate = _hostGates.GetOrAdd(uri.Host, _ => new SemaphoreSlim(MaxConcurrentRequestsPerHost));
		await gate.WaitAsync(cancellationToken);
		// HttpClient's timeout stops at the headers now; this one covers reading the body too, so a server that sends
		// it a byte at a time can't hold a refresh forever.
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(_httpClient.Timeout);
		try
		{
			var requestUri = uri;
			var response = await SendAsync(requestUri, etag, lastModified, timeout.Token);
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
				response = await SendAsync(requestUri, etag, lastModified, timeout.Token);
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
			var data = maxBytes is { } limit ? await ReadAtMostAsync(response.Content, limit, timeout.Token) : await response.Content.ReadAsByteArrayAsync(timeout.Token);
			if (data is null)
			{
				return new DownloadResult(DownloadStatus.Failed, null, null, null, requestUri, $"Larger than {maxBytes:N0} bytes")
				{
					StatusCode = (int)response.StatusCode,
					IsTooLarge = true,
				};
			}
			response.Content.Headers.TryGetValues("Last-Modified", out var lastModifiedValues);
			return new DownloadResult(DownloadStatus.Ok, data, response.Headers.ETag?.ToString(), lastModifiedValues?.FirstOrDefault(), requestUri, null)
			{
				StatusCode = (int)response.StatusCode,
			};
		}
		catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException && !cancellationToken.IsCancellationRequested)
		{
			return new DownloadResult(DownloadStatus.Failed, null, null, null, uri, ex is OperationCanceledException ? "Timed out" : ex.Message);
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
		// Headers first, so a body can be read with a limit rather than buffered whole.
		return _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
	}

	/// <summary>The body, or null once it passes <paramref name="maxBytes"/>, whatever Content-Length claimed.</summary>
	private static async Task<byte[]?> ReadAtMostAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
	{
		if (content.Headers.ContentLength > maxBytes)
		{
			return null;
		}
		await using var stream = await content.ReadAsStreamAsync(cancellationToken);
		using var buffer = new MemoryStream();
		var chunk = new byte[81920];
		int read;
		while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
		{
			if (buffer.Length + read > maxBytes)
			{
				return null;
			}
			buffer.Write(chunk, 0, read);
		}
		return buffer.ToArray();
	}

	private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
		or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
