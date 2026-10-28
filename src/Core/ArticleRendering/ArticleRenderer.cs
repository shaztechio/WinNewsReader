using System.Globalization;
using System.Text;
using WinNewsReader.Parser.Feeds;
using WinNewsReader.Store;

namespace WinNewsReader.Core.ArticleRendering;

/// <summary>A rendered article: the theme's CSS, the filled-in template, the title, and the base URL for relative links.</summary>
public sealed record ArticleRendering(string Style, string Html, string Title, string BaseUrl);

/// <summary>What the renderer needs beyond the article: its feed, and the reader's display choices.</summary>
/// <param name="TextSizeClass">One of NetNewsWire's article text size classes, such as <c>largeText</c>.</param>
/// <param name="AvatarSource">The image URL for the feed icon in the article header.</param>
public sealed record ArticleRenderContext(StoredFeed? Feed, bool IsFreshRss, string TextSizeClass, string AvatarSource);

/// <summary>NetNewsWire's article text sizes, as the CSS classes its stylesheets style.</summary>
public enum ArticleTextSize
{
	Small = 1,
	Medium,
	Large,
	ExtraLarge,
	ExtraExtraLarge,
}

/// <summary>
/// NetNewsWire's <c>ArticleRenderer</c>: fills a theme's template with an article, then wraps it in <c>page.html</c>.
/// </summary>
public static class ArticleRenderer
{
	private const int SecondsPerMinute = 60;
	private const int SecondsPerHour = 3600;

	private static readonly string[] VisualElementTags = ["<img", "<picture", "<video", "<iframe"];
	private static readonly string[] AudioFileExtensions = ["mp3", "m4a", "aac", "wav", "ogg", "oga", "opus", "flac"];
	private static readonly string[] VideoFileExtensions = ["mp4", "m4v", "mov", "webm"];
	private static readonly string[] ImageFileExtensions = ["jpg", "jpeg", "png", "gif", "webp", "heic", "avif"];

	public static string CssClass(this ArticleTextSize size) => size switch
	{
		ArticleTextSize.Small => "smallText",
		ArticleTextSize.Medium => "mediumText",
		ArticleTextSize.ExtraLarge => "xLargeText",
		ArticleTextSize.ExtraExtraLarge => "xxLargeText",
		_ => "largeText",
	};

	/// <summary>
	/// Renders the article, or with <paramref name="extracted"/>, Reader View's version of it: the extracted body, with
	/// the web page as the base URL, and the feed's own fixes left out since the content didn't come from the feed.
	/// </summary>
	public static ArticleRendering Render(StoredArticle article, ArticleRenderContext context, ArticleTheme theme, ExtractedArticle? extracted = null)
	{
		var title = ArticleText.SanitizedTitle(article.Title, forHtml: true) ?? "";
		var feed = context.Feed;
		string body;
		string? baseUrl;
		if (extracted is not null)
		{
			body = ArticleRenderingSpecialCases.ExtractBodyFragmentIfNeeded(extracted.Content);
			baseUrl = extracted.Url;
		}
		else
		{
			body = ArticleRenderingSpecialCases.ExtractBodyFragmentIfNeeded(BodyHtml(article));
			body = ArticleRenderingSpecialCases.InsertParagraphTagsIfNeeded(body, feed?.Url, feed?.HomePageUrl);
			body = ArticleRenderingSpecialCases.RemoveStrayLineBreaksIfNeeded(body, feed?.Url, feed?.HomePageUrl);
			if (context.IsFreshRss)
			{
				body = ArticleRenderingSpecialCases.DesanitizeFreshRssContent(body);
			}
			baseUrl = BaseUrl(article, feed);
		}
		var html = MacroProcessor.RenderedText(theme.Template, Substitutions(article, context, theme, title, body));
		return new ArticleRendering(theme.Css, html, title, baseUrl ?? "");
	}

	/// <summary>What NetNewsWire shows with nothing selected: the theme's styling and an empty body.</summary>
	public static ArticleRendering NoContent(ArticleTheme theme) => new(theme.Css, "", "", "");

	/// <summary>What NetNewsWire shows while Reader View fetches an article.</summary>
	public static ArticleRendering Loading(ArticleTheme theme) => new(theme.Css, "<h3 class='systemMessage'>Loading…</h3>", "", "");

	/// <summary>The full page for a rendering, ready to load into the article view.</summary>
	public static string PageHtml(ArticleRendering rendering, string pageTemplate, int windowScrollY = 0)
	{
		var html = MacroProcessor.RenderedText(pageTemplate, new Dictionary<string, string>
		{
			["title"] = rendering.Title,
			["baseURL"] = rendering.BaseUrl,
			["style"] = rendering.Style,
			["body"] = rendering.Html,
			["windowScrollY"] = windowScrollY.ToString(CultureInfo.InvariantCulture),
		});
		return ArticleRenderingSpecialCases.FilterHtmlIfNeeded(rendering.BaseUrl, html);
	}

	/// <summary>
	/// Audio and video players and images for an article's attachments. Empty when there's nothing to show. Skips
	/// attachments the body already shows; shows <paramref name="imageUrl"/> only when nothing else visual is.
	/// </summary>
	public static string EnclosuresHtml(IReadOnlyList<ParsedAttachment>? attachments, string body, string? imageUrl)
	{
		var enclosures = new List<string>();
		var hasVisualEnclosure = false;
		foreach (var attachment in (attachments ?? []).Where(attachment => !BodyContainsUrl(body, attachment.Url)))
		{
			if (EnclosureHtml(attachment, imageUrl) is { } enclosure)
			{
				enclosures.Add(enclosure);
				hasVisualEnclosure |= IsVisual(attachment);
			}
		}
		if (!hasVisualEnclosure && !string.IsNullOrEmpty(imageUrl) && !BodyContainsVisualElement(body) && !BodyContainsUrl(body, imageUrl))
		{
			enclosures.Insert(0, $"<div class=\"nnw-enclosure nnw-enclosure-image\"><img src=\"{ArticleText.EscapingSpecialXmlCharacters(imageUrl)}\" alt=\"\"></div>");
		}
		return enclosures.Count == 0 ? "" : $"<div class=\"nnw-enclosures\">\n{string.Join("\n", enclosures)}\n</div>";
	}

	/// <summary>The body as HTML; plain text (used when there's no HTML) is converted.</summary>
	private static string BodyHtml(StoredArticle article) =>
		article.ContentHtml is null && article.ContentText is { } text ? ArticleText.PlainTextToHtml(text) : article.ContentHtml ?? article.Summary ?? "";

	private static Dictionary<string, string> Substitutions(StoredArticle article, ArticleRenderContext context, ArticleTheme theme, string title, string body)
	{
		var preferredLink = article.Url ?? article.ExternalUrl ?? "";
		var externalLink = article.ExternalUrl is { } external && external != preferredLink ? external : null;
		var date = article.LogicalDatePublished.ToLocalTime();
		var culture = CultureInfo.CurrentCulture.DateTimeFormat;
		// Apple's medium date style, such as "Oct 28, 2026": the long pattern without the weekday, with short month names.
		var mediumDatePattern = culture.LongDatePattern.Replace("dddd, ", "").Replace("dddd ", "").Replace("MMMM", "MMM");
		var dateLong = date.ToString("D");
		var dateMedium = date.ToString(mediumDatePattern);
		var dateShort = date.ToString("d");

		// Templates without [[enclosures]] get them after the body. The wrapper closes anything the body leaves open,
		// such as an unclosed <audio>, so the enclosures can't end up inside it.
		var enclosures = EnclosuresHtml(article.Attachments, body, article.ImageUrl ?? article.ThumbnailUrl);
		var templateHasEnclosures = theme.Template.Contains("[[enclosures]]", StringComparison.Ordinal);

		return new Dictionary<string, string>
		{
			["title"] = title,
			["preferred_link"] = preferredLink,
			["external_link_label"] = externalLink is null ? "" : "Link:",
			["external_link_stripped"] = externalLink is null ? "" : ArticleText.StrippingHttpOrHttpsScheme(externalLink),
			["external_link"] = externalLink ?? "",
			["body"] = templateHasEnclosures || enclosures.Length == 0 ? body : "<div>" + body + "</div>" + enclosures,
			["enclosures"] = templateHasEnclosures ? enclosures : "",
			["text_size_class"] = context.TextSizeClass,
			["avatar_src"] = context.AvatarSource,
			["dateline_style"] = title.Length == 0 ? "articleDatelineTitle" : "articleDateline",
			["feed_link_title"] = context.Feed is { } feed ? NameSorting.DisplayName(feed) : "",
			["feed_link"] = context.Feed?.HomePageUrl ?? "",
			["byline"] = Byline(article.Authors, context.Feed),
			["datetime_long"] = $"{dateLong}, {date:T}",
			["datetime_medium"] = $"{dateMedium}, {date:t}",
			["datetime_short"] = $"{dateShort}, {date:t}",
			["date_long"] = dateLong,
			["date_medium"] = dateMedium,
			["date_short"] = dateShort,
			["time_long"] = date.ToString("T"),
			["time_medium"] = date.ToString("T"),
			["time_short"] = date.ToString("t"),
		};
	}

	/// <summary>
	/// NetNewsWire's byline: the authors, linked when they have a URL. A single author named like the feed is left out,
	/// and no-reply addresses aren't shown.
	/// </summary>
	private static string Byline(IReadOnlyList<ParsedAuthor>? authors, StoredFeed? feed)
	{
		if (authors is not { Count: > 0 })
		{
			return "";
		}
		if (authors.Count == 1 && feed is not null && authors[0].Name == NameSorting.DisplayName(feed))
		{
			return "";
		}
		var byline = new StringBuilder();
		foreach (var author in authors)
		{
			if (byline.Length > 0)
			{
				byline.Append(", ");
			}
			var email = author.EmailAddress is { } address && !address.Contains("noreply@") && !address.Contains("no-reply@") ? address : null;
			if (email is not null && email.Contains(' '))
			{
				byline.Append(email); // Probably a name and an address together.
			}
			else if (author.Name is { } name && author.Url is { } url)
			{
				byline.Append($"<a href=\"{url}\">{name}</a>");
			}
			else if (author.Name is { } nameWithEmail && email is not null)
			{
				byline.Append($"{nameWithEmail} &lt;{email}&gt;");
			}
			else if (author.Name is { } onlyName)
			{
				byline.Append(onlyName);
			}
			else if (email is not null)
			{
				byline.Append($"&lt;{email}&gt;");
			}
			else if (author.Url is { } onlyUrl)
			{
				byline.Append($"<a href=\"{onlyUrl}\">{onlyUrl}</a>");
			}
		}
		return byline.ToString();
	}

	/// <summary>
	/// The article's link, else the feed's home page, else the feed: without a fragment, which the article view can't
	/// load, and only http(s). YouTube articles get NetNewsWire's site instead.
	/// </summary>
	private static string? BaseUrl(StoredArticle article, StoredFeed? feed)
	{
		if ((article.Url ?? feed?.HomePageUrl ?? feed?.Url) is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
			|| (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
		{
			return null;
		}
		var withoutFragment = new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
		return ArticleRenderingSpecialCases.BaseUrlForRendering(withoutFragment);
	}

	private static string? EnclosureHtml(ParsedAttachment attachment, string? posterUrl)
	{
		if (ArticleRenderingSpecialCases.YouTubeEmbedUrl(attachment.Url) is { } embedUrl)
		{
			return $"<div class=\"nnw-enclosure nnw-enclosure-video\"><iframe src=\"{embedUrl}\"{DimensionAttributes(attachment)} allowfullscreen></iframe></div>";
		}
		if (DisplayMedium(attachment) is not { } medium)
		{
			return null;
		}
		var src = ArticleText.EscapingSpecialXmlCharacters(attachment.Url);
		string element;
		string? caption;
		switch (medium)
		{
			case AttachmentMedium.Audio:
				element = $"<audio controls preload=\"none\" src=\"{src}\"></audio>";
				caption = AudioCaption(attachment);
				break;
			case AttachmentMedium.Video:
				var attributes = $"controls preload=\"metadata\" playsinline src=\"{src}\"{DimensionAttributes(attachment)}";
				if (!string.IsNullOrEmpty(posterUrl))
				{
					attributes += $" poster=\"{ArticleText.EscapingSpecialXmlCharacters(posterUrl)}\"";
				}
				element = $"<video {attributes}></video>";
				caption = attachment.Title;
				break;
			default:
				element = $"<img src=\"{src}\" alt=\"\"{DimensionAttributes(attachment)}>";
				caption = attachment.Title;
				break;
		}
		var html = $"<div class=\"nnw-enclosure nnw-enclosure-{medium.ToString().ToLowerInvariant()}\">{element}";
		if (!string.IsNullOrEmpty(caption))
		{
			html += $"<div class=\"nnw-enclosure-caption\">{ArticleText.EscapingSpecialXmlCharacters(caption)}</div>";
		}
		return html + "</div>";
	}

	/// <summary>NetNewsWire's <c>Attachment.displayMedium</c>: from the MIME type, then the medium, then the file extension.</summary>
	private static AttachmentMedium? DisplayMedium(ParsedAttachment attachment)
	{
		if (attachment.MimeType?.ToLowerInvariant() is { } mimeType)
		{
			if (mimeType.StartsWith("audio/", StringComparison.Ordinal))
			{
				return AttachmentMedium.Audio;
			}
			if (mimeType.StartsWith("video/", StringComparison.Ordinal))
			{
				return AttachmentMedium.Video;
			}
			if (mimeType.StartsWith("image/", StringComparison.Ordinal))
			{
				return AttachmentMedium.Image;
			}
		}
		if (attachment.Medium is AttachmentMedium.Audio or AttachmentMedium.Video or AttachmentMedium.Image)
		{
			return attachment.Medium;
		}
		var extension = Uri.TryCreate(attachment.Url, UriKind.Absolute, out var uri) ? Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToLowerInvariant() : "";
		return AudioFileExtensions.Contains(extension) ? AttachmentMedium.Audio
			: VideoFileExtensions.Contains(extension) ? AttachmentMedium.Video
			: ImageFileExtensions.Contains(extension) ? AttachmentMedium.Image
			: null;
	}

	/// <summary>Title (or file name), duration and size, such as "Episode 311 · 1:54:09 · 54.9 MB".</summary>
	private static string AudioCaption(ParsedAttachment attachment)
	{
		var parts = new List<string>();
		if (!string.IsNullOrEmpty(attachment.Title))
		{
			parts.Add(attachment.Title);
		}
		else if (FileName(attachment.Url) is { } fileName)
		{
			parts.Add(fileName);
		}
		if (attachment.DurationInSeconds is { } duration)
		{
			parts.Add(DurationString(duration));
		}
		if (attachment.SizeInBytes is { } size)
		{
			parts.Add(FileSizeString(size));
		}
		return string.Join(" · ", parts);
	}

	private static string DimensionAttributes(ParsedAttachment attachment) =>
		attachment is { Width: { } width, Height: { } height } ? $" width=\"{width}\" height=\"{height}\"" : "";

	private static string DurationString(int seconds)
	{
		var hours = seconds / SecondsPerHour;
		var minutes = seconds % SecondsPerHour / SecondsPerMinute;
		var remainder = seconds % SecondsPerMinute;
		return hours > 0 ? $"{hours}:{minutes:00}:{remainder:00}" : $"{minutes}:{remainder:00}";
	}

	/// <summary>Like Apple's file-size formatting: decimal units, to one decimal place above kilobytes.</summary>
	private static string FileSizeString(int bytes) => bytes switch
	{
		< 1_000 => $"{bytes} bytes",
		< 1_000_000 => $"{Math.Round(bytes / 1_000.0)} KB",
		< 1_000_000_000 => $"{bytes / 1_000_000.0:0.0} MB",
		_ => $"{bytes / 1_000_000_000.0:0.00} GB",
	};

	private static string? FileName(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
		{
			return null;
		}
		var name = Path.GetFileName(uri.AbsolutePath);
		return name.Length == 0 ? null : Uri.UnescapeDataString(name);
	}

	private static bool IsVisual(ParsedAttachment attachment) =>
		ArticleRenderingSpecialCases.YouTubeEmbedUrl(attachment.Url) is not null || DisplayMedium(attachment) is AttachmentMedium.Image or AttachmentMedium.Video;

	private static bool BodyContainsVisualElement(string body) => VisualElementTags.Any(tag => body.Contains(tag, StringComparison.OrdinalIgnoreCase));

	/// <summary>Body HTML escapes &amp; in attributes, so both forms of the URL count.</summary>
	private static bool BodyContainsUrl(string body, string url) =>
		body.Contains(url, StringComparison.Ordinal) || body.Contains(ArticleText.EscapingSpecialXmlCharacters(url), StringComparison.Ordinal);
}
