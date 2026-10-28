using System.Globalization;
using WinNewsReader.Parser.Feeds;
using WinNewsReader.Parser.Utilities;
using WinNewsReader.Parser.Xml;

namespace WinNewsReader.Parser.Html;

public enum HtmlTagType
{
	Link,
	Meta,
}

/// <summary>
/// A <c>&lt;link&gt;</c> or <c>&lt;meta&gt;</c> tag. Attribute names keep the page's case, so look them up
/// case-insensitively.
/// </summary>
public sealed record HtmlTag(HtmlTagType Type, IReadOnlyDictionary<string, string> Attributes);

public sealed record HtmlMetadataFeedLink(string? Title, string? Type, string? Url);

public sealed record HtmlMetadataFavicon(string? Type, string? Url);

public sealed record HtmlMetadataAppleTouchIcon(string? Rel, string? Sizes, double Width, double Height, string? Url);

public sealed record HtmlOpenGraphImage(string? Url, string? SecureUrl, string? MimeType, double Width, double Height, string? AltText);

/// <summary>
/// Collects <c>&lt;link&gt;</c> and <c>&lt;meta&gt;</c> tags from a page's head. Stops at <c>&lt;body&gt;</c>,
/// except on YouTube, which puts its feed links in the body. (Any URL containing "youtube" counts;
/// false positives are harmless.)
/// </summary>
public static class HtmlMetadataParser
{
	public static HtmlMetadata HtmlMetadata(ParserData parserData)
	{
		var scanPastHead = parserData.Url.Contains("youtube", StringComparison.OrdinalIgnoreCase);
		var metadataDelegate = new MetadataParserDelegate(scanPastHead);
		new HtmlScanner(metadataDelegate).Parse(parserData.Data);
		return new HtmlMetadata(parserData.Url, metadataDelegate.Tags);
	}

	private sealed class MetadataParserDelegate(bool scanPastHead) : IHtmlScannerDelegate
	{
		private bool _finished;

		public List<HtmlTag> Tags { get; } = [];

		public void DidStartTag(ReadOnlySpan<byte> name, HtmlAttributes attributes, bool selfClosing)
		{
			if (_finished)
			{
				return;
			}
			if (!scanPastHead && AsciiBytes.EqualsIgnoreCase(name, "body"u8))
			{
				_finished = true;
				return;
			}
			if (AsciiBytes.EqualsIgnoreCase(name, "link"u8))
			{
				// Only links with both `rel` and `href` (or `src`).
				if (attributes["rel"] is { Length: > 0 } && (attributes["href"] ?? attributes["src"]) is { Length: > 0 })
				{
					Tags.Add(new HtmlTag(HtmlTagType.Link, attributes.ToDictionary()));
				}
				return;
			}
			if (AsciiBytes.EqualsIgnoreCase(name, "meta"u8) && !attributes.IsEmpty)
			{
				Tags.Add(new HtmlTag(HtmlTagType.Meta, attributes.ToDictionary()));
			}
		}
	}
}

/// <summary>What <see cref="HtmlMetadataParser"/> found in a page, sorted into favicons, feed links and so on.</summary>
public sealed class HtmlMetadata
{
	private static readonly string[] FeedTypeSuffixes = ["/rss+xml", "/atom+xml", "/json"];

	public HtmlMetadata(string url, IReadOnlyList<HtmlTag> tags)
	{
		BaseUrl = url;
		Tags = tags;
		Favicons = ResolveFavicons(tags, url);
		AppleTouchIcons = ResolveAppleTouchIcons(tags, url);
		FeedLinks = ResolveFeedLinks(tags, url);
		OpenGraphImages = ResolveOpenGraphImages(tags);
		TwitterImageUrl = FindTwitterImageUrl(tags);
	}

	public string BaseUrl { get; }

	public IReadOnlyList<HtmlTag> Tags { get; }

	public IReadOnlyList<HtmlMetadataFavicon> Favicons { get; }

	public IReadOnlyList<HtmlMetadataAppleTouchIcon> AppleTouchIcons { get; }

	public IReadOnlyList<HtmlMetadataFeedLink> FeedLinks { get; }

	public IReadOnlyList<HtmlOpenGraphImage> OpenGraphImages { get; }

	/// <summary>The first <c>twitter:image:src</c> meta tag's content.</summary>
	public string? TwitterImageUrl { get; }

	/// <summary>Addresses that are placeholders, not a site's own image.</summary>
	private static readonly HashSet<string> BadIconUrls = ["https://s0.wp.com/i/blank.jpg"];

	/// <summary>
	/// NetNewsWire's <c>bestWebsiteIconURL</c>: the largest Apple touch icon, else the largest Open Graph image, else the
	/// Twitter card image. Images more than twice as wide as tall are banners, not icons, and are skipped.
	/// </summary>
	public string? BestWebsiteIconUrl()
	{
		if (LargestAppleTouchIconUrl() is { } appleTouchIcon)
		{
			return appleTouchIcon;
		}
		if (LargestOpenGraphImageUrl() is { } openGraphImage)
		{
			return openGraphImage;
		}
		return TwitterImageUrl;
	}

	public string? LargestAppleTouchIconUrl() =>
		Largest(AppleTouchIcons.Select(icon => (icon.Width, icon.Height, Url: icon.Url)));

	public string? LargestOpenGraphImageUrl() =>
		Largest(OpenGraphImages.Select(image => (image.Width, image.Height, Url: image.SecureUrl ?? image.Url))) is { } url && !BadIconUrls.Contains(url) ? url : null;

	/// <summary>The first icon that isn't a banner, replaced by any later one that's larger both ways.</summary>
	private static string? Largest(IEnumerable<(double Width, double Height, string? Url)> images)
	{
		(double Width, double Height, string? Url)? best = null;
		foreach (var image in images)
		{
			if (image.Height > 0 && image.Width / image.Height > 2)
			{
				continue;
			}
			if (best is null || (image.Height > best.Value.Height && image.Width > best.Value.Width))
			{
				best = image;
			}
		}
		return best?.Url;
	}

	private static List<HtmlMetadataFavicon> ResolveFavicons(IReadOnlyList<HtmlTag> tags, string baseUrl)
	{
		var seen = new HashSet<string>();
		var result = new List<HtmlMetadataFavicon>();
		foreach (var tag in tags)
		{
			if (tag.Type != HtmlTagType.Link || string.IsNullOrEmpty(LinkUrl(tag))
				|| !RelValues(tag).Any(rel => rel.Equals("icon", StringComparison.OrdinalIgnoreCase)))
			{
				continue;
			}
			if (AbsoluteUrl(tag, baseUrl) is { } absolute && seen.Add(absolute))
			{
				result.Add(new HtmlMetadataFavicon(Value(tag, "type"), absolute));
			}
		}
		return result;
	}

	private static List<HtmlMetadataAppleTouchIcon> ResolveAppleTouchIcons(IReadOnlyList<HtmlTag> tags, string baseUrl) =>
		[.. tags
			.Where(tag => tag.Type == HtmlTagType.Link
				&& (Value(tag, "rel") ?? "").ToLowerInvariant() is "apple-touch-icon" or "apple-touch-icon-precomposed")
			.Select(tag =>
			{
				var sizes = Value(tag, "sizes");
				var (width, height) = ParsedSize(sizes);
				return new HtmlMetadataAppleTouchIcon(Value(tag, "rel"), sizes, width, height, AbsoluteUrl(tag, baseUrl));
			})];

	private static List<HtmlMetadataFeedLink> ResolveFeedLinks(IReadOnlyList<HtmlTag> tags, string baseUrl) =>
		[.. tags
			.Where(IsFeedLink)
			.Select(tag => new HtmlMetadataFeedLink(Value(tag, "title"), Value(tag, "type"), AbsoluteUrl(tag, baseUrl)))];

	private static bool IsFeedLink(HtmlTag tag)
	{
		if (tag.Type != HtmlTagType.Link || (Value(tag, "rel") ?? "").ToLowerInvariant() != "alternate")
		{
			return false;
		}
		// Alternates for other media (mobile pages) or languages aren't feeds.
		if (!string.IsNullOrEmpty(Value(tag, "media")) || !string.IsNullOrEmpty(Value(tag, "hreflang")))
		{
			return false;
		}
		// Typeless alternates count, since some pages advertise feeds that way; an explicit non-feed type doesn't.
		if (Value(tag, "type") is { Length: > 0 } type
			&& !FeedTypeSuffixes.Any(suffix => type.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
		{
			return false;
		}
		// Skip app-scheme alternates such as android-app://.
		if (LinkUrl(tag) is not { Length: > 0 } url)
		{
			return false;
		}
		var lower = url.ToLowerInvariant();
		return lower.StartsWith("http://", StringComparison.Ordinal) || lower.StartsWith("https://", StringComparison.Ordinal)
			|| lower.StartsWith('/') || lower.StartsWith('.') || !lower.Contains(':');
	}

	private static List<HtmlOpenGraphImage> ResolveOpenGraphImages(IReadOnlyList<HtmlTag> tags)
	{
		var images = new List<HtmlOpenGraphImage>();

		void UpdateCurrent(Func<HtmlOpenGraphImage, HtmlOpenGraphImage> update)
		{
			if (images.Count == 0)
			{
				images.Add(EmptyImage);
			}
			images[^1] = update(images[^1]);
		}

		foreach (var tag in tags)
		{
			if (tag.Type != HtmlTagType.Meta
				|| !tag.Attributes.TryGetValue("property", out var property) || !property.StartsWith("og:", StringComparison.Ordinal)
				|| !tag.Attributes.TryGetValue("content", out var content))
			{
				continue;
			}
			switch (property)
			{
				case "og:image":
					// og:image usually starts a new image.
					if (images.Count == 0 || images[^1].Url is not null)
					{
						images.Add(EmptyImage);
					}
					images[^1] = images[^1] with { Url = content };
					break;
				case "og:image:url":
					UpdateCurrent(image => image with { Url = content });
					break;
				case "og:image:secure_url":
					UpdateCurrent(image => image with { SecureUrl = content });
					break;
				case "og:image:type":
					UpdateCurrent(image => image with { MimeType = content });
					break;
				case "og:image:alt":
					UpdateCurrent(image => image with { AltText = content });
					break;
				case "og:image:width":
					UpdateCurrent(image => image with { Width = ParseDouble(content) ?? 0 });
					break;
				case "og:image:height":
					UpdateCurrent(image => image with { Height = ParseDouble(content) ?? 0 });
					break;
			}
		}
		return images;
	}

	private static readonly HtmlOpenGraphImage EmptyImage = new(null, null, null, 0, 0, null);

	private static string? FindTwitterImageUrl(IReadOnlyList<HtmlTag> tags) => tags
		.Where(tag => tag.Type == HtmlTagType.Meta && tag.Attributes.GetValueOrDefault("name") == "twitter:image:src")
		.Select(tag => tag.Attributes.GetValueOrDefault("content"))
		.FirstOrDefault(content => !string.IsNullOrEmpty(content));

	private static string[] RelValues(HtmlTag tag) =>
		Value(tag, "rel")?.Split([' ', '\t', '\n', '\r', '\f', '\v'], StringSplitOptions.RemoveEmptyEntries) ?? [];

	private static string? LinkUrl(HtmlTag tag) => Value(tag, "href") ?? Value(tag, "src");

	/// <summary><c>URL(string: href, relativeTo: base).absoluteURL.standardized.absoluteString</c>.</summary>
	private static string? AbsoluteUrl(HtmlTag tag, string baseUrl)
	{
		if (LinkUrl(tag) is not { Length: > 0 } relative || FoundationUrl.Parse(baseUrl) is not { } baseParts)
		{
			return null;
		}
		return FoundationUrl.Resolve(relative, baseParts) is { } absolute ? FoundationUrl.Standardize(absolute) : null;
	}

	private static (double Width, double Height) ParsedSize(string? sizes)
	{
		var parts = sizes?.Split('x');
		return parts is [var width, var height] && ParseDouble(width) is { } w && ParseDouble(height) is { } h ? (w, h) : (0, 0);
	}

	/// <summary>Swift's <c>Double(String)</c>: the whole string must be a number.</summary>
	private static double? ParseDouble(string s) =>
		s.Length > 0 && !char.IsWhiteSpace(s[0]) && !char.IsWhiteSpace(s[^1])
			&& double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
			? value
			: null;

	/// <summary>An attribute by exact name, else by case-insensitive name.</summary>
	private static string? Value(HtmlTag tag, string key)
	{
		if (tag.Attributes.TryGetValue(key, out var value))
		{
			return value;
		}
		foreach (var (name, v) in tag.Attributes)
		{
			if (name.Equals(key, StringComparison.OrdinalIgnoreCase))
			{
				return v;
			}
		}
		return null;
	}
}
