using WinNewsReader.Parser.Feeds;
using WinNewsReader.Parser.Html;
using WinNewsReader.Parser.Utilities;

namespace WinNewsReader.Core;

public sealed class FeedNotFoundException() : Exception("The feed couldn’t be found and can’t be added.");

/// <summary>
/// Finds the feeds for a URL the user typed: the URL itself if it's a feed, else feeds advertised in the
/// page's head, else links in the page that turn out to be feeds when fetched. Ported from NetNewsWire's
/// FeedFinder.
/// </summary>
public sealed class FeedFinder(FeedDownloader downloader)
{
	/// <summary>Throws <see cref="FeedNotFoundException"/> if the URL can't be fetched or isn't a feed or HTML.</summary>
	public async Task<IReadOnlySet<FeedSpecifier>> FindAsync(string url, CancellationToken cancellationToken = default)
	{
		if (FeedSpecifier.KnownFeedSpecifier(url) is { } known)
		{
			return new HashSet<FeedSpecifier> { known };
		}

		var download = await downloader.DownloadAsync(url, null, null, cancellationToken);
		if (download.StatusCode == 404)
		{
			// Micro.blog user pages 404 without a trailing .json, which is the feed.
			if (FoundationUrl.Parse(url) is { Host: "micro.blog" } parts)
			{
				var microblogUrl = (parts with { Path = parts.Path + ".json" }).ToString();
				return new HashSet<FeedSpecifier> { new(null, microblogUrl, FeedSpecifierSource.HtmlLink, 1) };
			}
			throw new FeedNotFoundException();
		}
		if (download is not { Status: DownloadStatus.Ok, Data: { Length: > 0 } data })
		{
			throw new FeedNotFoundException();
		}

		// After redirects, relative links resolve against where the page actually came from.
		var urlToUse = download.FinalUrl?.OriginalString ?? url;
		if (IsFeed(data, urlToUse))
		{
			return new HashSet<FeedSpecifier> { new(null, urlToUse, FeedSpecifierSource.UserEntered, 1) };
		}
		if (!IsProbablyHtml(data))
		{
			throw new FeedNotFoundException();
		}
		return await FindFeedsInHtmlPageAsync(data, urlToUse, cancellationToken);
	}

	/// <summary>
	/// Feeds in the page's head are taken as feeds. Without any, candidate links from the body are
	/// downloaded and kept if they're feeds.
	/// </summary>
	private async Task<IReadOnlySet<FeedSpecifier>> FindFeedsInHtmlPageAsync(byte[] html, string url, CancellationToken cancellationToken)
	{
		var feedSpecifiers = new Dictionary<string, FeedSpecifier>();
		var toDownload = new HashSet<FeedSpecifier>();
		foreach (var specifier in PossibleFeedsInHtmlPage(html, url))
		{
			if (specifier.Source == FeedSpecifierSource.HtmlHead)
			{
				FeedSpecifier.AddMerging(feedSpecifiers, specifier);
			}
			else if (!feedSpecifiers.ContainsKey(specifier.Url))
			{
				toDownload.Add(specifier);
			}
		}

		if (feedSpecifiers.Count > 0)
		{
			return feedSpecifiers.Values.ToHashSet();
		}
		if (toDownload.Count == 0)
		{
			throw new FeedNotFoundException();
		}

		var found = await Task.WhenAll(toDownload.Select(async candidate =>
		{
			var download = await downloader.DownloadAsync(candidate.Url, null, null, cancellationToken);
			return download is { Status: DownloadStatus.Ok, Data: { } data } && IsFeed(data, candidate.Url) ? candidate : null;
		}));
		foreach (var specifier in found.OfType<FeedSpecifier>())
		{
			FeedSpecifier.AddMerging(feedSpecifiers, specifier);
		}
		return feedSpecifiers.Values.ToHashSet();
	}

	private static HashSet<FeedSpecifier> PossibleFeedsInHtmlPage(byte[] html, string url)
	{
		var feedSpecifiers = HtmlFeedFinder.FeedSpecifiers(new ParserData(url, html));
		// Odds are decent it's a WordPress site, where /feed/ works. /index.xml is also common.
		if (feedSpecifiers.Count == 0 && FoundationUrl.Parse(url) is { } parts)
		{
			feedSpecifiers.Add(new FeedSpecifier(null, AppendingPathComponent(parts, "feed/"), FeedSpecifierSource.HtmlLink, 1));
			feedSpecifiers.Add(new FeedSpecifier(null, AppendingPathComponent(parts, "index.xml"), FeedSpecifierSource.HtmlLink, 1));
		}
		return feedSpecifiers;
	}

	/// <summary><c>URL.appendingPathComponent</c>: adds a slash between the path and the component if needed.</summary>
	private static string AppendingPathComponent(FoundationUrl.Parts parts, string component) =>
		(parts with { Path = (parts.Path.EndsWith('/') ? parts.Path : parts.Path + "/") + component }).ToString();

	private static bool IsFeed(byte[] data, string url) => FeedParser.CanParse(new ParserData(url, data));

	private static readonly string[] HtmlIndicators = ["<!doctype", "<!DOCTYPE", "html", "HTML", "head", "HEAD", "body", "BODY", "div", "DIV", "p", "P", "span", "SPAN"];

	/// <summary>
	/// NetNewsWire's <c>Data.isProbablyHTML</c>: has <c>&lt;</c> and <c>&gt;</c> and any of a few tag names,
	/// in UTF-8 or UTF-16LE.
	/// </summary>
	internal static bool IsProbablyHtml(byte[] data)
	{
		var span = data.AsSpan();
		if (!span.Contains((byte)'<') || !span.Contains((byte)'>'))
		{
			return false;
		}
		foreach (var indicator in HtmlIndicators)
		{
			if (span.IndexOf(System.Text.Encoding.UTF8.GetBytes(indicator)) >= 0
				|| span.IndexOf(System.Text.Encoding.Unicode.GetBytes(indicator)) >= 0)
			{
				return true;
			}
		}
		return false;
	}
}

public enum FeedSpecifierSource
{
	UserEntered = 0,
	HtmlHead,
	HtmlLink,
}

/// <summary>A possible feed, with where it was found. Lower <see cref="FeedSpecifierSource"/> values are better.</summary>
public sealed record FeedSpecifier(string? Title, string Url, FeedSpecifierSource Source, int OrderFound)
{
	public int Score => CalculatedScore();

	/// <summary>Feeds known in advance, saving a fetch.</summary>
	public static FeedSpecifier? KnownFeedSpecifier(string url)
	{
		var parts = FoundationUrl.Parse(url);
		if (parts?.Host is { } host && host.Contains("rachelbythebay.com", StringComparison.OrdinalIgnoreCase))
		{
			return new FeedSpecifier("writing - rachelbythebay", "https://rachelbythebay.com/w/atom.xml", FeedSpecifierSource.UserEntered, 0);
		}
		// The blog page only advertises the master podcast feed in its head.
		// <https://github.com/Ranchero-Software/NetNewsWire/issues/5299>
		if (UrlMatchesDomain(url, ["relay.fm"]) && parts?.Path is { } path && (path == "/blog" || path.StartsWith("/blog/", StringComparison.Ordinal)))
		{
			return new FeedSpecifier("Relay FM Blog", "https://www.relay.fm/blog/feed", FeedSpecifierSource.UserEntered, 0);
		}
		return null;
	}

	public static FeedSpecifier? BestFeed(IReadOnlyCollection<FeedSpecifier> feedSpecifiers) =>
		feedSpecifiers.Count switch
		{
			0 => null,
			1 => feedSpecifiers.First(),
			_ => feedSpecifiers.MaxBy(specifier => specifier.Score),
		};

	/// <summary>The best of both: a non-null title, the better source and the earlier order.</summary>
	public FeedSpecifier MergedWith(FeedSpecifier other) => new(
		Title ?? other.Title,
		Url,
		Source <= other.Source ? Source : other.Source,
		Math.Min(OrderFound, other.OrderFound));

	internal static void AddMerging(Dictionary<string, FeedSpecifier> feedSpecifiers, FeedSpecifier specifier) =>
		feedSpecifiers[specifier.Url] = feedSpecifiers.TryGetValue(specifier.Url, out var existing) ? existing.MergedWith(specifier) : specifier;

	/// <summary>
	/// NetNewsWire's <c>SpecialCase.urlStringMatchesDomain</c>: the host, minus any leading www., is one of
	/// <paramref name="domains"/> or a subdomain of one.
	/// </summary>
	internal static bool UrlMatchesDomain(string url, string[] domains)
	{
		if (FoundationUrl.Parse(url)?.Host?.ToLowerInvariant() is not { } host)
		{
			return false;
		}
		if (host.StartsWith("www.", StringComparison.Ordinal))
		{
			host = host[4..];
		}
		return domains.Any(domain => domain.Length > 0 && (host == domain || host.EndsWith("." + domain, StringComparison.Ordinal)));
	}

	private int CalculatedScore()
	{
		if (Source == FeedSpecifierSource.UserEntered)
		{
			return 1000;
		}
		var score = Source == FeedSpecifierSource.HtmlHead ? 50 : 0;
		score -= (OrderFound - 1) * 5;

		if (Url.Contains("comments", StringComparison.OrdinalIgnoreCase))
		{
			score -= 10;
		}
		if (Url.Contains("podcast", StringComparison.OrdinalIgnoreCase))
		{
			score -= 10;
		}
		if (Url.Contains("rss", StringComparison.OrdinalIgnoreCase))
		{
			score += 5;
		}
		if (Url.EndsWith("/index.xml", StringComparison.Ordinal))
		{
			score += 5;
		}
		if (Url.EndsWith("/feed/", StringComparison.Ordinal))
		{
			score += 5;
		}
		if (Url.EndsWith("/feed", StringComparison.Ordinal))
		{
			score += 4;
		}
		if (Url.Contains("json", StringComparison.OrdinalIgnoreCase))
		{
			score += 3;
		}
		if (Title is not null && Title.Contains("comments", StringComparison.OrdinalIgnoreCase))
		{
			score -= 10;
		}
		return score;
	}
}

/// <summary>Feed links from a page's head, plus body links whose URLs look like feeds.</summary>
internal static class HtmlFeedFinder
{
	private static readonly string[] FeedUrlWordsToMatch = ["feed", "xml", "rss", "atom", "json"];

	private static readonly string[] NonFeedDomains = ["x.com", "twitter.com", "facebook.com", "instagram.com"];

	private static readonly HashSet<string> NonFeedExtensions =
	[
		"html", "pdf", "jpg", "jpeg", "png", "gif", "tiff", "heic", "svg", "webp", "bmp", "ico", "zip", "tar", "tgz", "gz",
		"dmg", "mp3", "mp4", "mov", "mpeg", "mpg", "m4a", "aac", "wav", "aiff", "doc", "docx", "xls", "xlsx", "ppt", "pptx",
	];

	public static HashSet<FeedSpecifier> FeedSpecifiers(ParserData parserData)
	{
		var feedSpecifiers = new Dictionary<string, FeedSpecifier>();
		var orderFound = 0;

		foreach (var feedLink in HtmlMetadataParser.HtmlMetadata(parserData).FeedLinks)
		{
			if (feedLink.Url is { } url)
			{
				orderFound++;
				FeedSpecifier.AddMerging(feedSpecifiers, new FeedSpecifier(feedLink.Title, url.NormalizedUrl(), FeedSpecifierSource.HtmlHead, orderFound));
			}
		}
		foreach (var bodyLink in HtmlLinkParser.HtmlLinks(parserData))
		{
			if (bodyLink.Url is { } url && UrlMightBeFeed(url))
			{
				orderFound++;
				FeedSpecifier.AddMerging(feedSpecifiers, new FeedSpecifier(bodyLink.Text, url.NormalizedUrl(), FeedSpecifierSource.HtmlLink, orderFound));
			}
		}
		return feedSpecifiers.Values.ToHashSet();
	}

	private static bool UrlMightBeFeed(string url)
	{
		if (UrlIsDefinitelyNotFeed(url))
		{
			return false;
		}
		var massaged = url.Replace("buzzfeed", "_", StringComparison.Ordinal);
		return FeedUrlWordsToMatch.Any(word => massaged.Contains(word, StringComparison.OrdinalIgnoreCase));
	}

	private static bool UrlIsDefinitelyNotFeed(string url)
	{
		if (FeedSpecifier.UrlMatchesDomain(url, NonFeedDomains))
		{
			return true;
		}
		// The path's extension, so a query or fragment doesn't hide it.
		var path = FoundationUrl.Parse(url)?.Path ?? url;
		return NonFeedExtensions.Contains(PathExtension(path).ToLowerInvariant());
	}

	/// <summary>The last path component's extension, as Foundation's <c>pathExtension</c> returns it.</summary>
	private static string PathExtension(string path)
	{
		var lastComponent = path.TrimEnd('/');
		lastComponent = lastComponent[(lastComponent.LastIndexOf('/') + 1)..];
		var dot = lastComponent.LastIndexOf('.');
		return dot > 0 ? lastComponent[(dot + 1)..] : "";
	}
}

public static class UrlStringExtensions
{
	/// <summary>
	/// NetNewsWire's <c>String.normalizedURL</c>: trims whitespace, strips <c>feed:</c> or <c>feeds:</c>
	/// (so <c>feed:http://boingboing.net/feed</c> works), adds <c>http://</c> (<c>https://</c> after
	/// <c>feeds:</c>) if there's no http(s) prefix, and gives a bare host a trailing slash.
	/// </summary>
	public static string NormalizedUrl(this string url)
	{
		var s = url.Trim();
		var wasFeeds = false;
		if (s.StartsWith("feeds:", StringComparison.OrdinalIgnoreCase))
		{
			wasFeeds = true;
			s = s["feeds:".Length..];
		}
		else if (s.StartsWith("feed:", StringComparison.OrdinalIgnoreCase))
		{
			s = s["feed:".Length..];
		}
		if (s.StartsWith("//", StringComparison.Ordinal))
		{
			s = s[2..];
		}
		// No colon after http on purpose, so https passes too.
		if (!s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
		{
			s = $"{(wasFeeds ? "https" : "http")}://{s}";
		}
		if (s.Split('/').Length == 3)
		{
			s += "/";
		}
		return s;
	}
}
