using System.Text;

namespace WinNewsReader.Core;

/// <summary>
/// Crash reports that stay on this PC. When the app dies from an unhandled exception it writes the exception and the
/// version to a text file; the next start offers to show it, or to open a bug report the reader can paste it into.
/// Nothing is sent anywhere by the app itself.
/// </summary>
public static class CrashLog
{
	private const string Prefix = "crash-";

	/// <summary>Writes a report for <paramref name="exception"/> into <paramref name="directory"/> and returns its path.</summary>
	public static string Write(string directory, Exception exception, string version, DateTimeOffset when)
	{
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, $"{Prefix}{when:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.txt");
		var text = new StringBuilder();
		text.AppendLine($"WinNewsReader {version}");
		text.AppendLine($"Time: {when:u}");
		text.AppendLine($"Windows: {Environment.OSVersion.VersionString}");
		text.AppendLine($".NET: {Environment.Version}");
		text.AppendLine();
		text.AppendLine(exception.ToString());
		File.WriteAllText(path, text.ToString());
		return path;
	}

	/// <summary>Reports not yet looked at, oldest first.</summary>
	public static IReadOnlyList<string> Pending(string directory) =>
		Directory.Exists(directory) ? [.. Directory.EnumerateFiles(directory, Prefix + "*.txt").Order(StringComparer.Ordinal)] : [];

	/// <summary>Moves a report out of the pending list, into a folder of reports already seen.</summary>
	public static void Dismiss(string path)
	{
		var seen = Path.Combine(Path.GetDirectoryName(path)!, "seen");
		Directory.CreateDirectory(seen);
		File.Move(path, Path.Combine(seen, Path.GetFileName(path)), overwrite: true);
	}
}
