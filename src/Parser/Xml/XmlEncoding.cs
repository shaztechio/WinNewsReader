using System.Text;

namespace WinNewsReader.Parser.Xml;

internal static class XmlEncoding
{
	static XmlEncoding()
	{
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
	}

	/// <summary>
	/// Return UTF-8 bytes for <paramref name="input"/>, transcoding if a BOM or the XML declaration says so.
	/// Unsupported or undecodable input is returned unchanged (best effort, liberal).
	/// </summary>
	public static byte[] ToUtf8(byte[] input)
	{
		if (input.Length >= 3 && input[0] == 0xEF && input[1] == 0xBB && input[2] == 0xBF)
		{
			return input[3..];
		}
		if (input.Length >= 2 && input[0] == 0xFF && input[1] == 0xFE)
		{
			return Transcode(input.AsSpan(2), Encoding.Unicode) ?? input;
		}
		if (input.Length >= 2 && input[0] == 0xFE && input[1] == 0xFF)
		{
			return Transcode(input.AsSpan(2), Encoding.BigEndianUnicode) ?? input;
		}
		var name = DetectEncodingName(input);
		if (name is null)
		{
			return input;
		}
		switch (name)
		{
			case "utf-8" or "utf8" or "us-ascii" or "ascii":
				return input;
			case "iso-8859-1" or "latin1" or "latin-1":
				return TranscodeLatin1(input);
			case "windows-1252" or "cp1252":
				return TranscodeWindows1252(input);
		}
		if (!CodePageNames.TryGetValue(name, out var codePageName))
		{
			return input;
		}
		return Transcode(input, Encoding.GetEncoding(codePageName)) ?? input;
	}

	/// <summary>
	/// Declared names mapped to .NET encoding names. gb2312 maps to GBK (936): real-world files labelled
	/// gb2312 are usually GBK, and libxml2 treats them that way.
	/// </summary>
	private static readonly Dictionary<string, string> CodePageNames = new()
	{
		["utf-16"] = "utf-16BE",
		["utf16"] = "utf-16BE",
		["utf-16be"] = "utf-16BE",
		["utf-16le"] = "utf-16",
		["iso-8859-2"] = "iso-8859-2",
		["latin2"] = "iso-8859-2",
		["iso-8859-5"] = "iso-8859-5",
		["iso-8859-9"] = "iso-8859-9",
		["iso-8859-15"] = "iso-8859-15",
		["windows-1251"] = "windows-1251",
		["cp1251"] = "windows-1251",
		["koi8-r"] = "koi8-r",
		["shift_jis"] = "shift_jis",
		["shift-jis"] = "shift_jis",
		["sjis"] = "shift_jis",
		["euc-jp"] = "euc-jp",
		["iso-2022-jp"] = "iso-2022-jp",
		["gb2312"] = "gbk",
		["gbk"] = "gbk",
		["gb18030"] = "gb18030",
		["big5"] = "big5",
		["euc-kr"] = "euc-kr",
		["windows-1250"] = "windows-1250",
		["cp1250"] = "windows-1250",
		["windows-1253"] = "windows-1253",
		["cp1253"] = "windows-1253",
		["windows-1254"] = "windows-1254",
		["cp1254"] = "windows-1254",
	};

	/// <summary>Lowercased <c>encoding="…"</c> value from an XML declaration in the first 200 bytes, or null.</summary>
	private static string? DetectEncodingName(byte[] input)
	{
		var limit = Math.Min(input.Length, 200);
		var i = 0;
		while (i < limit && AsciiBytes.IsWhitespace(input[i]))
		{
			i++;
		}
		if (limit - i < 5 || !input.AsSpan(i, 5).SequenceEqual("<?xml"u8))
		{
			return null;
		}
		var declEnd = limit;
		for (var k = i; k + 1 < limit; k++)
		{
			if (input[k] == '?' && input[k + 1] == '>')
			{
				declEnd = k;
				break;
			}
		}
		var key = "encoding"u8;
		var encodingStart = -1;
		for (var k = i; k <= declEnd - key.Length; k++)
		{
			if (AsciiBytes.EqualsIgnoreCase(input.AsSpan(k, key.Length), key))
			{
				encodingStart = k;
				break;
			}
		}
		if (encodingStart < 0)
		{
			return null;
		}
		var j = encodingStart + key.Length;
		while (j < declEnd && AsciiBytes.IsWhitespace(input[j]))
		{
			j++;
		}
		if (j >= declEnd || input[j] != '=')
		{
			return null;
		}
		j++;
		while (j < declEnd && AsciiBytes.IsWhitespace(input[j]))
		{
			j++;
		}
		if (j >= declEnd || input[j] is not ((byte)'"' or (byte)'\''))
		{
			return null;
		}
		var quote = input[j];
		j++;
		var valueStart = j;
		while (j < declEnd && input[j] != quote)
		{
			j++;
		}
		if (j >= declEnd)
		{
			return null;
		}
		return Encoding.ASCII.GetString(input, valueStart, j - valueStart).ToLowerInvariant();
	}

	/// <summary>Strict decode then re-encode as UTF-8. Null if the bytes aren't valid in that encoding.</summary>
	private static byte[]? Transcode(ReadOnlySpan<byte> bytes, Encoding encoding)
	{
		var strict = Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
		try
		{
			return Encoding.UTF8.GetBytes(strict.GetString(bytes));
		}
		catch (DecoderFallbackException)
		{
			return null;
		}
	}

	/// <summary>ISO-8859-1: byte value equals code point.</summary>
	private static byte[] TranscodeLatin1(byte[] input)
	{
		var output = new List<byte>(input.Length);
		foreach (var b in input)
		{
			XmlEntities.AppendUtf8(output, b);
		}
		return [.. output];
	}

	/// <summary>Windows-1252: like Latin-1 except 0x80–0x9F.</summary>
	private static byte[] TranscodeWindows1252(byte[] input)
	{
		var output = new List<byte>(input.Length);
		foreach (var b in input)
		{
			XmlEntities.AppendUtf8(output, b is >= 0x80 and <= 0x9F ? XmlEntities.WindowsLatin1Extension[b - 0x80] : b);
		}
		return [.. output];
	}
}
