using System.Text.RegularExpressions;
using WinNewsReader.Parser.Feeds;
using WinNewsReader.Store;

namespace WinNewsReader.Core.ArticleRendering;

public static partial class ArticleRenderer
{
	// Ported from NetNewsWire's ArticleRenderer+PrimaryMedia.swift (7.3).
	private static (string Primary, string Enclosures) MediaHtml(StoredArticle article, string template, string body)
	{
		var attachments = (article.Attachments ?? []).ToList();
		var used = new HashSet<string>(StringComparer.Ordinal);
		var hasPrimary = template.Contains("[[primary_media]]", StringComparison.Ordinal);
		var showsImage = hasPrimary && !(string.IsNullOrEmpty(article.Title) && body.EnumerateRunes().Count() < 2048);
		var poster = article.ImageUrl ?? article.ThumbnailUrl;
		var primary = "";
		var playable = attachments.Where(a => !BodyContainsUrl(body, a.Url)).ToList();
		if (hasPrimary)
		{
			var video = playable.FirstOrDefault(a => DisplayMedium(a) == AttachmentMedium.Video
				|| ArticleRenderingSpecialCases.YouTubeEmbedUrl(a.Url) is not null);
			if (video is not null)
			{
				primary = EnclosureHtml(video, poster) ?? "";
				used.Add(video.Url);
				if (poster is not null) used.Add(poster);
			}
			else
			{
				if (showsImage && PrimaryImage(article.ImageUrl, article.ThumbnailUrl, attachments, body) is { } image)
				{
					primary = PrimaryImageHtml(image, article.Title);
					used.Add(image.Url);
				}
				if (playable.FirstOrDefault(a => DisplayMedium(a) == AttachmentMedium.Audio) is { } audio)
				{
					primary += EnclosureHtml(audio, null);
					used.Add(audio.Url);
				}
			}
		}
		attachments.RemoveAll(a => used.Contains(a.Url));
		if (!showsImage && PrimaryImage(article.ImageUrl, null, attachments, body) is { } fallback
			&& !used.Contains(fallback.Url) && !attachments.Any(a => a.Url == fallback.Url))
		{
			attachments.Insert(0, fallback);
		}
		// Artwork is selected above. Passing it only as a poster prevents the old enclosure
		// helper from adding it again after a primary audio player or a short post's video.
		var enclosures = attachments.Where(a => !BodyContainsUrl(body, a.Url))
			.Select(a => EnclosureHtml(a, poster)).OfType<string>().ToList();
		return (primary.Length == 0 ? "" : $"<div class=\"nnw-primary-media\">{primary}</div>",
			enclosures.Count == 0 ? "" : $"<div class=\"nnw-enclosures\">\n{string.Join("\n", enclosures)}\n</div>");
	}

	private static ParsedAttachment? PrimaryImage(string? imageUrl, string? thumbnailUrl, IReadOnlyList<ParsedAttachment> attachments, string body)
	{
		var image = !string.IsNullOrEmpty(imageUrl)
			? attachments.FirstOrDefault(a => a.Url == imageUrl) ?? ParsedAttachment.Create(imageUrl, medium: AttachmentMedium.Image)
			: attachments.FirstOrDefault(a => DisplayMedium(a) == AttachmentMedium.Image)
				?? (string.IsNullOrEmpty(thumbnailUrl) ? null : ParsedAttachment.Create(thumbnailUrl, medium: AttachmentMedium.Image));
		return image is not null && !BodyContainsUrl(body, image.Url) ? image : null;
	}

	private static string PrimaryImageHtml(ParsedAttachment image, string? articleTitle)
	{
		var attributes = $"src=\"{ArticleText.EscapingSpecialXmlCharacters(image.Url)}\"";
		var caption = "";
		if (!string.IsNullOrEmpty(image.Title))
		{
			var title = ArticleText.EscapingSpecialXmlCharacters(image.Title);
			attributes += $" alt=\"{title}\" title=\"{title}\"";
			if (!string.Equals(CollapseWhitespace(image.Title), CollapseWhitespace(articleTitle ?? ""), StringComparison.OrdinalIgnoreCase))
			{
				caption = $"<div class=\"nnw-primary-image-caption\">{title}</div>";
			}
		}
		else attributes += " alt=\"\"";
		return $"<div class=\"nnw-primary-image\"><img {attributes}{DimensionAttributes(image)}>{caption}</div>";
	}

	private static string CollapseWhitespace(string value) => Regex.Replace(value, @"\s+", " ").Trim();
}
