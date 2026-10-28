using System.Text;
using WinNewsReader.Parser.Html;
using WinNewsReader.Parser.Feeds;

namespace WinNewsReader.Parser.Tests;

/// <summary>NetNewsWire's <c>bestWebsiteIconURL</c>: the largest Apple touch icon, else an Open Graph image, else a Twitter card.</summary>
public class BestWebsiteIconTests
{
	private static HtmlMetadata Page(string head) =>
		HtmlMetadataParser.HtmlMetadata(new ParserData("https://site.example/", Encoding.UTF8.GetBytes($"<html><head>{head}</head><body></body></html>")));

	[Fact]
	public void SixColorsUsesItsLargestAppleTouchIcon()
	{
		var metadata = HtmlMetadataParser.HtmlMetadata(TestHelpers.ParserData("sixcolors", "html", "http://sixcolors.com/"));
		var best = metadata.BestWebsiteIconUrl();
		var largest = metadata.AppleTouchIcons.OrderByDescending(icon => icon.Width).First();
		Assert.Equal(largest.Url, best);
		Assert.True(largest.Width > 120);
	}

	[Fact]
	public void LargestAppleTouchIconWinsOverEarlierSmallerOnes()
	{
		var metadata = Page("""
			<link rel="apple-touch-icon" sizes="57x57" href="/a57.png">
			<link rel="apple-touch-icon" sizes="180x180" href="/a180.png">
			<link rel="apple-touch-icon" sizes="120x120" href="/a120.png">
			""");
		Assert.Equal("https://site.example/a180.png", metadata.BestWebsiteIconUrl());
	}

	[Fact]
	public void BannersAreNotIcons()
	{
		var metadata = Page("""
			<link rel="apple-touch-icon" sizes="600x200" href="/banner.png">
			<link rel="apple-touch-icon" sizes="120x120" href="/square.png">
			""");
		Assert.Equal("https://site.example/square.png", metadata.BestWebsiteIconUrl());

		var onlyBanner = Page("""<link rel="apple-touch-icon" sizes="600x200" href="/banner.png">""");
		Assert.Null(onlyBanner.BestWebsiteIconUrl());
	}

	[Fact]
	public void OpenGraphImageIsNextAndPrefersTheSecureAddress()
	{
		var metadata = Page("""
			<meta property="og:image" content="http://site.example/og-small.jpg">
			<meta property="og:image:width" content="300">
			<meta property="og:image:height" content="300">
			""");
		Assert.Equal("http://site.example/og-small.jpg", metadata.BestWebsiteIconUrl());
	}

	[Fact]
	public void AnAppleTouchIconBeatsAnOpenGraphImage()
	{
		var metadata = Page("""
			<link rel="apple-touch-icon" sizes="60x60" href="/touch.png">
			<meta property="og:image" content="https://site.example/og.jpg">
			""");
		Assert.Equal("https://site.example/touch.png", metadata.BestWebsiteIconUrl());
	}

	[Fact]
	public void WordPressBlankPlaceholderIsNotAnIcon()
	{
		var metadata = Page("""<meta property="og:image" content="https://s0.wp.com/i/blank.jpg">""");
		Assert.Null(metadata.LargestOpenGraphImageUrl());
		Assert.Null(metadata.BestWebsiteIconUrl());
	}

	[Fact]
	public void TwitterImageIsTheLastResort()
	{
		var metadata = Page("""<meta name="twitter:image:src" content="https://site.example/card.png">""");
		Assert.Equal("https://site.example/card.png", metadata.BestWebsiteIconUrl());
	}

	[Fact]
	public void NothingWhenThePageListsNoImages()
	{
		Assert.Null(Page("""<link rel="icon" href="/favicon.ico">""").BestWebsiteIconUrl());
	}
}
