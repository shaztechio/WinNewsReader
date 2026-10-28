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
	private readonly Dictionary<string, string> _redirects = [];
	private readonly FeedDownloader _downloader;
	private readonly ArticleThemes _themes;
	private readonly ArticleThemeInstaller _installer;

	public ThemeInstallTests()
	{
		Directory.CreateDirectory(_directory);
		_downloader = new FeedDownloader(new Handler(_downloads, _redirects));
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
		Assert.Throws<ArticleThemeInstallException>(() => _installer.Open(zip));
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

	[Fact]
	public async Task OversizedDownloadStopsWithoutReadingItAll()
	{
		var handler = new EndlessHandler();
		using var downloader = new FeedDownloader(handler);
		var installer = new ArticleThemeInstaller(_themes, downloader);

		var error = await Assert.ThrowsAsync<ArticleThemeInstallException>(() => installer.DownloadAsync(ThemeUrl));

		Assert.Equal("The NetNewsWire theme is too large.", error.Message);
		Assert.InRange(handler.BytesSent, 10_000_000, 11_000_000);
	}

	[Fact]
	public async Task HttpsThemeRedirectedToHttpIsRefused()
	{
		_redirects[ThemeUrl] = "http://themes.example/Midnight.zip";
		_downloads["http://themes.example/Midnight.zip"] = File.ReadAllBytes(Zip(ThemeFiles("Midnight.nnwtheme/", "")));

		var error = await Assert.ThrowsAsync<ArticleThemeInstallException>(() => _installer.DownloadAsync(ThemeUrl));
		Assert.Contains("securely", error.Message);
	}

	[Fact]
	public async Task DownloadedThemeKnowsWhereItCameFrom()
	{
		_downloads[ThemeUrl] = File.ReadAllBytes(Zip(ThemeFiles("Midnight.nnwtheme/", "")));
		using var package = await _installer.DownloadAsync(ThemeUrl);
		Assert.Equal("themes.example", package.Source?.Host);
		Assert.False(package.RunsScripts);
	}

	[Fact]
	public void ZipThatUnpacksTooLargeIsRefused()
	{
		var path = Path.Combine(_directory, "bomb.zip");
		using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
		{
			foreach (var (name, text) in ThemeFiles("Midnight.nnwtheme/", ""))
			{
				using var writer = new StreamWriter(archive.CreateEntry(name).Open());
				writer.Write(text);
			}
			using var stream = archive.CreateEntry("Midnight.nnwtheme/zeros.bin", CompressionLevel.SmallestSize).Open();
			var zeros = new byte[1_000_000];
			for (var i = 0; i < 51; i++)
			{
				stream.Write(zeros);
			}
		}
		Assert.True(new FileInfo(path).Length < 1_000_000);

		var error = Assert.Throws<ArticleThemeInstallException>(() => _installer.Open(path));
		Assert.Equal("The NetNewsWire theme is too large.", error.Message);
	}

	[Fact]
	public void ZipWithTooManyFilesIsRefused()
	{
		var zip = Zip([.. ThemeFiles("Midnight.nnwtheme/", ""), .. Enumerable.Range(0, 1_000).Select(i => ($"Midnight.nnwtheme/{i}.css", ""))]);
		Assert.Equal("The NetNewsWire theme has too many files.", Assert.Throws<ArticleThemeInstallException>(() => _installer.Open(zip)).Message);
	}

	[Theory]
	[InlineData("Midnight.nnwtheme/stylesheet.css:hidden")]
	[InlineData("C:/Windows/evil.css")]
	[InlineData("Midnight.nnwtheme/../../evil.css")]
	public void ZipEntryNamesThatEscapeAreRefused(string name)
	{
		var zip = Zip([.. ThemeFiles("Midnight.nnwtheme/", ""), (name, "x")]);
		Assert.Equal("The NetNewsWire theme’s zip has a file outside its folder.", Assert.Throws<ArticleThemeInstallException>(() => _installer.Open(zip)).Message);
	}

	[Fact]
	public void ThemeNamedLikeABuiltInOneAsksBeforeReplacingIt()
	{
		using var package = _installer.Open(Zip(ThemeFiles("Sepia.nnwtheme/", "")));
		Assert.True(_installer.Exists(package));
	}

	[Fact]
	public void DefaultThemeCannotBeReplaced()
	{
		var error = Assert.Throws<ArticleThemeInstallException>(() => _installer.Open(Zip(ThemeFiles("Default.nnwtheme/", ""))));
		Assert.Equal("The Default theme can’t be replaced.", error.Message);
	}

	[Theory]
	[InlineData("<p>[[body]]</p><script>fetch('https://evil.example/')</script>", true)]
	[InlineData("<img src=x onerror=\"alert(1)\">", true)]
	[InlineData("<a href=\"javascript:alert(1)\">x</a>", true)]
	[InlineData("<article class=\"one\">[[body]]</article>", false)]
	public void ThemeWithScriptsIsFlagged(string template, bool runsScripts)
	{
		var zip = Zip([.. ThemeFiles("Midnight.nnwtheme/", ""), ("Midnight.nnwtheme/template.html", template)]);
		using var package = _installer.Open(zip);
		Assert.Equal(runsScripts, package.RunsScripts);
	}

	[Fact]
	public void InfoPlistEntitiesAreNotExpanded()
	{
		var plist = Plist(withIdentifier: true)
			.Replace("<plist version=\"1.0\">", "<!DOCTYPE plist [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">]>\n<plist version=\"1.0\">")
			.Replace("<string>A. Themer</string>", "<string>&b;</string>");
		var zip = Zip(("Midnight.nnwtheme/Info.plist", plist));
		Assert.Throws<ArticleThemeInstallException>(() => _installer.Open(zip));
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

	private sealed class Handler(Dictionary<string, byte[]> downloads, Dictionary<string, string> redirects) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var url = request.RequestUri!.AbsoluteUri;
			var response = redirects.TryGetValue(url, out var location)
				? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } }
				: downloads.TryGetValue(url, out var data)
				? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
				: new HttpResponseMessage(HttpStatusCode.NotFound);
			response.RequestMessage = request;
			return Task.FromResult(response);
		}
	}

	/// <summary>Serves zeros forever with no Content-Length, counting what the reader actually pulled.</summary>
	private sealed class EndlessHandler : HttpMessageHandler
	{
		private readonly ZeroStream _stream = new();

		public long BytesSent => _stream.Position;

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(_stream), RequestMessage = request });

		private sealed class ZeroStream : Stream
		{
			private long _position;

			public override bool CanRead => true;
			public override bool CanSeek => false;
			public override bool CanWrite => false;
			public override long Length => throw new NotSupportedException();
			public override long Position { get => _position; set => throw new NotSupportedException(); }

			public override int Read(byte[] buffer, int offset, int count)
			{
				Array.Clear(buffer, offset, count);
				_position += count;
				return count;
			}

			public override void Flush() { }
			public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
			public override void SetLength(long value) => throw new NotSupportedException();
			public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		}
	}
}
