using System.Text;

namespace WinNewsReader.Parser.Xml;

internal static class XmlEntities
{
	public enum Mode
	{
		/// <summary>Expand predefined XML entities, numeric entities and HTML named entities (<c>&amp;mdash;</c>, <c>&amp;nbsp;</c>).</summary>
		Html,

		/// <summary>libxml2-compatible without a DTD: predefined XML and numeric entities only. Unknown names pass through.</summary>
		XmlStrict,
	}

	/// <summary>
	/// Decode the entity starting at the <c>&amp;</c> at <paramref name="at"/>. If it isn't a valid entity,
	/// returns a single <c>&amp;</c> and advances past it, so the caller appends it literally.
	/// </summary>
	public static (byte[] Bytes, int NextIndex) Decode(byte[] bytes, int at, Mode mode)
	{
		var searchEnd = Math.Min(bytes.Length, at + 1 + MaxEntityLength);
		var semicolonIndex = -1;
		for (var i = at + 1; i < searchEnd; i++)
		{
			var b = bytes[i];
			if (b == ';')
			{
				semicolonIndex = i;
				break;
			}
			// Whitespace or another `&` means we've left the entity.
			if (AsciiBytes.IsWhitespace(b) || b == '&' || b == '<')
			{
				break;
			}
		}
		if (semicolonIndex < 0)
		{
			return LiteralAmpersand(at);
		}
		var nameStart = at + 1;
		if (nameStart == semicolonIndex)
		{
			return LiteralAmpersand(at);
		}
		if (bytes[nameStart] == '#')
		{
			var numeric = DecodeNumeric(bytes, nameStart + 1, semicolonIndex);
			return numeric is null ? LiteralAmpersand(at) : (numeric, semicolonIndex + 1);
		}
		var name = bytes.AsSpan(nameStart, semicolonIndex - nameStart);
		if (XmlPredefinedEntity(name) is { } predefined)
		{
			return (predefined, semicolonIndex + 1);
		}
		// HTML named entities only in HTML mode (libxml2's XML parser without a DTD leaves them literal).
		if (mode == Mode.Html && HtmlNamedEntities.TryGetValue(Encoding.UTF8.GetString(name), out var html))
		{
			return (html, semicolonIndex + 1);
		}
		return LiteralAmpersand(at);
	}

	/// <summary>Windows-1252 code points for bytes 0x80–0x9F. From WebKit's HTMLEntityParser.</summary>
	public static readonly int[] WindowsLatin1Extension =
	[
		0x20AC, 0x0081, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021,
		0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0x008D, 0x017D, 0x008F,
		0x0090, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014,
		0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0x009D, 0x017E, 0x0178,
	];

	public static void AppendUtf8(List<byte> output, int codepoint)
	{
		if (codepoint < 0x80)
		{
			output.Add((byte)codepoint);
		}
		else if (codepoint < 0x800)
		{
			output.Add((byte)(0xC0 | (codepoint >> 6)));
			output.Add((byte)(0x80 | (codepoint & 0x3F)));
		}
		else if (codepoint < 0x10000)
		{
			output.Add((byte)(0xE0 | (codepoint >> 12)));
			output.Add((byte)(0x80 | ((codepoint >> 6) & 0x3F)));
			output.Add((byte)(0x80 | (codepoint & 0x3F)));
		}
		else
		{
			output.Add((byte)(0xF0 | (codepoint >> 18)));
			output.Add((byte)(0x80 | ((codepoint >> 12) & 0x3F)));
			output.Add((byte)(0x80 | ((codepoint >> 6) & 0x3F)));
			output.Add((byte)(0x80 | (codepoint & 0x3F)));
		}
	}

	private static readonly byte[] AmpersandBytes = [(byte)'&'];
	private static readonly byte[] LessThanBytes = [(byte)'<'];
	private static readonly byte[] GreaterThanBytes = [(byte)'>'];
	private static readonly byte[] DoubleQuoteBytes = [(byte)'"'];
	private static readonly byte[] SingleQuoteBytes = [(byte)'\''];

	private static (byte[] Bytes, int NextIndex) LiteralAmpersand(int at) => (AmpersandBytes, at + 1);

	/// <summary>Decode <c>&amp;#NNN;</c> or <c>&amp;#xHH;</c>. <paramref name="start"/> is just past the <c>#</c>.</summary>
	private static byte[]? DecodeNumeric(byte[] bytes, int start, int end)
	{
		if (start >= end)
		{
			return null;
		}
		long codepoint = 0;
		if (bytes[start] is (byte)'x' or (byte)'X')
		{
			for (var i = start + 1; i < end; i++)
			{
				var v = AsciiBytes.HexValue(bytes[i]);
				if (v < 0)
				{
					return null;
				}
				codepoint = codepoint * 16 + v;
				if (codepoint > 0x10FFFF)
				{
					return null;
				}
			}
		}
		else
		{
			for (var i = start; i < end; i++)
			{
				if (!AsciiBytes.IsDigit(bytes[i]))
				{
					return null;
				}
				codepoint = codepoint * 10 + (bytes[i] - '0');
				if (codepoint > 0x10FFFF)
				{
					return null;
				}
			}
		}
		// Authors typed `&#128;` meaning the Windows-1252 €, not the C1 control U+0080. Remap 0x80–0x9F.
		if ((codepoint & ~0x1F) == 0x80)
		{
			codepoint = WindowsLatin1Extension[codepoint - 0x80];
		}
		if (codepoint == 0 || codepoint is >= 0xD800 and <= 0xDFFF)
		{
			return null;
		}
		var output = new List<byte>(4);
		AppendUtf8(output, (int)codepoint);
		return [.. output];
	}

	private static byte[]? XmlPredefinedEntity(ReadOnlySpan<byte> name)
	{
		if (name.SequenceEqual("amp"u8))
		{
			return AmpersandBytes;
		}
		if (name.SequenceEqual("lt"u8))
		{
			return LessThanBytes;
		}
		if (name.SequenceEqual("gt"u8))
		{
			return GreaterThanBytes;
		}
		if (name.SequenceEqual("quot"u8))
		{
			return DoubleQuoteBytes;
		}
		if (name.SequenceEqual("apos"u8))
		{
			return SingleQuoteBytes;
		}
		return null;
	}

	// HTML named entities commonly seen in feeds. Same table as NetNewsWire's (from NSString+RSParser.m),
	// including its quirks (Yacute → "Y"), so decoded text matches.
	private static readonly Dictionary<string, string> HtmlNamedEntityStrings = new()
	{
		["AElig"] = "Æ", ["Aacute"] = "Á", ["Acirc"] = "Â", ["Agrave"] = "À", ["Aring"] = "Å",
		["Atilde"] = "Ã", ["Auml"] = "Ä", ["Ccedil"] = "Ç", ["Dstrok"] = "Ð", ["ETH"] = "Ð",
		["Eacute"] = "É", ["Ecirc"] = "Ê", ["Egrave"] = "È", ["Euml"] = "Ë", ["Iacute"] = "Í",
		["Icirc"] = "Î", ["Igrave"] = "Ì", ["Iuml"] = "Ï", ["Ntilde"] = "Ñ", ["Oacute"] = "Ó",
		["Ocirc"] = "Ô", ["Ograve"] = "Ò", ["Oslash"] = "Ø", ["Otilde"] = "Õ", ["Ouml"] = "Ö",
		["Pi"] = "Π", ["THORN"] = "Þ", ["Uacute"] = "Ú", ["Ucirc"] = "Û", ["Ugrave"] = "Ù",
		["Uuml"] = "Ü", ["Yacute"] = "Y", ["aacute"] = "á", ["acirc"] = "â", ["acute"] = "´",
		["aelig"] = "æ", ["agrave"] = "à", ["aring"] = "å", ["atilde"] = "ã", ["auml"] = "ä",
		["brkbar"] = "¦", ["brvbar"] = "¦", ["ccedil"] = "ç", ["cedil"] = "¸", ["cent"] = "¢",
		["copy"] = "©", ["CounterClockwiseContourIntegral"] = "∳", ["curren"] = "¤", ["deg"] = "°",
		["die"] = "¨", ["divide"] = "÷", ["eacute"] = "é", ["ecirc"] = "ê", ["egrave"] = "è",
		["eth"] = "ð", ["euml"] = "ë", ["euro"] = "€", ["frac12"] = "½", ["frac14"] = "¼",
		["frac34"] = "¾", ["hearts"] = "♥", ["hellip"] = "…", ["iacute"] = "í", ["icirc"] = "î",
		["iexcl"] = "¡", ["igrave"] = "ì", ["iquest"] = "¿", ["iuml"] = "ï", ["laquo"] = "«",
		["ldquo"] = "“", ["lsquo"] = "‘", ["macr"] = "¯", ["mdash"] = "—", ["micro"] = "µ",
		["middot"] = "·", ["ndash"] = "–", ["not"] = "¬", ["ntilde"] = "ñ", ["oacute"] = "ó",
		["ocirc"] = "ô", ["ograve"] = "ò", ["ordf"] = "ª", ["ordm"] = "º", ["oslash"] = "ø",
		["otilde"] = "õ", ["ouml"] = "ö", ["para"] = "¶", ["pi"] = "π", ["plusmn"] = "±",
		["pound"] = "£", ["raquo"] = "»", ["rdquo"] = "”", ["reg"] = "®", ["rsquo"] = "’",
		["sect"] = "§", ["smallcircle"] = "◦", ["shy"] = "­", ["sup1"] = "¹", ["sup2"] = "²",
		["sup3"] = "³", ["szlig"] = "ß", ["thorn"] = "þ", ["times"] = "×", ["trade"] = "™",
		["uacute"] = "ú", ["ucirc"] = "û", ["ugrave"] = "ù", ["uml"] = "¨", ["uuml"] = "ü",
		["yacute"] = "y", ["yen"] = "¥", ["yuml"] = "ÿ", ["infin"] = "∞", ["nbsp"] = " ",
		["zwj"] = "‍", ["zwnj"] = "‌",
	};

	private static readonly Dictionary<string, byte[]> HtmlNamedEntities =
		HtmlNamedEntityStrings.ToDictionary(pair => pair.Key, pair => Encoding.UTF8.GetBytes(pair.Value));

	/// <summary>Longest recognized entity name + 1 for the <c>;</c>, to bound the scan on malformed input.</summary>
	private static readonly int MaxEntityLength = HtmlNamedEntityStrings.Keys.Max(key => key.Length) + 1;
}

public static class HtmlEntityExtensions
{
	/// <summary>Decode all HTML entities (named, decimal, hex). Unknown entities pass through.</summary>
	public static string DecodingHtmlEntities(this string s)
	{
		if (!s.Contains('&'))
		{
			return s;
		}
		var bytes = Encoding.UTF8.GetBytes(s);
		var output = new List<byte>(bytes.Length);
		var i = 0;
		while (i < bytes.Length)
		{
			if (bytes[i] == '&')
			{
				var (decoded, next) = XmlEntities.Decode(bytes, i, XmlEntities.Mode.Html);
				output.AddRange(decoded);
				i = next;
			}
			else
			{
				output.Add(bytes[i]);
				i++;
			}
		}
		return Encoding.UTF8.GetString([.. output]);
	}
}
