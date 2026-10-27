using System.Text;
using WinNewsReader.Parser.Xml;

namespace WinNewsReader.Parser.Utilities;

/// <summary>
/// Liberal parser for RFC 822 pubDates and W3C/ISO 8601 dates, ported from NetNewsWire so that the
/// same input gives the same instant (calculated article IDs depend on it). Garbage input yields the epoch
/// rather than null; only inputs shorter than 6 or longer than 150 bytes return null.
/// </summary>
public static class DateParser
{
	public static DateTimeOffset? Parse(string dateString) => Parse(Encoding.UTF8.GetBytes(dateString));

	public static DateTimeOffset? Parse(ReadOnlySpan<byte> bytes)
	{
		if (bytes.Length is < 6 or > 150)
		{
			return null;
		}
		if (LooksLikeW3CDate(bytes))
		{
			return ParseW3CDate(bytes);
		}
		if (bytes.Contains((byte)' ') || bytes.Contains((byte)','))
		{
			return ParsePubDate(bytes);
		}
		return ParseW3CDate(bytes);
	}

	private static bool LooksLikeW3CDate(ReadOnlySpan<byte> s)
	{
		for (var i = 0; i < s.Length; i++)
		{
			if (AsciiBytes.IsWhitespace(s[i]))
			{
				continue;
			}
			if (s.Length - i < 5)
			{
				return false;
			}
			return AsciiBytes.IsDigit(s[i]) && AsciiBytes.IsDigit(s[i + 1]) && AsciiBytes.IsDigit(s[i + 2]) && AsciiBytes.IsDigit(s[i + 3])
				&& s[i + 4] is (byte)'-' or (byte)'/';
		}
		return false;
	}

	private static DateTimeOffset? ParsePubDate(ReadOnlySpan<byte> s)
	{
		var finalIndex = 0;
		var day = NextNumericValue(s, 0, 2, ref finalIndex);
		if (day is null or < 1)
		{
			day = 1;
		}
		var month = NextMonthValue(s, finalIndex + 1, ref finalIndex) ?? 1;
		var year = NextNumericValue(s, finalIndex + 1, 4, ref finalIndex);
		if (year is < 100)
		{
			year += 2000;
		}
		var hour = Math.Max(NextNumericValue(s, finalIndex + 1, 2, ref finalIndex) ?? 0, 0);
		var minute = Math.Max(NextNumericValue(s, finalIndex + 1, 2, ref finalIndex) ?? 0, 0);
		var currentIndex = finalIndex + 1;
		var second = 0;
		if (currentIndex < s.Length && s[currentIndex] == ':')
		{
			second = NextNumericValue(s, currentIndex, 2, ref finalIndex) ?? 0;
		}
		currentIndex = finalIndex + 1;
		var timeZoneOffset = 0;
		if (currentIndex < s.Length && s[currentIndex] == ' ')
		{
			timeZoneOffset = ParsedTimeZoneOffset(s, currentIndex);
		}
		return DateWithComponents(year ?? 1970, month, day.Value, hour, minute, second, 0, timeZoneOffset);
	}

	private static DateTimeOffset? ParseW3CDate(ReadOnlySpan<byte> s)
	{
		var finalIndex = 0;
		var year = NextNumericValue(s, 0, 4, ref finalIndex) ?? 1970;
		var month = NextNumericValue(s, finalIndex + 1, 2, ref finalIndex) ?? 1;
		var day = NextNumericValue(s, finalIndex + 1, 2, ref finalIndex) ?? 1;
		var hour = NextNumericValue(s, finalIndex + 1, 2, ref finalIndex) ?? 0;
		var minute = NextNumericValue(s, finalIndex + 1, 2, ref finalIndex) ?? 0;
		var second = NextNumericValue(s, finalIndex + 1, 2, ref finalIndex) ?? 0;
		var currentIndex = finalIndex + 1;
		var milliseconds = 0;
		if (currentIndex < s.Length && s[currentIndex] == '.')
		{
			milliseconds = NextNumericValue(s, currentIndex, 3, ref finalIndex) ?? 0;
			currentIndex = finalIndex + 1;
			while (currentIndex < s.Length && AsciiBytes.IsDigit(s[currentIndex]))
			{
				currentIndex++;
			}
		}
		var timeZoneOffset = ParsedTimeZoneOffset(s, currentIndex);
		return DateWithComponents(year, month, day, hour, minute, second, milliseconds, timeZoneOffset);
	}

	/// <summary>Days-from-civil (Howard Hinnant), so out-of-range components roll over instead of failing.</summary>
	private static DateTimeOffset? DateWithComponents(long year, long month, long day, long hour, long minute, long second, long milliseconds, long timeZoneOffset)
	{
		var shiftedYear = month <= 2 ? year - 1 : year;
		var era = (shiftedYear >= 0 ? shiftedYear : shiftedYear - 399) / 400;
		var yearOfEra = shiftedYear - era * 400;
		var shiftedMonth = month > 2 ? month - 3 : month + 9;
		var dayOfYear = (153 * shiftedMonth + 2) / 5 + day - 1;
		var dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;
		var daysSinceEpoch = era * 146097 + dayOfEra - 719468;
		var epochSeconds = daysSinceEpoch * 86400 + hour * 3600 + minute * 60 + second - timeZoneOffset;
		try
		{
			return DateTimeOffset.FromUnixTimeMilliseconds(epochSeconds * 1000 + Math.Max(milliseconds, 0));
		}
		catch (ArgumentOutOfRangeException)
		{
			return null;
		}
	}

	private static int? NextNumericValue(ReadOnlySpan<byte> s, int startingIndex, int maxDigits, ref int finalIndex)
	{
		var limit = Math.Min(maxDigits, 4);
		var i = startingIndex;
		while (i < s.Length)
		{
			if (AsciiBytes.IsDigit(s[i]))
			{
				break;
			}
			finalIndex = i;
			i++;
		}
		if (i >= s.Length)
		{
			return null;
		}
		var value = 0;
		var digitsRead = 0;
		while (i < s.Length && AsciiBytes.IsDigit(s[i]))
		{
			value = value * 10 + (s[i] - '0');
			digitsRead++;
			finalIndex = i;
			i++;
			if (digitsRead >= limit)
			{
				break;
			}
		}
		return value;
	}

	private static int? NextMonthValue(ReadOnlySpan<byte> s, int startingIndex, ref int finalIndex)
	{
		byte c0 = 0, c1 = 0, c2 = 0;
		var charsRead = 0;
		var i = startingIndex;
		while (i < s.Length)
		{
			finalIndex = i;
			var b = s[i];
			if (!AsciiBytes.IsLetter(b))
			{
				if (charsRead == 0)
				{
					i++;
					continue;
				}
				break;
			}
			var lower = (byte)(b | 0x20);
			if (charsRead == 0)
			{
				switch (lower)
				{
					case (byte)'f': return 2;
					case (byte)'s': return 9;
					case (byte)'o': return 10;
					case (byte)'n': return 11;
					case (byte)'d': return 12;
				}
				c0 = lower;
			}
			else if (charsRead == 1)
			{
				c1 = lower;
			}
			else
			{
				c2 = lower;
			}
			charsRead++;
			if (charsRead >= 3)
			{
				break;
			}
			i++;
		}
		if (charsRead < 2)
		{
			return null;
		}
		return c0 switch
		{
			(byte)'j' when c1 == 'a' => 1,
			(byte)'j' when c1 == 'u' => charsRead >= 3 && c2 == 'n' ? 6 : 7,
			(byte)'m' => charsRead >= 3 && c2 == 'y' ? 5 : 3,
			(byte)'a' => c1 == 'u' ? 8 : 4,
			_ => 1,
		};
	}

	private static int ParsedTimeZoneOffset(ReadOnlySpan<byte> s, int startingIndex)
	{
		var i = startingIndex;
		while (i < s.Length && s[i] is (byte)' ' or (byte)':')
		{
			i++;
		}
		if (i >= s.Length)
		{
			return 0;
		}
		var first = s[i];
		if (first is (byte)'z' or (byte)'Z')
		{
			return 0;
		}
		if (first is (byte)'+' or (byte)'-')
		{
			return ParseNumericTimeZoneOffset(s, i);
		}
		return AsciiBytes.IsLetter(first) ? LookupAlphaTimeZone(s, i) : 0;
	}

	private static int ParseNumericTimeZoneOffset(ReadOnlySpan<byte> s, int startingIndex)
	{
		var isPlus = s[startingIndex] == '+';
		var i = startingIndex + 1;
		var hours = 0;
		for (var read = 0; i < s.Length && read < 2 && AsciiBytes.IsDigit(s[i]); read++, i++)
		{
			hours = hours * 10 + (s[i] - '0');
		}
		if (i < s.Length && s[i] is (byte)':' or (byte)' ')
		{
			i++;
		}
		var minutes = 0;
		for (var read = 0; i < s.Length && read < 2 && AsciiBytes.IsDigit(s[i]); read++, i++)
		{
			minutes = minutes * 10 + (s[i] - '0');
		}
		var seconds = hours * 3600 + minutes * 60;
		return isPlus ? seconds : -seconds;
	}

	private static int LookupAlphaTimeZone(ReadOnlySpan<byte> s, int startingIndex)
	{
		var name = new StringBuilder(5);
		var i = startingIndex;
		while (i < s.Length && name.Length < 5)
		{
			var b = s[i];
			if (AsciiBytes.IsLetter(b))
			{
				name.Append((char)(b | 0x20));
			}
			else if (b is not ((byte)':' or (byte)' '))
			{
				break;
			}
			i++;
		}
		return TimeZoneOffsets.GetValueOrDefault(name.ToString());
	}

	private static int Offset(int hours, int minutes = 0) => hours < 0 ? hours * 3600 - minutes * 60 : hours * 3600 + minutes * 60;

	private static readonly Dictionary<string, int> TimeZoneOffsets = new()
	{
		["gmt"] = 0, ["utc"] = 0,
		["pdt"] = Offset(-7), ["pst"] = Offset(-8), ["est"] = Offset(-5), ["edt"] = Offset(-4),
		["mdt"] = Offset(-6), ["mst"] = Offset(-7), ["cst"] = Offset(-6), ["cdt"] = Offset(-5),
		["act"] = Offset(-8), ["aft"] = Offset(4, 30), ["amt"] = Offset(4), ["art"] = Offset(-3),
		["ast"] = Offset(3), ["azt"] = Offset(4), ["bit"] = Offset(-12), ["bdt"] = Offset(8),
		["acst"] = Offset(9, 30), ["aest"] = Offset(10), ["akst"] = Offset(-9), ["amst"] = Offset(5),
		["awst"] = Offset(8), ["azost"] = Offset(-1), ["biot"] = Offset(6), ["brt"] = Offset(-3),
		["bst"] = Offset(6), ["btt"] = Offset(6), ["cat"] = Offset(2), ["cct"] = Offset(6, 30),
		["cet"] = Offset(1), ["cest"] = Offset(2), ["chast"] = Offset(12, 45), ["chst"] = Offset(10),
		["cist"] = Offset(-8), ["ckt"] = Offset(-10), ["clt"] = Offset(-4), ["clst"] = Offset(-3),
		["cot"] = Offset(-5), ["cost"] = Offset(-4), ["cvt"] = Offset(-1), ["cxt"] = Offset(7),
		["east"] = Offset(-6), ["eat"] = Offset(3), ["ect"] = Offset(-4), ["eest"] = Offset(3),
		["eet"] = Offset(2), ["fjt"] = Offset(12), ["fkst"] = Offset(-4), ["galt"] = Offset(-6),
		["get"] = Offset(4), ["gft"] = Offset(-3), ["gilt"] = Offset(7), ["git"] = Offset(-9),
		["gst"] = Offset(-2), ["gyt"] = Offset(-4), ["hast"] = Offset(-10), ["hkt"] = Offset(8),
		["hmt"] = Offset(5), ["irkt"] = Offset(8), ["irst"] = Offset(3, 30), ["ist"] = Offset(2),
		["jst"] = Offset(9), ["krat"] = Offset(7), ["kst"] = Offset(9), ["lhst"] = Offset(10, 30),
		["lint"] = Offset(14), ["magt"] = Offset(11), ["mit"] = Offset(-9, 30), ["msk"] = Offset(3),
		["mut"] = Offset(4), ["ndt"] = Offset(-2, 30), ["nft"] = Offset(11, 30), ["npt"] = Offset(5, 45),
		["nt"] = Offset(-3, 30), ["omst"] = Offset(6), ["pett"] = Offset(12), ["phot"] = Offset(13),
		["pkt"] = Offset(5), ["ret"] = Offset(4), ["samt"] = Offset(4), ["sast"] = Offset(2),
		["sbt"] = Offset(11), ["sct"] = Offset(4), ["slt"] = Offset(5, 30), ["sst"] = Offset(8),
		["taht"] = Offset(-10), ["tha"] = Offset(7), ["uyt"] = Offset(-3), ["uyst"] = Offset(-2),
		["vet"] = Offset(-4, 30), ["vlat"] = Offset(10), ["wat"] = Offset(1), ["wet"] = Offset(0),
		["west"] = Offset(1), ["yakt"] = Offset(9), ["yekt"] = Offset(5),
	};
}
