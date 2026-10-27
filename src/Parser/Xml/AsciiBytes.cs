namespace WinNewsReader.Parser.Xml;

/// <summary>ASCII byte helpers shared by the XML scanner, entity decoder and date parser.</summary>
internal static class AsciiBytes
{
	public static bool IsWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r';

	public static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

	public static bool IsLetter(byte b) => b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';

	/// <summary>Lowercased ASCII letter. Non-letters returned unchanged.</summary>
	public static byte ToLower(byte b) => IsLetter(b) ? (byte)(b | 0x20) : b;

	/// <summary>Numeric value of a hex digit, or -1 if not a hex digit.</summary>
	public static int HexValue(byte b)
	{
		if (IsDigit(b))
		{
			return b - '0';
		}
		var lower = b | 0x20;
		if (lower is >= 'a' and <= 'f')
		{
			return lower - 'a' + 10;
		}
		return -1;
	}

	/// <summary>XML Name start characters (ASCII subset). Every feed in the wild uses ASCII names.</summary>
	public static bool IsXmlNameStart(byte b) => IsLetter(b) || b == '_' || b == ':';

	/// <summary>XML Name characters after the first. ASCII subset.</summary>
	public static bool IsXmlNameChar(byte b) => IsXmlNameStart(b) || IsDigit(b) || b == '-' || b == '.';

	/// <summary>Case-insensitive comparison against a lowercase ASCII literal.</summary>
	public static bool EqualsIgnoreCase(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> lowercaseLiteral)
	{
		if (bytes.Length != lowercaseLiteral.Length)
		{
			return false;
		}
		for (var i = 0; i < bytes.Length; i++)
		{
			if (ToLower(bytes[i]) != lowercaseLiteral[i])
			{
				return false;
			}
		}
		return true;
	}
}
