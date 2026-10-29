using System.Net;
using System.Text.RegularExpressions;
using WinNewsReader.Core;

namespace WinNewsReader.Store.Tests;

/// <summary>The Keyboard Shortcuts window and the help page's table list the same shortcuts.</summary>
public sealed class KeyboardShortcutsTests
{
	private static string HelpPage()
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			var path = Path.Combine(directory.FullName, "site", "help", "index.html");
			if (File.Exists(path))
			{
				return File.ReadAllText(path);
			}
		}
		throw new FileNotFoundException("site/help/index.html");
	}

	private static string Plain(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "")).Trim();

	[Fact]
	public void TheWindowListsExactlyWhatTheHelpPageTables()
	{
		var page = HelpPage();
		var keyboard = page[page.IndexOf("<section id=\"keyboard\">", StringComparison.Ordinal)..];
		keyboard = keyboard[..keyboard.IndexOf("</section>", StringComparison.Ordinal)];
		var expected = new List<ShortcutEntry>();
		foreach (Match table in Regex.Matches(keyboard, "<h3>(.*?)</h3>\\s*<table>(.*?)</table>", RegexOptions.Singleline))
		{
			foreach (Match row in Regex.Matches(table.Groups[2].Value, "<tr><td>(.*?)</td><td>(.*?)</td></tr>", RegexOptions.Singleline))
			{
				expected.Add(new ShortcutEntry(Plain(table.Groups[1].Value), Plain(row.Groups[1].Value), Plain(row.Groups[2].Value)));
			}
		}

		Assert.NotEmpty(expected);
		Assert.Equal(expected, KeyboardShortcuts.All);
	}

	[Fact]
	public void EveryShortcutHasKeysAndAnAction() =>
		Assert.All(KeyboardShortcuts.All, entry =>
		{
			Assert.False(string.IsNullOrWhiteSpace(entry.Keys));
			Assert.False(string.IsNullOrWhiteSpace(entry.Action));
		});
}
