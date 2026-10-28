using WinNewsReader.Core;

namespace WinNewsReader.Store.Tests;

public sealed class PreferencesTests
{
	[Fact]
	public void RefreshIntervalsAreNetNewsWires()
	{
		Assert.Equal([0, 30, 60, 120, 240, 480], RefreshIntervals.All.Select(interval => interval.Minutes));
		Assert.Equal("Manually", RefreshIntervals.All[0].Name);
		Assert.Null(RefreshIntervals.ToTimeSpan(0));
		Assert.Equal(TimeSpan.FromMinutes(30), RefreshIntervals.ToTimeSpan(30));
		Assert.Equal(TimeSpan.FromHours(8), RefreshIntervals.ToTimeSpan(480));
	}

	[Theory]
	[InlineData(45)]
	[InlineData(-5)]
	[InlineData(100000)]
	public void UnknownRefreshIntervalBecomesTheDefault(int minutes)
	{
		Assert.Equal(60, RefreshIntervals.Normalized(minutes));
		Assert.Equal(TimeSpan.FromHours(1), RefreshIntervals.ToTimeSpan(minutes));
	}

	[Theory]
	[InlineData(@"""C:\Program Files\Google\Chrome\Application\chrome.exe""", @"C:\Program Files\Google\Chrome\Application\chrome.exe")]
	[InlineData(@"""C:\Program Files (x86)\Mozilla Firefox\firefox.exe"" -url ""%1""", @"C:\Program Files (x86)\Mozilla Firefox\firefox.exe")]
	[InlineData(@"C:\Tools\browser.exe --new-window", @"C:\Tools\browser.exe")]
	[InlineData(@"C:\Tools\browser.exe", @"C:\Tools\browser.exe")]
	[InlineData(@"  ""C:\Tools\My Browser\b.EXE""  ", @"C:\Tools\My Browser\b.EXE")]
	[InlineData("", null)]
	[InlineData("not a program", null)]
	[InlineData(null, null)]
	public void ExecutableIsTakenFromARegisteredCommand(string? command, string? expected) =>
		Assert.Equal(expected, InstalledBrowsers.ExecutableFrom(command));

	[Fact]
	public void InstalledBrowsersListsEachOnceByName()
	{
		var browsers = InstalledBrowsers.Find();
		Assert.Equal(browsers.Count, browsers.Select(browser => browser.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
		Assert.All(browsers, browser => Assert.True(File.Exists(browser.Path)));
		Assert.Equal(browsers.Select(browser => browser.Name).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase), browsers.Select(browser => browser.Name));
	}
}
