using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WinNewsReader.Core.ArticleRendering;

/// <summary>
/// NetNewsWire's <c>ArticleTheme</c>: an article template and stylesheet. A theme is a <c>.nnwtheme</c> folder with an
/// <c>Info.plist</c>, and optionally <c>stylesheet.css</c> and <c>template.html</c>; whichever it leaves out comes from
/// the default theme. Every stylesheet gets <c>core.css</c> first.
/// </summary>
public sealed class ArticleTheme
{
	public const string DefaultName = "Default";
	public const string FolderSuffix = ".nnwtheme";

	private ArticleTheme(string name, string css, string template, bool isAppTheme, string? creatorName, string? creatorHomePage, int version, string? folder)
	{
		Name = name;
		Css = css;
		Template = template;
		IsAppTheme = isAppTheme;
		CreatorName = creatorName;
		CreatorHomePage = creatorHomePage;
		Version = version;
		Folder = folder;
	}

	/// <summary>The folder name without <c>.nnwtheme</c>, as NetNewsWire names themes.</summary>
	public string Name { get; }

	public string Css { get; }

	public string Template { get; }

	/// <summary>Built in, rather than one the reader added.</summary>
	public bool IsAppTheme { get; }

	public string? CreatorName { get; }

	public string? CreatorHomePage { get; }

	public int Version { get; }

	/// <summary>The theme's folder; null for the default theme.</summary>
	public string? Folder { get; }

	/// <summary>The default theme, from the app's rendering resources.</summary>
	public static ArticleTheme LoadDefault(string resourcesDirectory) => new(
		DefaultName,
		WithWindowsFonts(CoreCss(resourcesDirectory) + "\n" + File.ReadAllText(Path.Combine(resourcesDirectory, "stylesheet.css"))),
		File.ReadAllText(Path.Combine(resourcesDirectory, "template.html")),
		isAppTheme: true, "Ranchero Software", "https://netnewswire.com/", 1, folder: null);

	/// <summary>Loads a <c>.nnwtheme</c> folder. Throws if its <c>Info.plist</c> is missing or unreadable.</summary>
	public static ArticleTheme Load(string folder, bool isAppTheme, ArticleTheme defaultTheme, string resourcesDirectory)
	{
		var info = ReadPlist(Path.Combine(folder, "Info.plist"));
		var stylesheet = ReadIfExists(Path.Combine(folder, "stylesheet.css"));
		var template = ReadIfExists(Path.Combine(folder, "template.html"));
		return new ArticleTheme(
			NameForFolder(folder),
			stylesheet is null ? defaultTheme.Css : WithWindowsFonts(CoreCss(resourcesDirectory) + "\n" + stylesheet),
			template ?? defaultTheme.Template,
			isAppTheme,
			info.GetValueOrDefault("CreatorName"),
			info.GetValueOrDefault("CreatorHomePage"),
			int.TryParse(info.GetValueOrDefault("Version"), out var version) ? version : 0,
			folder);
	}

	public static string NameForFolder(string folder)
	{
		var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		return name.EndsWith(FolderSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^FolderSuffix.Length] : name;
	}

	private const string WindowsSystemFont = "\"Segoe UI Variable Text\", \"Segoe UI\", system-ui, sans-serif";

	/// <summary>
	/// Themes name Apple's fonts: <c>-apple-system</c> (San Francisco), Menlo and <c>ui-serif</c> mean nothing to WebView2,
	/// which would fall back to Times New Roman. Each gets the Windows font that plays the same part.
	/// </summary>
	internal static string WithWindowsFonts(string css)
	{
		// The shorthand with a system font keyword has no size, so it can only become a family.
		css = Regex.Replace(css, @"\bfont:\s*-apple-system(-[a-z]+)?\b", "font-family: " + WindowsSystemFont);
		css = Regex.Replace(css, @"-apple-system(-[a-z]+)?\b", WindowsSystemFont);
		css = Regex.Replace(css, @"\bMenlo\b", "Menlo, \"Cascadia Mono\", Consolas");
		return Regex.Replace(css, @"\bui-serif\b", "Georgia, serif");
	}

	private static string CoreCss(string resourcesDirectory) => File.ReadAllText(Path.Combine(resourcesDirectory, "core.css"));

	private static string? ReadIfExists(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

	/// <summary>The top-level string and integer values of an XML property list.</summary>
	internal static Dictionary<string, string> ReadPlist(string path)
	{
		var values = new Dictionary<string, string>();
		var dict = XDocument.Load(path).Root?.Element("dict") ?? throw new InvalidDataException($"{path} has no dictionary.");
		string? key = null;
		foreach (var element in dict.Elements())
		{
			if (element.Name == "key")
			{
				key = element.Value;
			}
			else if (key is not null)
			{
				values[key] = element.Value;
				key = null;
			}
		}
		return values;
	}
}

/// <summary>
/// NetNewsWire's <c>ArticleThemesManager</c>: the default theme, the built-in themes, and themes the reader adds to the
/// themes folder. A reader's theme replaces a built-in one of the same name.
/// </summary>
public sealed class ArticleThemes
{
	private readonly string _resourcesDirectory;
	private readonly string _userThemesDirectory;

	public ArticleThemes(string resourcesDirectory, string userThemesDirectory)
	{
		_resourcesDirectory = resourcesDirectory;
		_userThemesDirectory = userThemesDirectory;
		Default = ArticleTheme.LoadDefault(resourcesDirectory);
		Reload();
	}

	/// <summary>The app's rendering resources: <c>core.css</c>, the default theme and the built-in ones.</summary>
	public string ResourcesDirectory => _resourcesDirectory;

	/// <summary>Where the reader's themes go: each a <c>.nnwtheme</c> folder.</summary>
	public string UserThemesDirectory => _userThemesDirectory;

	public ArticleTheme Default { get; }

	/// <summary>Default first, then the rest by name.</summary>
	public IReadOnlyList<ArticleTheme> All { get; private set; } = [];

	/// <summary>The page every rendering is placed in: NetNewsWire's <c>page.html</c>.</summary>
	public string PageTemplate => field ??= File.ReadAllText(Path.Combine(_resourcesDirectory, "page.html"));

	/// <summary>The scripts NetNewsWire's Mac app runs in every article page, in order.</summary>
	public IReadOnlyList<string> Scripts =>
		field ??= [.. new[] { "main.js", "main_mac.js", "newsfoot.js" }.Select(name => File.ReadAllText(Path.Combine(_resourcesDirectory, name)))];

	/// <summary>Looks the themes folders over again, for themes added or removed since.</summary>
	public void Reload()
	{
		var themes = new Dictionary<string, ArticleTheme>(StringComparer.OrdinalIgnoreCase);
		Add(Path.Combine(_resourcesDirectory, "Themes"), isAppTheme: true);
		Add(_userThemesDirectory, isAppTheme: false);
		All = [Default, .. themes.Values.Where(theme => theme.Name != ArticleTheme.DefaultName).OrderBy(theme => theme.Name, NameSorting.Comparer)];

		void Add(string directory, bool isAppTheme)
		{
			if (!Directory.Exists(directory))
			{
				return;
			}
			foreach (var folder in Directory.EnumerateDirectories(directory, "*" + ArticleTheme.FolderSuffix))
			{
				try
				{
					var theme = ArticleTheme.Load(folder, isAppTheme, Default, _resourcesDirectory);
					themes[theme.Name] = theme;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException)
				{
					// A broken theme folder is skipped rather than stopping the rest from loading.
				}
			}
		}
	}

	/// <summary>The theme named <paramref name="name"/>, or the default theme.</summary>
	public ArticleTheme Named(string? name) => All.FirstOrDefault(theme => string.Equals(theme.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Default;
}
