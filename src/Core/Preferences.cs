using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinNewsReader.Core;

/// <summary>Light, dark, or whatever Windows is set to. NetNewsWire only follows the system.</summary>
public enum AppAppearance
{
	System,
	Light,
	Dark,
}

/// <summary>NetNewsWire's "Unread Counts" preference: the number, a dot, or nothing.</summary>
public enum UnreadCountDisplay
{
	Show,
	Dot,
	Hide,
}

/// <summary>NetNewsWire's <c>RefreshInterval</c>: manual, or every 30 minutes, 1, 2, 4 or 8 hours.</summary>
public static class RefreshIntervals
{
	/// <summary>NetNewsWire's default.</summary>
	public const int DefaultMinutes = 60;

	/// <summary>In menu order. Zero means manually.</summary>
	public static IReadOnlyList<(int Minutes, string Name)> All { get; } =
	[
		(0, "Manually"),
		(30, "Every 30 minutes"),
		(60, "Every hour"),
		(120, "Every 2 hours"),
		(240, "Every 4 hours"),
		(480, "Every 8 hours"),
	];

	/// <summary>A stored value that isn't one of the choices, such as one from a hand-edited file, becomes the default.</summary>
	public static int Normalized(int minutes) => All.Any(interval => interval.Minutes == minutes) ? minutes : DefaultMinutes;

	/// <summary>How often to refresh, or null when only the reader refreshes.</summary>
	public static TimeSpan? ToTimeSpan(int minutes) => Normalized(minutes) is > 0 and var normalized ? TimeSpan.FromMinutes(normalized) : null;
}

/// <summary>A web browser the reader can pick to open links with.</summary>
public sealed record InstalledBrowser(string Name, string Path);

/// <summary>
/// NetNewsWire's browser picker (PR-05): the browsers Windows lists under <c>Clients\StartMenuInternet</c>, which
/// every browser registers itself in.
/// </summary>
public static partial class InstalledBrowsers
{
	private static readonly string[] ClientsKeys = [@"SOFTWARE\Clients\StartMenuInternet", @"SOFTWARE\WOW6432Node\Clients\StartMenuInternet"];

	/// <summary>Installed browsers by name, each once, whose programs exist.</summary>
	public static IReadOnlyList<InstalledBrowser> Find()
	{
		if (!OperatingSystem.IsWindows())
		{
			return [];
		}
		var browsers = new Dictionary<string, InstalledBrowser>(StringComparer.OrdinalIgnoreCase);
		foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
		{
			foreach (var clients in ClientsKeys)
			{
				using var key = hive.OpenSubKey(clients);
				foreach (var name in key?.GetSubKeyNames() ?? [])
				{
					using var browser = key!.OpenSubKey(name);
					using var command = browser?.OpenSubKey(@"shell\open\command");
					// Internet Explorer is retired, and its program only hands the address to Edge.
					if (ExecutableFrom(command?.GetValue(null) as string) is { } path && File.Exists(path)
						&& !Path.GetFileName(path).Equals("iexplore.exe", StringComparison.OrdinalIgnoreCase))
					{
						browsers.TryAdd(path, new InstalledBrowser(browser?.GetValue(null) as string is { Length: > 0 } display ? display : name, path));
					}
				}
			}
		}
		return [.. browsers.Values.OrderBy(browser => browser.Name, StringComparer.CurrentCultureIgnoreCase)];
	}

	/// <summary>The program in a registered command line: <c>"C:\Program Files\App\app.exe" --flag</c> gives the path.</summary>
	public static string? ExecutableFrom(string? command) =>
		command is null ? null : Program().Match(command.Trim()) is { Success: true } match ? Environment.ExpandEnvironmentVariables(match.Groups["path"].Value) : null;

	[GeneratedRegex("""^(?:"(?<path>[^"]+\.exe)"|(?<path>.+?\.exe))(?:\s|$)""", RegexOptions.IgnoreCase)]
	private static partial Regex Program();
}

/// <summary>What the taskbar badge says, for NetNewsWire's "Unread Counts" preference.</summary>
public static class UnreadBadge
{
	/// <summary>The badge's text for a dot, which the badge draws as a dot rather than as text.</summary>
	public const string DotText = "●";

	/// <summary>
	/// The number for Show, past 99 as "99+" since the badge is tiny, a dot for Show dot, and null, meaning no badge,
	/// for Hide or when nothing is unread.
	/// </summary>
	public static string? Text(int unread, UnreadCountDisplay display) => unread <= 0 ? null : display switch
	{
		UnreadCountDisplay.Show => unread > 99 ? "99+" : unread.ToString(System.Globalization.CultureInfo.InvariantCulture),
		UnreadCountDisplay.Dot => DotText,
		_ => null,
	};

	/// <summary>What a screen reader says for the badge.</summary>
	public static string Description(int unread, UnreadCountDisplay display) =>
		unread <= 0 || display == UnreadCountDisplay.Hide ? "" : unread == 1 ? "1 unread article" : $"{unread:N0} unread articles";
}
