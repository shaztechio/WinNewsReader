using WinNewsReader.Parser.Utilities;

namespace WinNewsReader.Parser.Feeds.Xml;

/// <summary>Mutable item under construction, shared by the RSS and Atom parsers.</summary>
internal sealed class RssItem
{
	public string? Guid;
	public string? Title;
	public string? Body;
	public string? Summary;
	public string? Markdown;

	/// <summary>External link.</summary>
	public string? Link;

	/// <summary>URL of the item itself.</summary>
	public string? Permalink;

	public string? Language;
	public DateTimeOffset? DatePublished;
	public DateTimeOffset? DateModified;
	public readonly HashSet<ParsedAuthor> Authors = [];
	public readonly List<ParsedAttachment> Attachments = [];
	public string? ThumbnailUrl;
	public string? ITunesImageUrl;
	public int? ITunesDurationInSeconds;
	public string? MediaDescription;
	public bool MediaDescriptionIsHtml;

	/// <summary><c>media:title</c> of each Gravatar <c>media:content</c>, by URL, in feed order.</summary>
	public readonly List<KeyValuePair<string, string>> GravatarTitlesByUrl = [];

	/// <summary>
	/// The guid if present, otherwise an MD5 of the most stable available properties.
	/// Don't change this: existing article IDs would no longer match and people would get duplicates.
	/// </summary>
	public string UniqueId
	{
		get
		{
			if (!string.IsNullOrEmpty(Guid))
			{
				return Guid;
			}
			var dateString = DatePublished is { } date ? SwiftCompat.FormatNoDecimals(SwiftCompat.TimeIntervalSince1970(date)) : null;
			string s;
			if (!string.IsNullOrEmpty(Permalink) && dateString is not null)
			{
				s = Permalink + dateString;
			}
			else if (!string.IsNullOrEmpty(Link) && dateString is not null)
			{
				s = Link + dateString;
			}
			else if (!string.IsNullOrEmpty(Title) && dateString is not null)
			{
				s = Title + dateString;
			}
			else if (dateString is not null)
			{
				s = dateString;
			}
			else if (!string.IsNullOrEmpty(Permalink))
			{
				s = Permalink;
			}
			else if (!string.IsNullOrEmpty(Link))
			{
				s = Link;
			}
			else if (!string.IsNullOrEmpty(Title))
			{
				s = Title;
			}
			else
			{
				s = Body ?? "";
			}
			return SwiftCompat.Md5String(s);
		}
	}

	public ParsedItem ToParsedItem(string feedUrl)
	{
		// An empty body with a summary: promote the summary to the body.
		var contentHtml = Body;
		var summary = Summary;
		if (string.IsNullOrEmpty(contentHtml) && !string.IsNullOrEmpty(summary))
		{
			contentHtml = summary;
			summary = null;
		}
		// No body or summary: fall back to media:description (YouTube, for instance).
		string? contentText = null;
		if (string.IsNullOrEmpty(contentHtml) && MediaDescription is not null)
		{
			if (MediaDescriptionIsHtml)
			{
				contentHtml = MediaDescription;
			}
			else
			{
				contentText = MediaDescription;
			}
		}
		var (authors, avatarUrls) = AuthorsWithGravatars();
		return new ParsedItem
		{
			UniqueId = UniqueId,
			FeedUrl = feedUrl,
			Url = Permalink,
			ExternalUrl = Link,
			Title = Title,
			Language = Language,
			ContentHtml = contentHtml,
			ContentText = contentText,
			Markdown = Markdown,
			Summary = summary,
			ImageUrl = ITunesImageUrl,
			ThumbnailUrl = ThumbnailUrl,
			DatePublished = DatePublished,
			DateModified = DateModified,
			Authors = authors.Count == 0 ? null : authors,
			Attachments = MergedAttachments(Attachments.Where(attachment => !avatarUrls.Contains(attachment.Url))),
		};
	}

	/// <summary>WordPress adds each author's Gravatar as a media:content titled with their name. It becomes the avatar.</summary>
	private (HashSet<ParsedAuthor> Authors, HashSet<string> AvatarUrls) AuthorsWithGravatars()
	{
		var avatarUrls = new HashSet<string>();
		var authors = new HashSet<ParsedAuthor>();
		foreach (var author in Authors)
		{
			if (author.AvatarUrl is null && author.Name is { } name && GravatarUrl(name) is { } avatarUrl)
			{
				avatarUrls.Add(avatarUrl);
				authors.Add(author with { AvatarUrl = avatarUrl });
			}
			else
			{
				authors.Add(author);
			}
		}
		return (authors, avatarUrls);
	}

	private string? GravatarUrl(string authorName) =>
		GravatarTitlesByUrl.FirstOrDefault(pair => string.Equals(pair.Value, authorName, StringComparison.OrdinalIgnoreCase)).Key;

	/// <summary>
	/// Podcast feeds often list one file as both enclosure and media:content: same-URL attachments merge
	/// into the first. itunes:duration fills in audio and video attachments that lack a duration.
	/// </summary>
	private List<ParsedAttachment>? MergedAttachments(IEnumerable<ParsedAttachment> attachments)
	{
		var merged = new List<ParsedAttachment>();
		var indexesByUrl = new Dictionary<string, int>();
		foreach (var attachment in attachments)
		{
			if (indexesByUrl.TryGetValue(attachment.Url, out var index))
			{
				merged[index] = merged[index].FilledIn(attachment);
			}
			else
			{
				indexesByUrl[attachment.Url] = merged.Count;
				merged.Add(attachment);
			}
		}
		if (merged.Count == 0)
		{
			return null;
		}
		if (ITunesDurationInSeconds is not { } duration)
		{
			return merged;
		}
		return merged.ConvertAll(attachment =>
			attachment.DurationInSeconds is null && IsAudioOrVideo(attachment) ? attachment with { DurationInSeconds = duration } : attachment);
	}

	private static bool IsAudioOrVideo(ParsedAttachment attachment)
	{
		if (attachment.Medium is AttachmentMedium.Audio or AttachmentMedium.Video)
		{
			return true;
		}
		var mimeType = attachment.MimeType?.ToLowerInvariant();
		return mimeType is not null && (mimeType.StartsWith("audio/") || mimeType.StartsWith("video/"));
	}
}
