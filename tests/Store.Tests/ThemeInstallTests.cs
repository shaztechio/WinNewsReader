using System.IO.Compression;
using System.Net;
using WinNewsReader.Core;
using WinNewsReader.Core.ArticleRendering;

namespace WinNewsReader.Store.Tests;

public sealed class ThemeInstallTests : IDisposable
{
	private const string ThemeUrl = "https://themes.example/Midnight.zip";

	private static readonly string ResourcesDirectory = Path.Combine(AppContext.BaseDirectory, "ArticleRendering", "Resources");

	private readonly string _directory = Path.Combine(Path.GetTempPath(), "wnr-theme-install-" + Guid.NewGuid().ToString("N"));
	private readonly Dictionary<string, byte[]> _downloads = [];
	private readonly FeedDownloader _downloader;
	private readonly ArticleThemes _themes;
	private readonly ArticleThemeInstaller _installer;

	public ThemeInstallTests()
	{
		Directory.CreateDirectory(_directory);
		_downloader = new FeedDownloader(new Handler(_downloads));
		_themes = new ArticleThemes(ResourcesDirectory, Path.Combine(_directory, "Themes"));
		_installer = new ArticleThemeInstaller(_themes, _downloader);
	}

	public void Dispose()
	{
		_downloader.Dispose();
		Directory.Delete(_directory, recursive: true);
	}

	[Fact]
	public void InstallsFromAZipAndReplacesOnReinstall()
	{
		var zip = Zip([("__MACOSX/._Midnight.nnwtheme", "junk"), .. ThemeFiles("Midnight.nnwtheme/", "body { color: navy; }")]);

		using (var package = _installer.Open(zip))
		{
			Assert.Equal("Midnight", package.Theme.Name);
			Assert.Equal("A. Themer", package.Theme.CreatorName);
			Assert.False(_installer.Exists(package));
			_installer.Install(package);
		}

		var installed = _themes.Named("Midnight");
		Assert.False(installed.IsAppTheme);
		Assert.Contains("color: navy", installed.Css);

		using (var package = _installer.Open(Zip(ThemeFiles("Midnight.nnwtheme/", "body { color: black; }"))))
		{
			Assert.True(_installer.Exists(package));
			_installer.Install(package);
		}
		Assert.Contains("color: black", _themes.Named("Midnight").Css);
		Assert.DoesNotContain("color: navy", _themes.Named("Midnight").Css);
	}

	[Fact]
	public void InstallsFromAFolderOrItsInfoPlist()
	{
		var folder = Path.Combine(_directory, "Download", "Midnight.nnwtheme");
		Directory.CreateDirectory(folder);
		foreach (var (name, text) in ThemeFiles("", "body { color: navy; }"))
		{
			File.WriteAllText(Path.Combine(folder, name), text);
		}

		using var package = _installer.Open(Path.Combine(folder, "Info.plist"));
		_installer.Install(package);

		Assert.Contains("color: navy", _themes.Named("Midnight").Css);
		Assert.True(Directory.Exists(folder), "The original stays where it was.");
	}

	[Fact]
	public void ThemeMissingAnInfoKeyIsRefused()
	{
		var zip = Zip(("Midnight.nnwtheme/Info.plist", Plist(withIdentifier: false)));
		var error = Assert.Throws<ArticleThemeInstallException>(() => _installer.Open(zip));
		Assert.Equal("This theme cannot be used because the key “ThemeIdentifier” is not found in the Info.plist.", error.Message);
	}

	[Fact]
	public void ZipWithoutAThemeIsRefused()
	{
		var error = Assert.Throws<ArticleThemeInstallException>(() => _installer.Open(Zip(("readme.txt", "hello"))));
		Assert.Equal("There is no NetNewsWire theme available.", error.Message);
	}

	[Fact]
	public void FileThatIsNotAZipIsRefused()
	{
		var path = Path.Combine(_directory, "theme.zip");
		File.WriteAllText(path, "not a zip");
		Assert.Throws<ArticleThemeInstallException>(() => _installer.Open(path));
	}

	[Fact]
	public void ZipEntryOutsideTheFolderIsRefused()
	{
		var zip = Zip([("../escaped.txt", "x"), .. ThemeFiles("Midnight.nnwtheme/", "")]);
		Assert.ThrowsAny<IOException>(() => _installer.Open(zip));
		Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "escaped.txt")));
	}

	[Fact]
	public async Task DownloadsAZippedTheme()
	{
		_downloads[ThemeUrl] = File.ReadAllBytes(Zip(ThemeFiles("Midnight.nnwtheme/", "body { color: navy; }")));

		using var package = await _installer.DownloadAsync(ThemeUrl);
		_installer.Install(package);

		Assert.Contains("color: navy", _themes.Named("Midnight").Css);
	}

	[Fact]
	public async Task DownloadFailuresAreReported()
	{
		var error = await Assert.ThrowsAsync<ArticleThemeInstallException>(() => _installer.DownloadAsync("file:///C:/Midnight.zip"));
		Assert.Equal("A NetNewsWire theme can be downloaded only from an http or https URL.", error.Message);
		error = await Assert.ThrowsAsync<ArticleThemeInstallException>(() => _installer.DownloadAsync(ThemeUrl));
		Assert.Equal("The NetNewsWire theme could not be downloaded.", error.Message);
	}

	private static (string Name, string Text)[] ThemeFiles(string prefix, string css) =>
		[(prefix + "Info.plist", Plist(withIdentifier: true)), (prefix + "stylesheet.css", css)];

	private static string Plist(bool withIdentifier) => $"""
		<?xml version="1.0" encoding="UTF-8"?>
		<plist version="1.0">
		<dict>
			<key>Name</key><string>Midnight</string>
			{(withIdentifier ? "<key>ThemeIdentifier</key><string>example.midnight</string>" : "")}
			<key>CreatorHomePage</key><string>https://themes.example/</string>
			<key>CreatorName</key><string>A. Themer</string>
			<key>Version</key><integer>2</integer>
		</dict>
		</plist>
		""";

	private string Zip(params (string Name, string Text)[] entries)
	{
		var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".zip");
		using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
		foreach (var (name, text) in entries)
		{
			using var writer = new StreamWriter(archive.CreateEntry(name).Open());
			writer.Write(text);
		}
		return path;
	}

	private sealed class Handler(Dictionary<string, byte[]> downloads) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var response = downloads.TryGetValue(request.RequestUri!.AbsoluteUri, out var data)
				? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
				: new HttpResponseMessage(HttpStatusCode.NotFound);
			response.RequestMessage = request;
			return Task.FromResult(response);
		}
	}
}
