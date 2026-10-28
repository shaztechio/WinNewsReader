using System.IO.Compression;
using System.Text.RegularExpressions;

namespace WinNewsReader.Core.ArticleRendering;

public sealed class ArticleThemeInstallException(string message) : Exception(message);

/// <summary>
/// A theme ready to install: its <c>.nnwtheme</c> folder, unzipped to a temporary folder when it came as a zip.
/// Disposing deletes that temporary folder.
/// </summary>
public sealed class ArticleThemePackage : IDisposable
{
	private readonly string? _temporaryDirectory;

	internal ArticleThemePackage(ArticleTheme theme, string folder, string? temporaryDirectory, Uri? source)
	{
		Theme = theme;
		Folder = folder;
		_temporaryDirectory = temporaryDirectory;
		Source = source;
	}

	public ArticleTheme Theme { get; }

	public string Folder { get; }

	/// <summary>Where it was downloaded from, after redirects; null for a file on disk.</summary>
	public Uri? Source { get; }

	/// <summary>
	/// Whether the theme's template has scripts of its own. They would run in every article shown with the theme, with
	/// what an article's own scripts can do, so the reader is told before installing.
	/// </summary>
	public bool RunsScripts { get; init; }

	public void Dispose()
	{
		if (_temporaryDirectory is not null && Directory.Exists(_temporaryDirectory))
		{
			Directory.Delete(_temporaryDirectory, recursive: true);
		}
	}
}

/// <summary>
/// NetNewsWire's theme import (<c>ArticleThemesManager.importTheme</c>) and <c>ArticleThemeDownloader</c>: open a theme
/// from a <c>.nnwtheme</c> folder, a zip or an http(s) URL, then copy it into the themes folder, replacing one of the
/// same name. A theme comes from anywhere, so everything about it is checked before it touches the disk outside a
/// temporary folder.
/// </summary>
public sealed partial class ArticleThemeInstaller(ArticleThemes themes, FeedDownloader downloader)
{
	/// <summary>NetNewsWire's limit for a downloaded theme, enforced while downloading.</summary>
	private const int MaximumThemeSize = 10_000_000;

	/// <summary>Unzipped, a theme gets more room, but not enough for a zip bomb to fill the disk. Counted as written.</summary>
	private const long MaximumUnzippedSize = 50_000_000;

	private const int MaximumEntries = 1_000;

	/// <summary>NetNewsWire's <c>ArticleThemePlist</c> requires all of these.</summary>
	private static readonly string[] RequiredInfoKeys = ["Name", "ThemeIdentifier", "CreatorHomePage", "CreatorName", "Version"];

	/// <summary>A <c>.nnwtheme</c> folder, the <c>Info.plist</c> in one, or a zip with one inside.</summary>
	public ArticleThemePackage Open(string path) => Open(path, source: null);

	/// <summary>Downloads a zipped theme, as NetNewsWire does for <c>netnewswire://theme/add?url=</c>.</summary>
	public async Task<ArticleThemePackage> DownloadAsync(string url, CancellationToken cancellationToken = default)
	{
		if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
		{
			throw new ArticleThemeInstallException("A NetNewsWire theme can be downloaded only from an http or https URL.");
		}
		var download = await downloader.DownloadAsync(uri.AbsoluteUri, MaximumThemeSize, cancellationToken);
		if (download.IsTooLarge)
		{
			throw new ArticleThemeInstallException("The NetNewsWire theme is too large.");
		}
		if (download is not { Status: DownloadStatus.Ok, Data: { Length: > 0 } data })
		{
			throw new ArticleThemeInstallException("The NetNewsWire theme could not be downloaded.");
		}
		// Feeds may bounce through http, but a theme asked for over https mustn't arrive over plain http, where anyone
		// on the network could swap it.
		if (uri.Scheme == "https" && download.FinalUrl?.Scheme == "http")
		{
			throw new ArticleThemeInstallException("The NetNewsWire theme could not be downloaded securely: its https link redirected to http.");
		}
		var zip = Path.Combine(Path.GetTempPath(), "WinNewsReader-theme-" + Guid.NewGuid().ToString("N") + ".zip");
		await File.WriteAllBytesAsync(zip, data, cancellationToken);
		try
		{
			return Open(zip, download.FinalUrl ?? uri);
		}
		finally
		{
			File.Delete(zip);
		}
	}

	/// <summary>
	/// Whether installing would replace a theme of the same name, which NetNewsWire asks about first. Built-in themes
	/// count too: one of the reader's themes takes the place of a built-in one of the same name.
	/// </summary>
	public bool Exists(ArticleThemePackage package) =>
		Directory.Exists(Destination(package)) || themes.All.Any(theme => theme.Name.Equals(package.Theme.Name, StringComparison.OrdinalIgnoreCase));

	/// <summary>Copies the theme into the themes folder and reloads the theme list.</summary>
	public void Install(ArticleThemePackage package)
	{
		var destination = Destination(package);
		if (Path.GetFullPath(destination).TrimEnd('\\', '/') == Path.GetFullPath(package.Folder).TrimEnd('\\', '/'))
		{
			return;
		}
		if (Directory.Exists(destination))
		{
			Directory.Delete(destination, recursive: true);
		}
		CopyDirectory(package.Folder, destination);
		themes.Reload();
	}

	internal static bool HasScripts(string html) => ScriptPattern().IsMatch(html);

	/// <summary>A script element, an inline event handler or a <c>javascript:</c> URL.</summary>
	[GeneratedRegex(@"<script\b|\son[a-z]+\s*=|javascript\s*:", RegexOptions.IgnoreCase)]
	private static partial Regex ScriptPattern();

	private ArticleThemePackage Open(string path, Uri? source)
	{
		if (Directory.Exists(path))
		{
			return Package(path, null, source);
		}
		if (Path.GetFileName(path).Equals("Info.plist", StringComparison.OrdinalIgnoreCase))
		{
			return Open(Path.GetDirectoryName(Path.GetFullPath(path))!, source);
		}
		if (!File.Exists(path))
		{
			throw new ArticleThemeInstallException($"Couldn’t find {path}.");
		}
		var temporaryDirectory = Path.Combine(Path.GetTempPath(), "WinNewsReader-theme-" + Guid.NewGuid().ToString("N"));
		try
		{
			Unzip(path, temporaryDirectory);
			var folder = FindThemeFolder(temporaryDirectory) ?? throw new ArticleThemeInstallException("There is no NetNewsWire theme available.");
			return Package(folder, temporaryDirectory, source);
		}
		catch
		{
			if (Directory.Exists(temporaryDirectory))
			{
				Directory.Delete(temporaryDirectory, recursive: true);
			}
			throw;
		}
	}

	private ArticleThemePackage Package(string folder, string? temporaryDirectory, Uri? source)
	{
		var theme = LoadChecked(folder);
		// Only the theme's own template counts; one without a template gets the default one.
		var template = Path.Combine(folder, "template.html");
		return new(theme, folder, temporaryDirectory, source) { RunsScripts = File.Exists(template) && HasScripts(File.ReadAllText(template)) };
	}

	private string Destination(ArticleThemePackage package) =>
		Path.Combine(themes.UserThemesDirectory, Path.GetFileName(package.Folder.TrimEnd('\\', '/')));

	private ArticleTheme LoadChecked(string folder)
	{
		var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
		if (!name.EndsWith(ArticleTheme.FolderSuffix, StringComparison.OrdinalIgnoreCase) || name.Length == ArticleTheme.FolderSuffix.Length)
		{
			throw new ArticleThemeInstallException($"A NetNewsWire theme is a folder whose name ends in {ArticleTheme.FolderSuffix}.");
		}
		if (ArticleTheme.NameForFolder(folder).Equals(ArticleTheme.DefaultName, StringComparison.OrdinalIgnoreCase))
		{
			throw new ArticleThemeInstallException("The Default theme can’t be replaced.");
		}
		var infoPath = Path.Combine(folder, "Info.plist");
		if (!File.Exists(infoPath))
		{
			throw new ArticleThemeInstallException("This theme cannot be used because it has no Info.plist.");
		}
		Dictionary<string, string> info;
		try
		{
			info = ArticleTheme.ReadPlist(infoPath);
		}
		catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException)
		{
			throw new ArticleThemeInstallException($"This theme cannot be used because of data corruption in the Info.plist: {ex.Message}");
		}
		if (RequiredInfoKeys.FirstOrDefault(key => !info.ContainsKey(key)) is { } missing)
		{
			throw new ArticleThemeInstallException($"This theme cannot be used because the key “{missing}” is not found in the Info.plist.");
		}
		if (!int.TryParse(info["Version"], out _))
		{
			throw new ArticleThemeInstallException("This theme cannot be used because the type “Int” is mismatched in the Info.plist.");
		}
		return ArticleTheme.Load(folder, isAppTheme: false, themes.Default, themes.ResourcesDirectory);
	}

	/// <summary>
	/// Extracts entry by entry rather than trusting the zip: sizes are counted as written, not as the zip declares them,
	/// and every path must stay inside <paramref name="destination"/>.
	/// </summary>
	private static void Unzip(string zipPath, string destination)
	{
		var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
		try
		{
			using var archive = ZipFile.OpenRead(zipPath);
			if (archive.Entries.Count > MaximumEntries)
			{
				throw new ArticleThemeInstallException("The NetNewsWire theme has too many files.");
			}
			long written = 0;
			var buffer = new byte[81920];
			foreach (var entry in archive.Entries)
			{
				// A colon would name an NTFS alternate data stream; a rooted name ignores the destination.
				if (entry.FullName.Contains(':') || Path.IsPathRooted(entry.FullName))
				{
					throw new ArticleThemeInstallException("The NetNewsWire theme’s zip has a file outside its folder.");
				}
				var path = Path.GetFullPath(Path.Combine(root, entry.FullName));
				if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
				{
					throw new ArticleThemeInstallException("The NetNewsWire theme’s zip has a file outside its folder.");
				}
				if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
				{
					Directory.CreateDirectory(path);
					continue;
				}
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				using var input = entry.Open();
				using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
				int read;
				while ((read = input.Read(buffer)) > 0)
				{
					written += read;
					if (written > MaximumUnzippedSize)
					{
						throw new ArticleThemeInstallException("The NetNewsWire theme is too large.");
					}
					output.Write(buffer, 0, read);
				}
			}
		}
		catch (InvalidDataException)
		{
			throw new ArticleThemeInstallException("That isn’t a NetNewsWire theme or a zip with one in it.");
		}
	}

	/// <summary>The first <c>.nnwtheme</c> folder, skipping the <c>__MACOSX</c> folder macOS adds to zips.</summary>
	private static string? FindThemeFolder(string directory) =>
		Directory.EnumerateDirectories(directory, "*" + ArticleTheme.FolderSuffix, SearchOption.AllDirectories)
			.Where(folder => !Path.GetRelativePath(directory, folder).StartsWith("__MACOSX", StringComparison.Ordinal))
			.OrderBy(folder => folder.Length)
			.FirstOrDefault();

	/// <summary>Skips links and junctions, which could point anywhere on the disk or loop back on themselves.</summary>
	private static void CopyDirectory(string source, string destination)
	{
		Directory.CreateDirectory(destination);
		foreach (var file in new DirectoryInfo(source).EnumerateFiles())
		{
			if (!file.Attributes.HasFlag(FileAttributes.ReparsePoint))
			{
				file.CopyTo(Path.Combine(destination, file.Name));
			}
		}
		foreach (var directory in new DirectoryInfo(source).EnumerateDirectories())
		{
			if (!directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
			{
				CopyDirectory(directory.FullName, Path.Combine(destination, directory.Name));
			}
		}
	}
}
