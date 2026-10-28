using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinNewsReader.Core;

/// <summary>Where the main window was and how big its panes were, so the next launch looks the same.</summary>
public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool IsMaximized, double SidebarWidth, double TimelineWidth);

/// <summary>
/// View settings and window state, kept as JSON next to the account's data. NetNewsWire keeps the same things in
/// user defaults and its window state restoration.
/// </summary>
public sealed class AppSettings
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

	/// <summary>Sidebar rows whose timelines hide read articles. NetNewsWire keeps this choice per row.</summary>
	public HashSet<string> HideReadArticlesKeys { get; set; } = [];

	public bool HideReadFeeds { get; set; }

	/// <summary>NetNewsWire's "Sort Articles By" setting. Newest first by default.</summary>
	public bool SortOldestFirst { get; set; }

	/// <summary>The article theme's name; null means NetNewsWire's Default.</summary>
	public string? ArticleThemeName { get; set; }

	/// <summary>NetNewsWire's "Enable JavaScript" preference for article content. On by default.</summary>
	public bool ArticleContentJavaScriptEnabled { get; set; } = true;

	/// <summary>Feeds with NetNewsWire's "Always Use Reader View" on, as <c>accountID|feedID</c>.</summary>
	public HashSet<string> ReaderViewFeedKeys { get; set; } = [];

	/// <summary>NetNewsWire's article text size, Large by default.</summary>
	public ArticleRendering.ArticleTextSize ArticleTextSize { get; set; } = ArticleRendering.ArticleTextSize.Large;

	public WindowPlacement? Window { get; set; }

	public List<string>? ExpandedSidebarKeys { get; set; }

	public string? SelectedSidebarKey { get; set; }

	public string? SelectedArticleId { get; set; }

	/// <summary>Set once the default feeds have been offered, so deleting them all doesn't bring them back.</summary>
	public bool AddedDefaultFeeds { get; set; }

	/// <summary>The settings in <paramref name="path"/>, or defaults when the file is missing or unreadable.</summary>
	public static AppSettings Load(string path)
	{
		try
		{
			return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new() : new();
		}
		catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
		{
			return new();
		}
	}

	/// <summary>Writes to a temporary file first, so a crash mid-write can't leave half a file.</summary>
	public void Save(string path)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		var temporary = path + ".tmp";
		File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
		File.Move(temporary, path, overwrite: true);
	}
}
