using System.Globalization;
using WinNewsReader.Parser.Feeds;

namespace WinNewsReader.Parser.Tests;

internal static class TestHelpers
{
	public static ParserData ParserData(string filename, string fileExtension, string url)
	{
		var path = Path.Combine(AppContext.BaseDirectory, "Resources", $"{filename}.{fileExtension}");
		return new ParserData(url, File.ReadAllBytes(path));
	}

	public static ParserData ParserData(string xml, string url) => new(url, System.Text.Encoding.UTF8.GetBytes(xml));

	public static ParsedFeed Parse(ParserData data) => FeedParser.Parse(data) ?? throw new Xunit.Sdk.XunitException("FeedParser returned null");

	public static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0, int milliseconds = 0) =>
		new(year, month, day, hour, minute, second, milliseconds, TimeSpan.Zero);

	public static DateTimeOffset UnixSeconds(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);

	/// <summary>Swift's <c>String.count</c>: extended grapheme clusters.</summary>
	public static int GraphemeCount(string s) => new StringInfo(s).LengthInTextElements;
}
