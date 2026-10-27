using System.Text;
using WinNewsReader.Parser.Utilities;
using static WinNewsReader.Parser.Tests.TestHelpers;

namespace WinNewsReader.Parser.Tests;

/// <summary>Ported from NetNewsWire's DateParserTests.</summary>
public class DateParserTests
{
	private static void AssertParsed(string input, DateTimeOffset expected)
	{
		var fromString = DateParser.Parse(input);
		var fromBytes = DateParser.Parse(Encoding.UTF8.GetBytes(input));
		Assert.Equal(expected, fromString);
		Assert.Equal(fromString, fromBytes);
	}

	private static void AssertNull(string input)
	{
		Assert.Null(DateParser.Parse(input));
		Assert.Null(DateParser.Parse(Encoding.UTF8.GetBytes(input)));
	}

	[Theory]
	[InlineData("Fri, 28 May 2010 21:03:38 GMT")]
	[InlineData("Fri, 28 May 2010 21:03:38 +0000")]
	[InlineData("Fri, 28 May 2010 21:03:38 -0000")]
	[InlineData("Fri, 28 May 2010 21:03:38 +00:00")]
	[InlineData("Fri, 28 May 2010 21:03:38 -00:00")]
	public void PubDateStandardForms(string input) => AssertParsed(input, Utc(2010, 5, 28, 21, 3, 38));

	[Fact]
	public void PubDateWithoutWeekday() => AssertParsed("21 May 2010 21:22:53 GMT", Utc(2010, 5, 21, 21, 22, 53));

	[Fact]
	public void PubDateWithoutSeconds()
	{
		AssertParsed("Fri, 28 May 2010 21:03 GMT", Utc(2010, 5, 28, 21, 3, 0));
		AssertParsed("30 Apr 2010 5:00 PDT", Utc(2010, 4, 30, 12, 0, 0));
	}

	[Theory]
	[InlineData("2010-05-28T21:03:38Z")]
	[InlineData("2010-05-28T21:03:38+00:00")]
	[InlineData("2010-05-28T21:03:38-00:00")]
	[InlineData("2010-05-28T21:03:38+0000")]
	[InlineData("2010-05-28T21:03:38-0000")]
	public void W3CStandardForms(string input) => AssertParsed(input, Utc(2010, 5, 28, 21, 3, 38));

	[Fact]
	public void W3CMissingTSeparator() => AssertParsed("2010-11-17 08:40:07-05:00", Utc(2010, 11, 17, 13, 40, 7));

	[Fact]
	public void SlashDateFormat()
	{
		AssertParsed("2020/1/10 14:33:00", Utc(2020, 1, 10, 14, 33, 0));
		AssertParsed("2020/12/31 23:59:59", Utc(2020, 12, 31, 23, 59, 59));
	}

	[Fact]
	public void W3CWithOffset()
	{
		AssertParsed("2010-11-17T08:40:07-05:00", Utc(2010, 11, 17, 13, 40, 7));
		AssertParsed("2010-07-13T17:06:40+00:00", Utc(2010, 7, 13, 17, 6, 40));
	}

	[Fact]
	public void HalfHourOffsets()
	{
		AssertParsed("2010-05-28T21:03:38+05:30", Utc(2010, 5, 28, 15, 33, 38));
		AssertParsed("2010-05-28T21:03:38+09:30", Utc(2010, 5, 28, 11, 33, 38));
		AssertParsed("2010-05-28T21:03:38-02:30", Utc(2010, 5, 28, 23, 33, 38));
	}

	[Fact]
	public void FortyFiveMinuteOffsets()
	{
		AssertParsed("2010-05-28T21:03:38+05:45", Utc(2010, 5, 28, 15, 18, 38));
		AssertParsed("2010-05-28T21:03:38+12:45", Utc(2010, 5, 28, 8, 18, 38));
	}

	[Fact]
	public void ExtremeNumericOffsets()
	{
		AssertParsed("2010-05-28T21:03:38+14:00", Utc(2010, 5, 28, 7, 3, 38));
		AssertParsed("2010-05-28T21:03:38-12:00", Utc(2010, 5, 29, 9, 3, 38));
	}

	[Fact]
	public void TimeZoneAbbreviations()
	{
		AssertParsed("Wed, 23 Jun 2010 03:43:50 Z", Utc(2010, 6, 23, 3, 43, 50));
		AssertParsed("Wed, 09 Jun 2010 00:00 EST", Utc(2010, 6, 9, 5, 0, 0));
		AssertParsed("Wed, 09 Jun 2010 09:00:00 JST", Utc(2010, 6, 9, 0, 0, 0));
		AssertParsed("Wed, 09 Jun 2010 01:00:00 CET", Utc(2010, 6, 9, 0, 0, 0));
		AssertParsed("Wed, 09 Jun 2010 02:00:00 IST", Utc(2010, 6, 9, 0, 0, 0));
		AssertParsed("Wed, 09 Jun 2010 10:00:00 AEST", Utc(2010, 6, 9, 0, 0, 0));
	}

	[Fact]
	public void LowercaseZ() => AssertParsed("2010-05-28T21:03:38z", Utc(2010, 5, 28, 21, 3, 38));

	[Fact]
	public void UnknownTimeZoneAbbreviationIsUtc() => AssertParsed("Fri, 28 May 2010 21:03:38 XYZ", Utc(2010, 5, 28, 21, 3, 38));

	[Theory]
	[InlineData("Jan", 1)]
	[InlineData("Feb", 2)]
	[InlineData("Mar", 3)]
	[InlineData("Apr", 4)]
	[InlineData("May", 5)]
	[InlineData("Jun", 6)]
	[InlineData("Jul", 7)]
	[InlineData("Aug", 8)]
	[InlineData("Sep", 9)]
	[InlineData("Oct", 10)]
	[InlineData("Nov", 11)]
	[InlineData("Dec", 12)]
	public void AllTwelveMonths(string monthName, int month) => AssertParsed($"15 {monthName} 2020 12:00:00 GMT", Utc(2020, month, 15, 12, 0, 0));

	[Theory]
	[InlineData("Jan")]
	[InlineData("jan")]
	[InlineData("JAN")]
	[InlineData("jAN")]
	public void MonthNamesAreCaseInsensitive(string monthName) => AssertParsed($"15 {monthName} 2020 12:00:00 GMT", Utc(2020, 1, 15, 12, 0, 0));

	[Fact]
	public void TwoDigitYear()
	{
		AssertParsed("Sun, 12 Apr 26 17:24:19 +0000", Utc(2026, 4, 12, 17, 24, 19));
		AssertParsed("12 Apr 26 17:24:19 +0000", Utc(2026, 4, 12, 17, 24, 19));
		AssertParsed("Fri, 28 May 99 21:03:38 +0000", Utc(2099, 5, 28, 21, 3, 38));
		AssertParsed("01 Jan 00 00:00:00 +0000", Utc(2000, 1, 1, 0, 0, 0));
	}

	[Fact]
	public void EpochBoundary() => AssertParsed("1970-01-01T00:00:00Z", DateTimeOffset.UnixEpoch);

	[Fact]
	public void FarFutureYear() => AssertParsed("2099-12-31T23:59:59Z", Utc(2099, 12, 31, 23, 59, 59));

	[Fact]
	public void LeapYearFeb29() => AssertParsed("2020-02-29T12:00:00Z", Utc(2020, 2, 29, 12, 0, 0));

	[Fact]
	public void NonLeapYearFebMarchBoundary()
	{
		AssertParsed("2021-02-28T23:59:59Z", Utc(2021, 2, 28, 23, 59, 59));
		AssertParsed("2021-03-01T00:00:00Z", Utc(2021, 3, 1, 0, 0, 0));
	}

	[Fact]
	public void Milliseconds() => AssertParsed("2019-09-27T21:01:48.000000Z", Utc(2019, 9, 27, 21, 1, 48));

	[Fact]
	public void MillisecondsTruncatedBeyondThreeDigits() =>
		AssertParsed("2021-03-29T10:46:56.516941+00:00", Utc(2021, 3, 29, 10, 46, 56, 516));

	[Theory]
	[InlineData("")]
	[InlineData("abc")]
	[InlineData("2010-")]
	public void TooShortInputIsNull(string input) => AssertNull(input);

	[Fact]
	public void TooLongInputIsNull() => AssertNull(new string('x', 200));

	[Fact]
	public void UnknownMonthNameFallsBackToJanuary()
	{
		AssertParsed("Fri, 28 Xyz 2010 21:03:38 GMT", Utc(2010, 1, 28, 21, 3, 38));
		AssertParsed("28 Xyz 2010 21:03:38 GMT", Utc(2010, 1, 28, 21, 3, 38));
	}

	[Fact]
	public void MonthPrefixMatchAccepted() => AssertParsed("28 Februar 2010 21:03:38 GMT", Utc(2010, 2, 28, 21, 3, 38));

	[Fact]
	public void GarbageInputReturnsEpoch() => AssertParsed("the quick brown fox jumped over", DateTimeOffset.UnixEpoch);

	[Fact]
	public void PartialIsoDateAcceptedWithMissingFieldsZeroed()
	{
		AssertParsed("2020-02", Utc(2020, 2, 1));
		AssertParsed("2020-02-15", Utc(2020, 2, 15));
	}

	[Fact]
	public void MixedSeparatorsTolerated()
	{
		AssertParsed("2020-01/10 14:33:00", Utc(2020, 1, 10, 14, 33, 0));
		AssertParsed("2020/01-10 14:33:00", Utc(2020, 1, 10, 14, 33, 0));
	}
}
