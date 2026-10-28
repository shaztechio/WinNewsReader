using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinNewsReader.Parser.Feeds;
using WinNewsReader.Parser.Html;

namespace WinNewsReader.Core;

/// <summary>
/// Finds, downloads and caches feeds' icons, as NetNewsWire's <c>FeedIconDownloader</c> and <c>FaviconDownloader</c> do.
/// A feed's own icon comes first, then the home page's best image (its largest Apple touch icon, else an Open Graph or
/// Twitter image), then the home page's icon links in document order, skipping SVG, and last <c>/favicon.ico</c>.
/// Files go in the cache directory with an index of which feed or site uses which file.
/// </summary>
public sealed class FaviconDownloader(FeedDownloader downloader, string cacheDirectory)
{
	/// <summary>How long to wait before looking again at a site that had no usable favicon.</summary>
	public static readonly TimeSpan RetryAfter = TimeSpan.FromDays(1);

	/// <summary>An icon bigger than this isn't one.</summary>
	private const int MaxIconBytes = 2_000_000;

	/// <summary>NetNewsWire's <c>specialCasesToSkip</c>: sites whose feed icons are wrong, so only their favicon is used.</summary>
	private static readonly string[] SitesWithoutFeedIcons = ["macsparky.com", "xkcd.com", "rachelbythebay.com", "openrss.org"];

	/// <summary>NetNewsWire's <c>feedURLSubstringsToIgnoreFeedIconURL</c>: feeds whose declared icon is wrong, so the home page's is used.</summary>
	private static readonly string[] FeedsWhoseDeclaredIconIsIgnored =
		["propublica.org", "clubic.com", "comptoir-hardware.com", "cowcotland", "404media.co", "awfulannouncing.com", "michalzelazny.com"];

	private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inFlight = new();
	private readonly Lock _indexLock = new();
	private Dictionary<string, IndexEntry>? _index;

	private string IndexPath => Path.Combine(cacheDirectory, "index.json");

	/// <summary>
	/// The cached favicon file for the site at <paramref name="homePageUrl"/>, downloaded first if needed.
	/// Null when the site has no favicon this can use.
	/// </summary>
	public Task<string?> FaviconPathAsync(string homePageUrl, CancellationToken cancellationToken = default) =>
		IconPathAsync(homePageUrl, declaredIconUrl: null, feedUrl: null, cancellationToken);

	/// <summary>
	/// The cached icon file for a feed: its <paramref name="declaredIconUrl"/> if it has one, else the best image on its
	/// home page, else the site's favicon. Null when none can be used.
	/// </summary>
	public Task<string?> IconPathAsync(string homePageUrl, string? declaredIconUrl, string? feedUrl, CancellationToken cancellationToken = default)
	{
		if (SiteRoot(homePageUrl) is null)
		{
			return Task.FromResult<string?>(null);
		}
		// Feeds that declare their own icon keep it separate from the site's, which other feeds there may share.
		var key = IndexKey(homePageUrl, UsableDeclaredIcon(declaredIconUrl, feedUrl, homePageUrl));
		if (Lookup(key) is { } entry)
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
		return _inFlight.GetOrAdd(key, _ => new Lazy<Task<string?>>(() => FindAsync(key, homePageUrl, declaredIconUrl, feedUrl, cancellationToken))).Value;
	}

	/// <summary>
	/// The index key: the declared icon's address, or the home page's, with a prefix that keeps these icons apart from the
	/// smaller favicons that older versions cached under the bare home page address.
	/// </summary>
	private static string IndexKey(string homePageUrl, string? declaredIconUrl) => "icon:" + (declaredIconUrl ?? homePageUrl);

	/// <summary>The feed's declared icon, cleaned up, unless this feed or site is one NetNewsWire knows to have a wrong one.</summary>
	private static string? UsableDeclaredIcon(string? declaredIconUrl, string? feedUrl, string homePageUrl) =>
		declaredIconUrl is { Length: > 0 } declared && !SkipsFeedIcons(homePageUrl) && !SkipsFeedIcons(feedUrl)
			&& (feedUrl is null || !FeedsWhoseDeclaredIconIsIgnored.Any(part => feedUrl.Contains(part, StringComparison.OrdinalIgnoreCase)))
			? SanitizedIconUrl(declared)
			: null;

	private static bool SkipsFeedIcons(string? url) =>
		url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri)
		&& SitesWithoutFeedIcons.Any(site => uri.Host.Equals(site, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + site, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// NetNewsWire's <c>sanitizedIconURL</c>: WordPress uploads often carry a query that asks for a 32 px version; without
	/// it the original comes back.
	/// </summary>
	public static string SanitizedIconUrl(string url)
	{
		if (!url.Contains("/wp-content/uploads/", StringComparison.Ordinal) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
		{
			return url;
		}
		return new UriBuilder(uri) { Query = "", Fragment = "" }.Uri.AbsoluteUri;
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

	private async Task<string?> FindAsync(string key, string homePageUrl, string? declaredIconUrl, string? feedUrl, CancellationToken cancellationToken)
	{
		try
		{
			var declared = UsableDeclaredIcon(declaredIconUrl, feedUrl, homePageUrl);
			foreach (var candidate in await CandidateUrlsAsync(homePageUrl, declared, cancellationToken))
			{
				var download = await downloader.DownloadAsync(candidate, MaxIconBytes, cancellationToken);
				if (download is { Status: DownloadStatus.Ok, Data: { } data } && ImageExtension(data) is { } extension)
				{
					Directory.CreateDirectory(cacheDirectory);
					var file = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(candidate))) + extension;
					await File.WriteAllBytesAsync(Path.Combine(cacheDirectory, file), data, cancellationToken);
					Record(key, new IndexEntry(file, DateTimeOffset.UtcNow));
					return Path.Combine(cacheDirectory, file);
				}
			}
			Record(key, new IndexEntry(null, DateTimeOffset.UtcNow));
			return null;
		}
		catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
		{
			return null;
		}
		finally
		{
			_inFlight.TryRemove(key, out _);
		}
	}

	/// <summary>
	/// The feed's declared icon, the home page's best image, its icon links, then <c>/favicon.ico</c>. NetNewsWire tries
	/// <c>/favicon.ico</c> only when the page lists no icons; trying it last as well also covers pages whose listed icons
	/// are broken. A site that has no feed icons skips the best image.
	/// </summary>
	private async Task<List<string>> CandidateUrlsAsync(string homePageUrl, string? declaredIconUrl, CancellationToken cancellationToken)
	{
		var candidates = new List<string>();
		if (declaredIconUrl is not null)
		{
			candidates.Add(declaredIconUrl);
		}
		var page = await downloader.DownloadAsync(homePageUrl, null, null, cancellationToken);
		if (page is { Status: DownloadStatus.Ok, Data: { } html })
		{
			var metadata = HtmlMetadataParser.HtmlMetadata(new ParserData(page.FinalUrl?.AbsoluteUri ?? homePageUrl, html));
			if (!SkipsFeedIcons(homePageUrl) && metadata.BestWebsiteIconUrl() is { } best && !candidates.Contains(best))
			{
				candidates.Add(best);
			}
			candidates.AddRange(metadata.Favicons.Where(favicon => favicon.Url is not null && !IsSvg(favicon)).Select(favicon => favicon.Url!).Where(url => !candidates.Contains(url)));
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
