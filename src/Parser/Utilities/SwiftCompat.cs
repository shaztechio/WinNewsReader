using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WinNewsReader.Parser.Utilities;

/// <summary>
/// Formatting that must match NetNewsWire exactly, because calculated article IDs are hashes of these strings.
/// Changing any of it would make every existing article look new.
/// </summary>
public static class SwiftCompat
{
	/// <summary>Lowercase hex MD5 of the UTF-8 bytes (RSCore's <c>md5String</c>).</summary>
	public static string Md5String(string s) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));

	/// <summary>Seconds since 1970 as a double, as Foundation's <c>timeIntervalSince1970</c>.</summary>
	public static double TimeIntervalSince1970(DateTimeOffset date) => date.ToUnixTimeMilliseconds() / 1000.0;

	/// <summary><c>String(format: "%.0f", value)</c>. printf rounds exact ties to even; .NET's F0 rounds them away from zero.</summary>
	public static string FormatNoDecimals(double value) =>
		Math.Round(value, MidpointRounding.ToEven).ToString("F0", CultureInfo.InvariantCulture);

	/// <summary>Swift's <c>"\(value)"</c> for a Double: shortest round-trip digits, always with a decimal point.</summary>
	public static string DoubleDescription(double value)
	{
		if (double.IsNaN(value))
		{
			return "nan";
		}
		if (double.IsInfinity(value))
		{
			return value > 0 ? "inf" : "-inf";
		}
		// Dates fall well inside the range where both runtimes print plain decimals. Exponent forms
		// (1e+16, 1.5e-05) only differ in letter case.
		var s = value.ToString("R", CultureInfo.InvariantCulture);
		if (s.Contains('E'))
		{
			return s.ToLowerInvariant();
		}
		return s.Contains('.') ? s : s + ".0";
	}
}
