using System.IO.Compression;

namespace WinNewsReader.Core.ArticleRendering;

public sealed class ArticleThemeInstallException(string message) : Exception(message);

/// <summary>
/// A theme ready to install: its <c>.nnwtheme</c> folder, unzipped to a temporary folder when it came as a zip.
/// Disposing deletes that temporary folder.
/// </summary>
public sealed class ArticleThemePackage : IDisposable
{
	private readonly string? _temporaryDirectory;

	internal ArticleThemePackage(ArticleTheme theme, string folder, string? temporaryDirectory)
	{
		Theme = theme;
		Folder = folder;
		_temporaryDirectory = temporaryDirectory;
	}

	public ArticleTheme Theme { get; }

	public string Folder { get; }

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
/// same name.
/// </summary>
public sealed class ArticleThemeInstaller(ArticleThemes themes, FeedDownloader downloader)
{
	/// <summary>NetNewsWire's limit for a downloaded theme.</summary>
	private const int MaximumThemeSize = 10_000_000;

	/// <summary>Unzipped, a theme gets more room, but not enough for a zip bomb to fill the disk.</summary>
	private const long MaximumUnzippedSize = 50_000_000;

	/// <summary>NetNewsWire's <c>ArticleThemePlist</c> requires all of these.</summary>
	private static readonly string[] RequiredInfoKeys = ["Name", "ThemeIdentifier", "CreatorHomePage", "CreatorName", "Version"];

	/// <summary>A <c>.nnwtheme</c> folder, the <c>Info.plist</c> in one, or a zip with one inside.</summary>
	public ArticleThemePackage Open(string path)
	{
		if (Directory.Exists(path))
		{
			return new ArticleThemePackage(LoadChecked(path), path, null);
		}
		if (Path.GetFileName(path).Equals("Info.plist", StringComparison.OrdinalIgnoreCase))
		{
			return Open(Path.GetDirectoryName(Path.GetFullPath(path))!);
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
			return new ArticleThemePackage(LoadChecked(folder), folder, temporaryDirectory);
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

	/// <summary>Downloads a zipped theme, as NetNewsWire does for <c>netnewswire://theme/add?url=</c>.</summary>
	public async Task<ArticleThemePackage> DownloadAsync(string url, CancellationToken cancellationToken = default)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
		{
			throw new ArticleThemeInstallException("A NetNewsWire theme can be downloaded only from an http or https URL.");
		}
		var download = await downloader.DownloadAsync(uri.AbsoluteUri, null, null, cancellationToken);
		if (download is not { Status: DownloadStatus.Ok, Data: { Length: > 0 } data })
		{
			throw new ArticleThemeInstallException("The NetNewsWire theme could not be downloaded.");
		}
		if (data.Length > MaximumThemeSize)
		{
			throw new ArticleThemeInstallException("The NetNewsWire theme is too large.");
		}
		var zip = Path.Combine(Path.GetTempPath(), "WinNewsReader-theme-" + Guid.NewGuid().ToString("N") + ".zip");
		await File.WriteAllBytesAsync(zip, data, cancellationToken);
		try
		{
			return Open(zip);
		}
		finally
		{
			File.Delete(zip);
		}
	}

	/// <summary>Whether installing would replace one of the reader's themes, which NetNewsWire asks about first.</summary>
	public bool Exists(ArticleThemePackage package) => Directory.Exists(Destination(package));

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

	private string Destination(ArticleThemePackage package) =>
		Path.Combine(themes.UserThemesDirectory, Path.GetFileName(package.Folder.TrimEnd('\\', '/')));

	private ArticleTheme LoadChecked(string folder)
	{
		if (!Path.GetFileName(folder.TrimEnd('\\', '/')).EndsWith(ArticleTheme.FolderSuffix, StringComparison.OrdinalIgnoreCase))
		{
			throw new ArticleThemeInstallException($"A NetNewsWire theme is a folder whose name ends in {ArticleTheme.FolderSuffix}.");
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

	private static void Unzip(string zipPath, string destination)
	{
		try
		{
			using var archive = ZipFile.OpenRead(zipPath);
			if (archive.Entries.Sum(entry => entry.Length) > MaximumUnzippedSize)
			{
				throw new ArticleThemeInstallException("The NetNewsWire theme is too large.");
			}
			// Throws for an entry that would land outside the destination.
			archive.ExtractToDirectory(destination);
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

	private static void CopyDirectory(string source, string destination)
	{
		Directory.CreateDirectory(destination);
		foreach (var file in Directory.EnumerateFiles(source))
		{
			File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
		}
		foreach (var directory in Directory.EnumerateDirectories(source))
		{
			CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
		}
	}
}
