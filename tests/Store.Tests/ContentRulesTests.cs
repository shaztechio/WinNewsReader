using WinNewsReader.Core;

namespace WinNewsReader.Store.Tests;

/// <summary>NetNewsWire's content rules, which keep ad and tracking requests out of the article view.</summary>
public sealed class ContentRulesTests
{
	private static string RulesPath()
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			var path = Path.Combine(directory.FullName, "src", "Core", "ArticleRendering", "Resources", "ContentRules.json");
			if (File.Exists(path))
			{
				return path;
			}
		}
		throw new FileNotFoundException("ContentRules.json");
	}

	[Theory]
	[InlineData("https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js")]
	[InlineData("https://stats.g.doubleclick.net/collect")]
	[InlineData("HTTPS://WWW.GOOGLE-ANALYTICS.COM/analytics.js")]
	[InlineData("https://cdn.taboola.com/libtrc/x.js")]
	public void BlocksAdAndTrackingHosts(string url) => Assert.True(ContentRules.Load(RulesPath()).Blocks(url));

	[Theory]
	[InlineData("https://example.com/image.png")]
	[InlineData("https://daringfireball.net/graphics/logos/")]
	[InlineData("http://localhost/article")]
	public void LeavesEverythingElseAlone(string url) => Assert.False(ContentRules.Load(RulesPath()).Blocks(url));

	[Fact]
	public void ReadsEveryRuleInNetNewsWiresFile() => Assert.Equal(42, ContentRules.Load(RulesPath()).Count);

	[Fact]
	public void SkipsRulesItDoesNotUnderstand()
	{
		var rules = ContentRules.Parse("""
			[
				{"trigger": {"url-filter": "ads[.]example"}, "action": {"type": "block"}},
				{"trigger": {"url-filter": "("}, "action": {"type": "block"}},
				{"trigger": {"url-filter": "hide[.]example"}, "action": {"type": "css-display-none", "selector": ".x"}},
				{"trigger": {}, "action": {"type": "block"}}
			]
			""");

		Assert.Equal(1, rules.Count);
		Assert.True(rules.Blocks("https://ads.example/x.js"));
		Assert.False(rules.Blocks("https://hide.example/"));
	}

	[Fact]
	public void AMissingFileBlocksNothing() => Assert.Equal(0, ContentRules.Load("does-not-exist.json").Count);
}
