using System.Globalization;
using WinNewsReader.Parser.Utilities;
using WinNewsReader.Parser.Xml;

namespace WinNewsReader.Parser.Feeds.Xml;

internal static class MediaRss
{
	/// <summary>Attachment from a <c>media:content</c> element. <paramref name="url"/> is the resolved url attribute.</summary>
	public static ParsedAttachment? Attachment(string url, XmlAttributes attributes) => ParsedAttachment.Create(
		url,
		mimeType: attributes.ValueIgnoringCase("type"),
		// The spec says fileSize. Some feeds (atp.fm) use length, as in RSS enclosures.
		sizeInBytes: PositiveInt(attributes.ValueIgnoringCase("fileSize") ?? attributes.ValueIgnoringCase("length")),
		durationInSeconds: ParsedAttachment.ParseDurationInSeconds(attributes.ValueIgnoringCase("duration")),
		width: PositiveInt(attributes.ValueIgnoringCase("width")),
		height: PositiveInt(attributes.ValueIgnoringCase("height")),
		medium: ParsedAttachment.MediumFromMediaRss(attributes.ValueIgnoringCase("medium")));

	public static bool IsGravatarAvatar(string urlString)
	{
		if (FoundationUrl.Parse(urlString) is not { } url || url.Host?.ToLowerInvariant() is not { } host)
		{
			return false;
		}
		return (host == "gravatar.com" || host.EndsWith(".gravatar.com")) && url.Path.StartsWith("/avatar/");
	}

	public static bool IsDefault(XmlAttributes attributes) =>
		string.Equals(attributes.ValueIgnoringCase("isDefault"), "true", StringComparison.OrdinalIgnoreCase);

	/// <summary>Swift's <c>Int(s)</c> after trimming: an optional sign and ASCII digits only.</summary>
	internal static int? StrictInt(string? s) =>
		s is not null && int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;

	private static int? PositiveInt(string? s) => StrictInt(s?.Trim()) is > 0 and var value ? value : null;
}

/// <summary>
/// Tracks Media RSS elements inside an item: groups (choosing the default rendition), content, Gravatar
/// titles, descriptions and thumbnails.
/// </summary>
internal sealed class MediaElementTracker
{
	/// <summary>
	/// Depth inside media:group, media:content, media:description and a Gravatar's media:title. The parsers
	/// skip non-Media RSS elements in there, so an unprefixed child (&lt;title&gt;) can't be mistaken for the item's own.
	/// </summary>
	public int ContainerDepth { get; private set; }

	private MediaGroup? _group;

	/// <summary>Set inside a media:content that's a Gravatar avatar, so its media:title is kept.</summary>
	private string? _gravatarUrl;

	public void Reset()
	{
		ContainerDepth = 0;
		_group = null;
		_gravatarUrl = null;
	}

	/// <summary>Returns true when the parser should skip the element's subtree.</summary>
	public bool Start(ReadOnlySpan<byte> localName, XmlAttributes attributes, RssItem item, XmlSaxParser parser, Func<string, string?> resolveUrl)
	{
		if (localName.SequenceEqual("group"u8))
		{
			_group = new MediaGroup();
			ContainerDepth++;
			return false;
		}
		if (localName.SequenceEqual("content"u8))
		{
			if (attributes.ValueIgnoringCase("url") is { } urlString && resolveUrl(urlString) is { } url
				&& MediaRss.Attachment(url, attributes) is { } attachment)
			{
				if (_group is not null)
				{
					_group.Add(attachment, MediaRss.IsDefault(attributes));
				}
				else
				{
					item.Attachments.Add(attachment);
					if (MediaRss.IsGravatarAvatar(url))
					{
						_gravatarUrl = url;
					}
				}
			}
			ContainerDepth++;
			return false;
		}
		if (localName.SequenceEqual("title"u8) && _gravatarUrl is not null)
		{
			parser.BeginStoringCharacters();
			ContainerDepth++;
			return false;
		}
		if (localName.SequenceEqual("description"u8))
		{
			if (item.MediaDescription is null)
			{
				item.MediaDescriptionIsHtml = string.Equals(attributes.ValueIgnoringCase("type"), "html", StringComparison.OrdinalIgnoreCase);
				parser.BeginStoringCharacters();
			}
			ContainerDepth++;
			return false;
		}
		if (localName.SequenceEqual("thumbnail"u8) && item.ThumbnailUrl is null
			&& attributes.ValueIgnoringCase("url") is { Length: > 0 } thumbnailUrl)
		{
			item.ThumbnailUrl = resolveUrl(thumbnailUrl);
		}
		return true;
	}

	public void End(ReadOnlySpan<byte> localName, RssItem item, XmlSaxParser parser)
	{
		if (localName.SequenceEqual("group"u8))
		{
			if (_group?.ChosenAttachment is { } attachment)
			{
				item.Attachments.Add(attachment);
			}
			_group = null;
			ContainerDepth--;
		}
		else if (localName.SequenceEqual("content"u8))
		{
			_gravatarUrl = null;
			ContainerDepth--;
		}
		else if (localName.SequenceEqual("title"u8))
		{
			if (_gravatarUrl is not null && parser.CurrentStringWithTrimmedWhitespace is { Length: > 0 } title)
			{
				item.GravatarTitlesByUrl.RemoveAll(pair => pair.Key == _gravatarUrl);
				item.GravatarTitlesByUrl.Add(new(_gravatarUrl, title));
			}
			ContainerDepth--;
		}
		else if (localName.SequenceEqual("description"u8))
		{
			item.MediaDescription ??= parser.CurrentStringWithTrimmedWhitespace;
			ContainerDepth--;
		}
	}

	private sealed class MediaGroup
	{
		private ParsedAttachment? _defaultAttachment;
		private ParsedAttachment? _firstAttachment;

		public ParsedAttachment? ChosenAttachment => _defaultAttachment ?? _firstAttachment;

		public void Add(ParsedAttachment attachment, bool isDefault)
		{
			_firstAttachment ??= attachment;
			if (isDefault)
			{
				_defaultAttachment ??= attachment;
			}
		}
	}
}
