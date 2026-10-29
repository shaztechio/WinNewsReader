namespace WinNewsReader.Core;

/// <summary>What the New Feed dialog makes of something dragged onto the sidebar: a web address, or a feed: link.</summary>
public static class DroppedUrl
{
	/// <summary>
	/// The feed address in <paramref name="text"/>, or null if it isn't one. A <c>feed:</c> or <c>feeds:</c> link (the old
	/// way a page offers its feed to a reader) becomes the http or https address it stands for, and of several lines the
	/// first one is used.
	/// </summary>
	public static string? Feed(string? text)
	{
		if (text is null)
		{
			return null;
		}
		var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
		if (line is null)
		{
			return null;
		}
		line = line switch
		{
			_ when line.StartsWith("feed://", StringComparison.OrdinalIgnoreCase) => "http://" + line["feed://".Length..],
			_ when line.StartsWith("feeds://", StringComparison.OrdinalIgnoreCase) => "https://" + line["feeds://".Length..],
			_ when line.StartsWith("feed:", StringComparison.OrdinalIgnoreCase) => line["feed:".Length..],
			_ => line,
		};
		return Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0 ? line : null;
	}
}
