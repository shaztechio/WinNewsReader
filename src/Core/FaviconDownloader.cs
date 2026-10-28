using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinNewsReader.Parser.Feeds;
using WinNewsReader.Parser.Html;

namespace WinNewsReader.Core;

/// <summary>
/// Finds, downloads and caches sites' favicons, as NetNewsWire's <c>FaviconDownloader</c> and <c>FaviconURLFinder</c> do:
/// the home page's icon links in document order, skipping SVG, then <c>/favicon.ico</c> at the site root.
/// Files go in the cache directory with an index of which site uses which file.
/// </summary>
public sealed class FaviconDownloader(FeedDownloader downloader, string cacheDirectory)
{
	/// <summary>How long to wait before looking again at a site that had no usable favicon.</summary>
	public static readonly TimeSpan RetryAfter = TimeSpan.FromDays(1);

	private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inFlight = new();
	private readonly Lock _indexLock = new();
	private Dictionary<string, IndexEntry>? _index;

	private string IndexPath => Path.Combine(cacheDirectory, "index.json");

	/// <summary>
	/// The cached favicon file for the site at <paramref name="homePageUrl"/>, downloaded first if needed.
	/// Null when the site has no favicon this can use.
	/// </summary>
	public Task<string?> FaviconPathAsync(string homePageUrl, CancellationToken cancellationToken = default)
	{
		if (SiteRoot(homePageUrl) is null)
		{
			return Task.FromResult<string?>(null);
		}
		if (Lookup(homePageUrl) is { } entry)
		{
			if (entry.File is not null && File.Exists(Path.Combine(cacheDirectory, entry.File)))
			{
				return Task.FromResult<string?>(Path.Combine(cacheDirectory, entry.File));
			}
			if (entry.File is null && DateTimeOffset.UtcNow - entry.Checked < RetryAfter)
			{
				return Task.FromResult<string?>(null);
			}
		}
		// Feeds from one site share a lookup.
		return _inFlight.GetOrAdd(homePageUrl, url => new Lazy<Task<string?>>(() => FindAsync(url, cancellationToken))).Value;
	}

	/// <summary><c>scheme://host/</c> for an http(s) URL; null otherwise.</summary>
	public static string? SiteRoot(string url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
			? uri.GetLeftPart(UriPartial.Authority) + "/"
			: null;

	/// <summary>The file extension for image data a favicon can be, from its first bytes; null for anything else, such as an HTML error page.</summary>
	public static string? ImageExtension(ReadOnlySpan<byte> data) => data switch
	{
		[0x89, (byte)'P', (byte)'N', (byte)'G', ..] => ".png",
		[0, 0, 1, 0, ..] => ".ico",
		[(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] => ".gif",
		[0xFF, 0xD8, 0xFF, ..] => ".jpg",
		[(byte)'B', (byte)'M', ..] => ".bmp",
		[(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => ".webp",
		_ => null,
	};

	private async Task<string?> FindAsync(string homePageUrl, CancellationToken cancellationToken)
	{
		try
		{
			foreach (var candidate in await CandidateUrlsAsync(homePageUrl, cancellationToken))
			{
				var download = await downloader.DownloadAsync(candidate, null, null, cancellationToken);
				if (download is { Status: DownloadStatus.Ok, Data: { } data } && ImageExtension(data) is { } extension)
				{
					Directory.CreateDirectory(cacheDirectory);
					var file = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(candidate))) + extension;
					await File.WriteAllBytesAsync(Path.Combine(cacheDirectory, file), data, cancellationToken);
					Record(homePageUrl, new IndexEntry(file, DateTimeOffset.UtcNow));
					return Path.Combine(cacheDirectory, file);
				}
			}
			Record(homePageUrl, new IndexEntry(null, DateTimeOffset.UtcNow));
			return null;
		}
		catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
		{
			return null;
		}
		finally
		{
			_inFlight.TryRemove(homePageUrl, out _);
		}
	}

	/// <summary>
	/// The home page's icon links, then <c>/favicon.ico</c>. NetNewsWire tries <c>/favicon.ico</c> only when the page
	/// lists no icons; trying it last as well also covers pages whose listed icons are broken.
	/// </summary>
	private async Task<List<string>> CandidateUrlsAsync(string homePageUrl, CancellationToken cancellationToken)
	{
		var candidates = new List<string>();
		var page = await downloader.DownloadAsync(homePageUrl, null, null, cancellationToken);
		if (page is { Status: DownloadStatus.Ok, Data: { } html })
		{
			var metadata = HtmlMetadataParser.HtmlMetadata(new ParserData(page.FinalUrl?.AbsoluteUri ?? homePageUrl, html));
			candidates.AddRange(metadata.Favicons.Where(favicon => favicon.Url is not null && !IsSvg(favicon)).Select(favicon => favicon.Url!));
		}
		var fallback = SiteRoot(page.FinalUrl?.AbsoluteUri ?? homePageUrl) + "favicon.ico";
		if (!candidates.Contains(fallback))
		{
			candidates.Add(fallback);
		}
		return candidates;
	}

	private static bool IsSvg(HtmlMetadataFavicon favicon) =>
		favicon.Type?.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase) == true
		|| (Uri.TryCreate(favicon.Url, UriKind.Absolute, out var uri) && uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase));

	private IndexEntry? Lookup(string homePageUrl)
	{
		lock (_indexLock)
		{
			return Index().GetValueOrDefault(homePageUrl);
		}
	}

	private void Record(string homePageUrl, IndexEntry entry)
	{
		lock (_indexLock)
		{
			var index = Index();
			index[homePageUrl] = entry;
			Directory.CreateDirectory(cacheDirectory);
			File.WriteAllText(IndexPath, JsonSerializer.Serialize(index));
		}
	}

	private Dictionary<string, IndexEntry> Index()
	{
		if (_index is null)
		{
			try
			{
				_index = File.Exists(IndexPath) ? JsonSerializer.Deserialize<Dictionary<string, IndexEntry>>(File.ReadAllText(IndexPath)) : null;
			}
			catch (JsonException)
			{
				// A damaged index only costs a fresh lookup.
			}
			_index ??= [];
		}
		return _index;
	}

	private sealed record IndexEntry(string? File, DateTimeOffset Checked);
}
