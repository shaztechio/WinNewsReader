using System.Globalization;
using System.Text.Json;
using WinNewsReader.Parser.Utilities;

namespace WinNewsReader.Parser.Feeds.Json;

/// <summary>RSS translated to JSON, as scripting.com publishes.</summary>
public static class RssInJsonParser
{
	public static ParsedFeed Parse(ParserData parserData)
	{
		using var document = JsonValues.ParseDocument(parserData.Data);
		if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root)
		{
			throw new FeedParserException(FeedParserError.InvalidJson);
		}
		if (root.Object("rss")?.Object("channel") is not { } channel)
		{
			throw new FeedParserException(FeedParserError.RssChannelNotFound);
		}
		// In practice the items array won't always be inside the channel, or called "item".
		var items = channel.Array("item") ?? root.Array("item") ?? channel.Array("items") ?? root.Array("items")
			?? throw new FeedParserException(FeedParserError.RssItemsNotFound);

		return new ParsedFeed
		{
			Type = FeedType.RssInJson,
			Title = channel.String("title"),
			HomePageUrl = channel.String("link"),
			FeedUrl = parserData.Url,
			Language = channel.String("language"),
			FeedDescription = channel.String("description"),
			IconUrl = channel.Object("image")?.String("url"),
			Items = items.Objects().Select(item => ParseItem(item, parserData.Url)).OfType<ParsedItem>().ToHashSet(),
		};
	}

	private static ParsedItem? ParseItem(JsonElement item, string feedUrl)
	{
		var externalUrl = item.String("link");
		var title = item.String("title");
		var contentHtml = item.String("description");
		string? contentText = null;
		if (contentHtml is not null && !contentHtml.Contains('<'))
		{
			contentText = contentHtml;
			contentHtml = null;
		}
		if (contentHtml is null && contentText is null && title is null)
		{
			return null;
		}
		var datePublished = item.String("pubDate") is { } pubDate ? DateParser.Parse(pubDate) : null;
		HashSet<ParsedAuthor>? authors = item.String("author") is { } email ? [new ParsedAuthor(null, null, null, email)] : null;
		var attachments = ParseAttachments(item);

		var uniqueId = item.String("guid");
		if (uniqueId is null)
		{
			// Items should have guids. Without one, build an ID from the non-empty elements and hash it.
			// It's valid only for this feed, just like JSON Feed ids.
			var s = "";
			if (datePublished is { } date)
			{
				s += SwiftCompat.DoubleDescription(SwiftCompat.TimeIntervalSince1970(date));
			}
			s += title + externalUrl + authors?.First().EmailAddress + attachments?[0].Url;
			if (s.Length == 0)
			{
				s = contentText ?? contentHtml ?? "";
			}
			uniqueId = SwiftCompat.Md5String(s);
		}
		return new ParsedItem
		{
			UniqueId = uniqueId,
			FeedUrl = feedUrl,
			ExternalUrl = externalUrl,
			Title = title,
			ContentHtml = contentHtml,
			ContentText = contentText,
			DatePublished = datePublished,
			Authors = authors,
			Tags = ParseTags(item),
			Attachments = attachments,
		};
	}

	private static HashSet<string>? ParseTags(JsonElement item)
	{
		if (item.Object("category") is { } category)
		{
			return category.String("#value") is { } tag ? [tag] : null;
		}
		if (item.Array("category") is { } categories)
		{
			return categories.Objects().Select(c => c.String("#value")).OfType<string>().ToHashSet();
		}
		return null;
	}

	private static List<ParsedAttachment>? ParseAttachments(JsonElement item)
	{
		if (item.Object("enclosure") is not { } enclosure || enclosure.String("url") is not { } url)
		{
			return null;
		}
		var size = enclosure.Integer("length");
		if (size is null && enclosure.String("length") is { } lengthString)
		{
			size = IntegerValue(lengthString);
		}
		return ParsedAttachment.Create(url, enclosure.String("type"), sizeInBytes: size is >= int.MinValue and <= int.MaxValue ? (int)size : null) is { } attachment
			? [attachment]
			: null;
	}

	/// <summary>NSString.integerValue: leading whitespace, optional sign, then digits; 0 if none.</summary>
	private static long IntegerValue(string s)
	{
		var trimmed = s.TrimStart();
		var end = 0;
		if (end < trimmed.Length && trimmed[end] is '+' or '-')
		{
			end++;
		}
		while (end < trimmed.Length && char.IsAsciiDigit(trimmed[end]))
		{
			end++;
		}
		return long.TryParse(trimmed.AsSpan(0, end), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : 0;
	}
}
