using System.Text.Json;
using WinNewsReader.Core.ArticleRendering;
using WinNewsReader.Store;

namespace WinNewsReader.Store.Tests;

public class PrimaryMediaTests
{
	private const string Image = "https://example.com/art.jpg";
	private const string Audio = "https://example.com/episode.mp3";
	private const string Video = "https://example.com/movie.mp4";
	private static StoredArticle Article(string? title = "Title", string body = "<p>Body</p>") =>
		new("id", "feed", "id", title, "https://example.com/post", null, body, null, null, null, null, DateTimeOffset.UnixEpoch, false, false);

	private static string Render(StoredArticle article, string template = "<main>[[primary_media]]</main><section>[[body]]</section><footer>[[enclosures]]</footer>")
	{
		var resources = Path.Combine(AppContext.BaseDirectory, "ArticleRendering", "Resources");
		var folder = Directory.CreateTempSubdirectory("wnr-primary-").FullName;
		try
		{
			File.WriteAllText(Path.Combine(folder, "Info.plist"), "<plist><dict/></plist>");
			File.WriteAllText(Path.Combine(folder, "template.html"), template);
			var theme = ArticleTheme.Load(folder, false, ArticleTheme.LoadDefault(resources), resources);
			return ArticleRenderer.Render(article, new(null, false, "largeText", ""), theme).Html;
		}
		finally { Directory.Delete(folder, true); }
	}

	private static string Attachments(params string[] urls) => JsonSerializer.Serialize(urls.Select(url => new { Url = url }));

	[Fact]
	public void VideoWinsAndPosterIsNotRepeated()
	{
		var html = Render(Article() with { ImageUrl = Image, AttachmentsJson = Attachments(Audio, Image, Video) });
		Assert.Contains("<main><div class=\"nnw-primary-media\"><div class=\"nnw-enclosure nnw-enclosure-video\">", html);
		Assert.Contains($"poster=\"{Image}\"", html);
		Assert.DoesNotContain("<img", html);
		Assert.Contains("<footer><div class=\"nnw-enclosures\">", html);
		Assert.Equal(1, html.Split(Video).Length - 1);
	}

	[Fact]
	public void AudioGetsArtworkAndRemainingEnclosuresStayInFooter()
	{
		var html = Render(Article() with { ImageUrl = Image, AttachmentsJson = Attachments(Audio, Image, "https://example.com/other.jpg") });
		var main = html[..html.IndexOf("</main>", StringComparison.Ordinal)];
		Assert.Contains("nnw-primary-image", main);
		Assert.Contains("<audio", main);
		Assert.Equal(1, html.Split(Image).Length - 1);
		Assert.DoesNotContain(Audio, html[html.IndexOf("<footer>", StringComparison.Ordinal)..]);
		Assert.Contains("other.jpg", html);
	}

	[Theory]
	[InlineData("Title", 5, true)]
	[InlineData(null, 2047, false)]
	[InlineData("", 2048, true)]
	public void ShortUntitledPostsKeepImagesAfterText(string? title, int length, bool primary)
	{
		var html = Render(Article(title, new string('x', length)) with { ImageUrl = Image });
		Assert.Equal(primary, html.Contains("nnw-primary-media", StringComparison.Ordinal));
		Assert.Contains(Image, html);
	}

	[Fact]
	public void ThumbnailIsPrimaryOnlyAndImageAttachmentTakesPrecedence()
	{
		var article = Article() with { ThumbnailUrl = Image };
		Assert.Contains(Image, Render(article));
		Assert.DoesNotContain(Image, Render(article, "[[body]][[enclosures]]"));
		Assert.DoesNotContain(Image, Render(article with { AttachmentsJson = Attachments("https://example.com/lead.jpg") }));
	}

	[Theory]
	[InlineData("[[body]]")]
	[InlineData("[[body]][[enclosures]]")]
	public void LegacyTemplatesKeepArtworkAndAudio(string template)
	{
		var html = Render(Article() with { ImageUrl = Image, AttachmentsJson = Attachments(Audio) }, template);
		Assert.Contains(Image, html);
		Assert.Contains(Audio, html);
		Assert.DoesNotContain("[[", html);
		Assert.True(html.IndexOf("Body", StringComparison.Ordinal) < html.IndexOf(Audio, StringComparison.Ordinal));
	}

	[Fact]
	public void MediaAlreadyInBodyIsNotRepeated()
	{
		var html = Render(Article(body: $"<img src=\"{Image}\"><audio src=\"{Audio}\"></audio>") with
		{ ImageUrl = Image, AttachmentsJson = Attachments(Image, Audio) });
		Assert.DoesNotContain("nnw-primary-media", html);
		Assert.DoesNotContain("nnw-enclosures", html);
	}

	[Fact]
	public void ImageCaptionEscapesMarkupAndSuppressesRepeatedArticleTitle()
	{
		var article = Article("A title") with { AttachmentsJson = JsonSerializer.Serialize(new[] { new { Url = Image, Title = " A\u00a0TITLE " } }) };
		var html = Render(article);
		Assert.DoesNotContain("nnw-primary-image-caption", html);
		Assert.Contains(" title=", html);
		html = Render(article with { Title = "Different", AttachmentsJson = JsonSerializer.Serialize(new[] { new { Url = Image, Title = "<script>&" } }) });
		Assert.Contains("nnw-primary-image-caption\">&lt;script&gt;&amp;", html);
	}

	[Fact]
	public void EmptyMediaSubstitutesEmptyStrings() =>
		Assert.Equal("<main></main><section><p>Body</p></section><footer></footer>", Render(Article()));

	[Fact]
	public void PrimaryOnlyTemplateAppendsRemainingEnclosuresAfterBody()
	{
		var html = Render(Article() with { AttachmentsJson = Attachments(Video, Audio) }, "[[primary_media]]<section>[[body]]</section>");
		Assert.Equal(1, html.Split(Video).Length - 1);
		Assert.Equal(1, html.Split(Audio).Length - 1);
		Assert.True(html.IndexOf(Video, StringComparison.Ordinal) < html.IndexOf("Body", StringComparison.Ordinal));
		Assert.True(html.IndexOf(Audio, StringComparison.Ordinal) > html.IndexOf("Body", StringComparison.Ordinal));
	}

	[Fact]
	public void ShortPostAudioRemainsPrimaryWhileArtworkFollowsText()
	{
		var html = Render(Article(null) with { ImageUrl = Image, AttachmentsJson = Attachments(Audio) });
		Assert.Contains(Audio, html[..html.IndexOf("</main>", StringComparison.Ordinal)]);
		Assert.Contains(Image, html[html.IndexOf("<footer>", StringComparison.Ordinal)..]);
	}

	[Fact]
	public void YouTubeAttachmentIsPrimaryVideo()
	{
		var html = Render(Article() with { AttachmentsJson = Attachments("https://www.youtube.com/v/dQw4w9WgXcQ?version=3") });
		Assert.Contains("<main><div class=\"nnw-primary-media\">", html);
		Assert.Contains("youtube-nocookie.com/embed/dQw4w9WgXcQ", html);
		Assert.DoesNotContain("nnw-enclosures", html);
	}
}
