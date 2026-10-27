using WinNewsReader.Parser.Feeds.Json;
using WinNewsReader.Parser.Feeds.Xml;

namespace WinNewsReader.Parser.Feeds;

public static class FeedParser
{
	private const int MinNumberOfBytesRequired = 128;

	/// <summary>Sniff the feed type from the bytes. With partial data, JSON that might still be a feed is Unknown.</summary>
	public static FeedType DetectType(ParserData parserData, bool isPartialData = false)
	{
		var data = parserData.Data;
		if (data.Length < MinNumberOfBytesRequired)
		{
			return FeedType.Unknown;
		}
		if (IsProbablyJsonFeed(data))
		{
			return FeedType.JsonFeed;
		}
		if (IsProbablyRssInJson(data))
		{
			return FeedType.RssInJson;
		}
		if (IsProbablyRss(data))
		{
			return FeedType.Rss;
		}
		if (data.AsSpan().IndexOf("<feed"u8) >= 0)
		{
			return FeedType.Atom;
		}
		if (isPartialData && StartsWithAscii(data, "{"u8))
		{
			return FeedType.Unknown;
		}
		return FeedType.NotAFeed;
	}

	public static bool CanParse(ParserData parserData) =>
		DetectType(parserData) is FeedType.JsonFeed or FeedType.RssInJson or FeedType.Rss or FeedType.Atom;

	public static bool MightBeAbleToParseBasedOnPartialData(ParserData parserData) =>
		DetectType(parserData, isPartialData: true) is not FeedType.NotAFeed;

	/// <summary>Null when the data isn't a feed. Throws <see cref="FeedParserException"/> for malformed JSON feeds.</summary>
	public static ParsedFeed? Parse(ParserData parserData) => DetectType(parserData) switch
	{
		FeedType.JsonFeed => JsonFeedParser.Parse(parserData),
		FeedType.RssInJson => RssInJsonParser.Parse(parserData),
		FeedType.Rss => RssParser.Parse(parserData),
		FeedType.Atom => AtomParser.Parse(parserData),
		_ => null,
	};

	public static Task<ParsedFeed?> ParseAsync(ParserData parserData) => Task.Run(() => Parse(parserData));

	/// <summary>JSON that mentions the jsonfeed.org version marker, canonical or with escaped slashes.</summary>
	private static bool IsProbablyJsonFeed(byte[] data) =>
		StartsWithAscii(data, "{"u8)
		&& (data.AsSpan().IndexOf("://jsonfeed.org/version/"u8) >= 0 || data.AsSpan().IndexOf(@":\/\/jsonfeed.org\/version\/"u8) >= 0);

	private static bool IsProbablyRssInJson(byte[] data) =>
		StartsWithAscii(data, "{"u8)
		&& data.AsSpan().IndexOf("rss"u8) >= 0
		&& data.AsSpan().IndexOf("channel"u8) >= 0
		&& data.AsSpan().IndexOf("item"u8) >= 0;

	/// <summary>RSS 2.0 or 1.0 (RDF). The channel/pubDate fallback catches feeds that omit the opening &lt;rss&gt;.</summary>
	private static bool IsProbablyRss(byte[] data)
	{
		var span = data.AsSpan();
		if (span.IndexOf("<rss"u8) >= 0 || span.IndexOf("<rdf:RDF"u8) >= 0)
		{
			return true;
		}
		return span.IndexOf("<channel>"u8) >= 0 && span.IndexOf("<pubDate>"u8) >= 0;
	}

	/// <summary>True if the first bytes past up to 4 BOM bytes and ASCII whitespace begin with <paramref name="needle"/>.</summary>
	private static bool StartsWithAscii(byte[] data, ReadOnlySpan<byte> needle)
	{
		var i = 0;
		while (i < data.Length)
		{
			var b = data[i];
			if (b is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t')
			{
				i++;
				continue;
			}
			if (b == needle[0])
			{
				return data.Length - i >= needle.Length && data.AsSpan(i, needle.Length).SequenceEqual(needle);
			}
			if (i < 4)
			{
				i++;
				continue;
			}
			return false;
		}
		return false;
	}
}
