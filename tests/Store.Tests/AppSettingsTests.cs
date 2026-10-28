using WinNewsReader.Core;

namespace WinNewsReader.Store.Tests;

public sealed class AppSettingsTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "wnr-tests-" + Guid.NewGuid().ToString("N"));

	private string SettingsPath => Path.Combine(_directory, "settings.json");

	public void Dispose()
	{
		if (Directory.Exists(_directory))
		{
			Directory.Delete(_directory, recursive: true);
		}
	}

	[Fact]
	public void RoundTrips()
	{
		new AppSettings
		{
			HideReadArticlesKeys = ["feed::https://a.example/feed"],
			HideReadFeeds = true,
			SortOldestFirst = true,
			Window = new WindowPlacement(10, 20, 1200, 800, true, 240, 360),
			ExpandedSidebarKeys = ["folder:1"],
			SelectedSidebarKey = "AllUnread",
			SelectedArticleId = "abc",
			AddedDefaultFeeds = true,
		}.Save(SettingsPath);

		var loaded = AppSettings.Load(SettingsPath);

		Assert.Equal(["feed::https://a.example/feed"], loaded.HideReadArticlesKeys);
		Assert.True(loaded.HideReadFeeds);
		Assert.True(loaded.SortOldestFirst);
		Assert.Equal(new WindowPlacement(10, 20, 1200, 800, true, 240, 360), loaded.Window);
		Assert.Equal(["folder:1"], loaded.ExpandedSidebarKeys);
		Assert.Equal("AllUnread", loaded.SelectedSidebarKey);
		Assert.Equal("abc", loaded.SelectedArticleId);
		Assert.True(loaded.AddedDefaultFeeds);
	}

	[Fact]
	public void PreferencesRoundTripAndDefaultToNetNewsWires()
	{
		var defaults = new AppSettings();
		Assert.Equal(AppAppearance.System, defaults.Appearance);
		Assert.Equal(60, defaults.RefreshIntervalMinutes);
		Assert.Equal(UnreadCountDisplay.Show, defaults.UnreadCounts);
		Assert.Null(defaults.BrowserPath);
		Assert.Empty(defaults.DisabledAccountIds);

		new AppSettings
		{
			Appearance = AppAppearance.Dark,
			RefreshIntervalMinutes = 0,
			UnreadCounts = UnreadCountDisplay.Dot,
			BrowserPath = @"C:\Browsers\browser.exe",
			DisabledAccountIds = ["OnThisPC"],
		}.Save(SettingsPath);

		Assert.Contains("\"Dark\"", File.ReadAllText(SettingsPath));
		var loaded = AppSettings.Load(SettingsPath);
		Assert.Equal(AppAppearance.Dark, loaded.Appearance);
		Assert.Equal(0, loaded.RefreshIntervalMinutes);
		Assert.Equal(UnreadCountDisplay.Dot, loaded.UnreadCounts);
		Assert.Equal(@"C:\Browsers\browser.exe", loaded.BrowserPath);
		Assert.Equal(["OnThisPC"], loaded.DisabledAccountIds);
	}

	[Fact]
	public void ToolbarAndHiddenPreferencesDefaultOffAndRoundTrip()
	{
		var defaults = new AppSettings();
		Assert.False(defaults.ToolbarHidden);
		Assert.False(defaults.SuppressRefreshOnLaunch);
		Assert.False(defaults.ArticleTitleInWindowTitle);
		Assert.False(defaults.DoubleClickFeedMarksRead);

		new AppSettings { ToolbarHidden = true, SuppressRefreshOnLaunch = true, ArticleTitleInWindowTitle = true, DoubleClickFeedMarksRead = true }.Save(SettingsPath);
		var loaded = AppSettings.Load(SettingsPath);

		Assert.True(loaded.ToolbarHidden);
		Assert.True(loaded.SuppressRefreshOnLaunch);
		Assert.True(loaded.ArticleTitleInWindowTitle);
		Assert.True(loaded.DoubleClickFeedMarksRead);
	}

	[Fact]
	public void MissingFileGivesDefaults()
	{
		var settings = AppSettings.Load(SettingsPath);
		Assert.False(settings.HideReadFeeds);
		Assert.False(settings.SortOldestFirst);
		Assert.Null(settings.Window);
		Assert.Empty(settings.HideReadArticlesKeys);
	}

	[Fact]
	public void DamagedFileGivesDefaults()
	{
		Directory.CreateDirectory(_directory);
		File.WriteAllText(SettingsPath, "{ not json");
		Assert.Null(AppSettings.Load(SettingsPath).Window);
	}

	[Fact]
	public void SaveReplacesExistingFile()
	{
		new AppSettings { HideReadFeeds = true }.Save(SettingsPath);
		new AppSettings { HideReadFeeds = false }.Save(SettingsPath);
		Assert.False(AppSettings.Load(SettingsPath).HideReadFeeds);
		Assert.False(File.Exists(SettingsPath + ".tmp"));
	}
}
