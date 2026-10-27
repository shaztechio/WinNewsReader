using System.Text.Json;
using WinNewsReader.Parser.Utilities;

namespace WinNewsReader.Parser.Feeds.Json;

/// <summary>Typed lookups with the same leniency as Swift's <c>as?</c> casts on JSONSerialization output.</summary>
internal static class JsonValues
{
	public static JsonDocument? ParseDocument(byte[] data)
	{
		var span = data.AsMemory();
		if (span.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
		{
			span = span[3..];
		}
		try
		{
			return JsonDocument.Parse(span);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	public static JsonElement? Object(this JsonElement element, string key) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

	public static JsonElement? Array(this JsonElement element, string key) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array ? value : null;

	public static string? String(this JsonElement element, string key) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	/// <summary>An integral JSON number that fits in an Int (Swift's NSNumber-to-Int bridging is exact).</summary>
	public static long? Integer(this JsonElement element, string key) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) ? AsInteger(value) : null;

	public static long? AsInteger(JsonElement value)
	{
		if (value.ValueKind != JsonValueKind.Number)
		{
			return null;
		}
		if (value.TryGetInt64(out var l))
		{
			return l;
		}
		return value.TryGetDouble(out var d) && d == Math.Floor(d) && d is >= long.MinValue and <= long.MaxValue ? (long)d : null;
	}

	public static bool? Boolean(this JsonElement element, string key)
	{
		if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out var value))
		{
			return null;
		}
		return value.ValueKind switch
		{
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			// NSNumber 0 and 1 bridge to Bool too.
			JsonValueKind.Number when value.TryGetInt64(out var n) && n is 0 or 1 => n == 1,
			_ => null,
		};
	}

	public static IEnumerable<JsonElement> Objects(this JsonElement array) =>
		array.EnumerateArray().Where(element => element.ValueKind == JsonValueKind.Object);

	/// <summary>Swift's <c>"\(value)"</c> for a numeric id: as an Int when it's integral (5.0 bridges to 5), else a Double.</summary>
	public static string? NumberDescription(JsonElement value)
	{
		if (AsInteger(value) is { } integer)
		{
			return integer.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}
		return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) ? SwiftCompat.DoubleDescription(d) : null;
	}
}
