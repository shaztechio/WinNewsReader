using System.Text.Json;
using WinNewsReader.Parser.Utilities;
using WinNewsReader.Parser.Xml;

namespace WinNewsReader.Parser.Feeds.Json;

/// <summary>JSON Feed, https://jsonfeed.org.</summary>
public static class JsonFeedParser
{
	/// <summary>Allows for the mistake of not getting the scheme exactly right.</summary>
	private const string JsonFeedVersionMarker = "://jsonfeed.org/version/";

	public static ParsedFeed Parse(ParserData parserData)
	{
		using var document = JsonValues.ParseDocument(parserData.Data);
		if (document?.RootElement is not { ValueKind: JsonValueKind.Object } d)
		{
			throw new FeedParserException(FeedParserError.InvalidJson);
		}
		if (d.String("version") is not { } version || !version.Contains(JsonFeedVersionMarker))
		{
			throw new FeedParserException(FeedParserError.JsonFeedVersionNotFound);
		}
		if (d.Array("items") is not { } items)
		{
			throw new FeedParserException(FeedParserError.JsonFeedItemsNotFound);
		}
		if (d.String("title") is not { } title)
		{
			throw new FeedParserException(FeedParserError.JsonFeedTitleNotFound);
		}
		return new ParsedFeed
		{
			Type = FeedType.JsonFeed,
			Title = title,
			HomePageUrl = d.String("home_page_url"),
			FeedUrl = d.String("feed_url") ?? parserData.Url,
			Language = d.String("language"),
			FeedDescription = d.String("description"),
			NextUrl = d.String("next_url"),
			IconUrl = d.String("icon"),
			FaviconUrl = d.String("favicon"),
			Authors = ParseAuthors(d),
			Expired = d.Boolean("expired") ?? false,
			Hubs = ParseHubs(d),
			Items = items.Objects().Select(item => ParseItem(item, parserData.Url)).OfType<ParsedItem>().ToHashSet(),
		};
	}

	/// <summary>JSON Feed 1.1 <c>authors</c>, falling back to 1.0's single <c>author</c>.</summary>
	private static HashSet<ParsedAuthor>? ParseAuthors(JsonElement dictionary)
	{
		if (dictionary.Array("authors") is { } authors)
		{
			return authors.Objects().Select(ParseAuthor).OfType<ParsedAuthor>().ToHashSet();
		}
		return dictionary.Object("author") is { } author && ParseAuthor(author) is { } parsed ? [parsed] : null;
	}

	private static ParsedAuthor? ParseAuthor(JsonElement dictionary)
	{
		var name = dictionary.String("name");
		var url = dictionary.String("url");
		var avatar = dictionary.String("avatar");
		return name is null && url is null && avatar is null ? null : new ParsedAuthor(name, url, avatar, null);
	}

	private static HashSet<ParsedHub>? ParseHubs(JsonElement dictionary)
	{
		if (dictionary.Array("hubs") is not { } hubs)
		{
			return null;
		}
		var parsed = hubs.Objects()
			.Select(hub => hub.String("url") is { } url && hub.String("type") is { } type ? new ParsedHub(type, url) : null)
			.OfType<ParsedHub>()
			.ToHashSet();
		return parsed.Count == 0 ? null : parsed;
	}

	private static ParsedItem? ParseItem(JsonElement item, string feedUrl)
	{
		if (ParseUniqueId(item) is not { } uniqueId)
		{
			return null;
		}
		var contentHtml = item.String("content_html");
		var contentText = item.String("content_text");
		if (contentHtml is null && contentText is null)
		{
			return null;
		}
		HashSet<string>? tags = null;
		if (item.Array("tags") is { } tagsArray && tagsArray.EnumerateArray().All(tag => tag.ValueKind == JsonValueKind.String))
		{
			tags = tagsArray.EnumerateArray().Select(tag => tag.GetString()!).ToHashSet();
		}
		return new ParsedItem
		{
			UniqueId = uniqueId,
			FeedUrl = feedUrl,
			Url = item.String("url"),
			ExternalUrl = item.String("external_url"),
			Title = ParseTitle(item, feedUrl),
			Language = item.String("language"),
			ContentHtml = contentHtml,
			ContentText = contentText,
			Summary = item.String("summary"),
			ImageUrl = item.String("image"),
			BannerImageUrl = item.String("banner_image"),
			DatePublished = ParseDate(item.String("date_published")),
			DateModified = ParseDate(item.String("date_modified")),
			Authors = ParseAuthors(item),
			Tags = tags,
			Attachments = ParseAttachments(item),
		};
	}

	private static string? ParseTitle(JsonElement item, string feedUrl)
	{
		var title = item.String("title");
		// Some feeds (Kottke's, Heer's, as of February 2018) put HTML entities in titles.
		return title is not null && IsSpecialCaseTitleWithEntitiesFeed(feedUrl) ? title.DecodingHtmlEntities() : title;
	}

	private static bool IsSpecialCaseTitleWithEntitiesFeed(string feedUrl)
	{
		var lower = feedUrl.ToLowerInvariant();
		return lower.Contains("kottke.org") || lower.Contains("pxlnv.com") || lower.Contains("macstories.net") || lower.Contains("macobserver.com");
	}

	/// <summary>The spec says a string; version 1 also says to coerce a number.</summary>
	private static string? ParseUniqueId(JsonElement item)
	{
		if (!item.TryGetProperty("id", out var id))
		{
			return null;
		}
		return id.ValueKind == JsonValueKind.String ? id.GetString() : JsonValues.NumberDescription(id);
	}

	private static DateTimeOffset? ParseDate(string? dateString) =>
		string.IsNullOrEmpty(dateString) ? null : DateParser.Parse(dateString);

	private static List<ParsedAttachment>? ParseAttachments(JsonElement item)
	{
		if (item.Array("attachments") is not { } attachments)
		{
			return null;
		}
		return attachments.Objects()
			.Select(attachment => attachment.String("url") is { } url
				? ParsedAttachment.Create(
					url,
					attachment.String("mime_type"),
					attachment.String("title"),
					ToInt(attachment.Integer("size_in_bytes")),
					ToInt(attachment.Integer("duration_in_seconds")))
				: null)
			.OfType<ParsedAttachment>()
			.ToList();
	}

	private static int? ToInt(long? value) => value is >= int.MinValue and <= int.MaxValue ? (int)value : null;
}
