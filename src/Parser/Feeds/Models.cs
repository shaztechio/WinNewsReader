using Markdig;

namespace WinNewsReader.Parser.Feeds;

public sealed record ParserData(string Url, byte[] Data);

public enum FeedType
{
	Rss,
	Atom,
	JsonFeed,
	RssInJson,
	Unknown,
	NotAFeed,
}

public enum FeedParserError
{
	RssChannelNotFound,
	RssItemsNotFound,
	JsonFeedVersionNotFound,
	JsonFeedItemsNotFound,
	JsonFeedTitleNotFound,
	InvalidJson,
}

public sealed class FeedParserException(FeedParserError error) : Exception(error.ToString())
{
	public FeedParserError Error { get; } = error;
}

public sealed class ParsedFeed
{
	public required FeedType Type { get; init; }
	public string? Title { get; init; }

	public string? HomePageUrl
	{
		get;
		init => field = string.IsNullOrWhiteSpace(value) ? null : value;
	}

	public string? FeedUrl { get; init; }
	public string? Language { get; init; }
	public string? FeedDescription { get; init; }
	public string? NextUrl { get; init; }
	public string? IconUrl { get; init; }
	public string? FaviconUrl { get; init; }
	public IReadOnlySet<ParsedAuthor>? Authors { get; init; }
	public bool Expired { get; init; }
	public IReadOnlySet<ParsedHub>? Hubs { get; init; }

	/// <summary>A set: exact duplicate items collapse, as in NetNewsWire.</summary>
	public required IReadOnlySet<ParsedItem> Items { get; init; }
}

/// <summary>Hashed by its most identifying non-null field; equal only when every field is equal.</summary>
public sealed record ParsedAuthor(string? Name, string? Url, string? AvatarUrl, string? EmailAddress)
{
	public override int GetHashCode() => (Name ?? Url ?? EmailAddress ?? AvatarUrl ?? "").GetHashCode();
}

public sealed record ParsedHub(string Type, string Url);

public enum AttachmentMedium
{
	Image,
	Audio,
	Video,
	Document,
	Executable,
}

public sealed record ParsedAttachment
{
	public string Url { get; }
	public string? MimeType { get; init; }
	public string? Title { get; init; }
	public int? SizeInBytes { get; init; }
	public int? DurationInSeconds { get; init; }
	public int? Width { get; init; }
	public int? Height { get; init; }
	public AttachmentMedium? Medium { get; init; }

	private ParsedAttachment(string url)
	{
		Url = url;
	}

	/// <summary>Null when <paramref name="url"/> is empty.</summary>
	public static ParsedAttachment? Create(string url, string? mimeType = null, string? title = null, int? sizeInBytes = null,
		int? durationInSeconds = null, int? width = null, int? height = null, AttachmentMedium? medium = null)
	{
		if (url.Length == 0)
		{
			return null;
		}
		return new ParsedAttachment(url)
		{
			MimeType = mimeType,
			Title = title,
			SizeInBytes = sizeInBytes,
			DurationInSeconds = durationInSeconds,
			Width = width,
			Height = height,
			Medium = medium,
		};
	}

	public override int GetHashCode() => Url.GetHashCode();

	/// <summary>Seconds from "SS", "MM:SS" or "HH:MM:SS" (iTunes and Media RSS durations). Null unless positive.</summary>
	public static int? ParseDurationInSeconds(string? s)
	{
		if (s is null)
		{
			return null;
		}
		var components = s.Trim().Split(':');
		if (components.Length > 3)
		{
			return null;
		}
		var seconds = 0.0;
		foreach (var component in components)
		{
			if (!double.TryParse(component, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
				|| !double.IsFinite(value) || value < 0 || component.Length == 0 || char.IsWhiteSpace(component[0]) || char.IsWhiteSpace(component[^1]))
			{
				return null;
			}
			seconds = seconds * 60 + value;
		}
		var whole = Math.Floor(seconds);
		return whole is > 0 and <= int.MaxValue ? (int)whole : null;
	}

	internal ParsedAttachment FilledIn(ParsedAttachment other) => this with
	{
		MimeType = MimeType ?? other.MimeType,
		Title = Title ?? other.Title,
		SizeInBytes = SizeInBytes ?? other.SizeInBytes,
		DurationInSeconds = DurationInSeconds ?? other.DurationInSeconds,
		Width = Width ?? other.Width,
		Height = Height ?? other.Height,
		Medium = Medium ?? other.Medium,
	};

	/// <summary>Case-insensitive Media RSS <c>medium</c>. Null for values the spec doesn't define.</summary>
	internal static AttachmentMedium? MediumFromMediaRss(string? value) => value?.Trim().ToLowerInvariant() switch
	{
		"image" => AttachmentMedium.Image,
		"audio" => AttachmentMedium.Audio,
		"video" => AttachmentMedium.Video,
		"document" => AttachmentMedium.Document,
		"executable" => AttachmentMedium.Executable,
		_ => null,
	};
}

public sealed class ParsedItem : IEquatable<ParsedItem>
{
	/// <summary>Null when not syncing.</summary>
	public string? SyncServiceId { get; init; }

	/// <summary>RSS guid, for instance; may be calculated.</summary>
	public required string UniqueId { get; init; }

	public required string FeedUrl { get; init; }
	public string? Url { get; init; }
	public string? ExternalUrl { get; init; }
	public string? Title { get; init; }
	public string? Language { get; init; }

	/// <summary>When <see cref="Markdown"/> is set and renders to something, this is the rendered HTML.</summary>
	public string? ContentHtml
	{
		get => _renderedMarkdown ?? field;
		init;
	}

	public string? ContentText { get; init; }

	public string? Markdown
	{
		get;
		init
		{
			field = value;
			var rendered = value is null ? null : Markdig.Markdown.ToHtml(value, MarkdownPipeline);
			_renderedMarkdown = string.IsNullOrEmpty(rendered) ? null : rendered;
		}
	}

	private readonly string? _renderedMarkdown;

	public string? Summary { get; init; }
	public string? ImageUrl { get; init; }
	public string? ThumbnailUrl { get; init; }
	public string? BannerImageUrl { get; init; }
	public DateTimeOffset? DatePublished { get; init; }
	public DateTimeOffset? DateModified { get; init; }
	public IReadOnlySet<ParsedAuthor>? Authors { get; init; }
	public IReadOnlySet<string>? Tags { get; init; }
	public IReadOnlyList<ParsedAttachment>? Attachments { get; init; }

	private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

	public override int GetHashCode() => SyncServiceId is not null ? SyncServiceId.GetHashCode() : HashCode.Combine(UniqueId, FeedUrl);

	public override bool Equals(object? obj) => Equals(obj as ParsedItem);

	public bool Equals(ParsedItem? other) =>
		other is not null
		&& SyncServiceId == other.SyncServiceId
		&& UniqueId == other.UniqueId
		&& FeedUrl == other.FeedUrl
		&& Url == other.Url
		&& ExternalUrl == other.ExternalUrl
		&& Title == other.Title
		&& Language == other.Language
		&& ContentHtml == other.ContentHtml
		&& ContentText == other.ContentText
		&& Markdown == other.Markdown
		&& Summary == other.Summary
		&& ImageUrl == other.ImageUrl
		&& ThumbnailUrl == other.ThumbnailUrl
		&& BannerImageUrl == other.BannerImageUrl
		&& DatePublished == other.DatePublished
		&& DateModified == other.DateModified
		&& SetEquals(Authors, other.Authors)
		&& SetEquals(Tags, other.Tags)
		&& (Attachments is null ? other.Attachments is null : other.Attachments is not null && Attachments.SequenceEqual(other.Attachments));

	private static bool SetEquals<T>(IReadOnlySet<T>? a, IReadOnlySet<T>? b) => a is null ? b is null : b is not null && a.SetEquals(b);
}
