using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinNewsReader.Core;

/// <summary>
/// NetNewsWire's <c>ContentRules.json</c>: Safari content-blocker rules that keep ad and tracking scripts out of the
/// article view. Only what that file uses is understood: <c>block</c> rules with a <c>url-filter</c> pattern, matched
/// without regard to case against the whole address. The WebView2 article view asks <see cref="Blocks"/> for each request.
/// </summary>
public sealed class ContentRules
{
	private readonly Regex[] _blocked;

	public ContentRules(IEnumerable<Regex> blocked) => _blocked = [.. blocked];

	public int Count => _blocked.Length;

	public bool Blocks(string url) => _blocked.Any(rule => rule.IsMatch(url));

	/// <summary>Reads the rules in <paramref name="json"/>, skipping any it doesn't understand, such as an invalid pattern.</summary>
	public static ContentRules Parse(string json)
	{
		var blocked = new List<Regex>();
		using var document = JsonDocument.Parse(json);
		foreach (var rule in document.RootElement.EnumerateArray())
		{
			if (rule.TryGetProperty("action", out var action) && action.TryGetProperty("type", out var type) && type.GetString() == "block"
				&& rule.TryGetProperty("trigger", out var trigger) && trigger.TryGetProperty("url-filter", out var filter) && filter.GetString() is { Length: > 0 } pattern)
			{
				try
				{
					blocked.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)));
				}
				catch (ArgumentException)
				{
					// A pattern .NET can't read just means one fewer thing blocked.
				}
			}
		}
		return new ContentRules(blocked);
	}

	/// <summary>The rules in <paramref name="path"/>, or none if the file is missing or unreadable.</summary>
	public static ContentRules Load(string path)
	{
		try
		{
			return Parse(File.ReadAllText(path));
		}
		catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
		{
			return new ContentRules([]);
		}
	}
}
