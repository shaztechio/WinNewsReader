using WinNewsReader.Parser.Feeds;

namespace WinNewsReader.Core;

/// <summary>
/// NetNewsWire's rule for an author's picture in a timeline row (<c>ArticleIconImageLoader</c>): it replaces the feed's
/// icon only when the article has exactly one author, and that author has an avatar.
/// </summary>
public static class AuthorAvatar
{
	public static string? UrlFor(IReadOnlyList<ParsedAuthor>? authors)
	{
		if (authors is not { Count: 1 } || authors[0].AvatarUrl is not { Length: > 0 } avatar)
		{
			return null;
		}
		return Uri.TryCreate(avatar.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
	}
}
