using System.Text;
using System.Text.RegularExpressions;

namespace WinNewsReader.Core.ArticleRendering;

/// <summary>Text helpers for article rendering, from NetNewsWire's <c>ArticleStringFormatter</c> and RSCore.</summary>
public static partial class ArticleText
{
	/// <summary>Inline tags allowed in titles, matched by name with attributes ignored.</summary>
	private static readonly HashSet<string> AllowedTitleTags =
	[
		"abbr", "b", "bdi", "bdo", "cite", "code", "del", "dfn", "em", "i", "ins", "kbd", "mark", "q", "s", "samp", "small",
		"strong", "sub", "sup", "time", "u", "var",
	];

	private static readonly string[] LinkSchemes = ["http", "https", "mailto"];

	/// <summary>
	/// NetNewsWire's <c>sanitizedTitle</c>. An allowed inline tag is kept for HTML and dropped (keeping its contents)
	/// otherwise; any other tag is escaped for HTML and kept literally otherwise. An unclosed tag gets no closing
	/// <c>&gt;</c> added, and a bare <c>&lt;</c> at the end disappears.
	/// </summary>
	public static string? SanitizedTitle(string? title, bool forHtml)
	{
		if (string.IsNullOrEmpty(title))
		{
			return title;
		}
		var result = new StringBuilder(title.Length);
		var i = 0;
		while (i < title.Length)
		{
			if (title[i] != '<')
			{
				result.Append(title[i]);
				i++;
				continue;
			}
			var j = title.IndexOf('>', i + 1);
			var tagClosed = j >= 0;
			var tagEnd = tagClosed ? j : title.Length;
			var tagStart = i + 1;
			if (tagStart == tagEnd)
			{
				i++;
				continue;
			}
			var nameStart = title[tagStart] == '/' ? tagStart + 1 : tagStart;
			var nameEnd = nameStart;
			while (nameEnd < tagEnd && char.IsAsciiLetter(title[nameEnd]))
			{
				nameEnd++;
			}
			var tagBody = title[tagStart..tagEnd];
			if (AllowedTitleTags.Contains(title[nameStart..nameEnd]))
			{
				if (forHtml)
				{
					result.Append('<').Append(tagBody).Append(tagClosed ? ">" : "");
				}
			}
			else if (forHtml)
			{
				result.Append("&lt;").Append(tagBody).Append(tagClosed ? "&gt;" : "");
			}
			else
			{
				result.Append('<').Append(tagBody).Append(tagClosed ? ">" : "");
			}
			i = tagClosed ? j + 1 : title.Length;
		}
		return result.ToString();
	}

	/// <summary>
	/// RSCore's <c>convertingPlainTextToHTML</c>: escaped, with blank lines as paragraph breaks, other line breaks as
	/// <c>&lt;br&gt;</c>, and URLs as links.
	/// </summary>
	public static string PlainTextToHtml(string text)
	{
		var paragraphs = new List<string>();
		var paragraphLines = new List<string>();
		void EndParagraph()
		{
			if (paragraphLines.Count > 0)
			{
				paragraphs.Add("<p>" + string.Join("<br>\n", paragraphLines) + "</p>");
				paragraphLines.Clear();
			}
		}
		foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
		{
			if (line.Trim(' ', '\t').Length == 0)
			{
				EndParagraph();
			}
			else
			{
				paragraphLines.Add(LinkifiedEscapedLine(line));
			}
		}
		EndParagraph();
		return string.Join("\n", paragraphs);
	}

	/// <summary>RSCore's <c>escapingSpecialXMLCharacters</c>.</summary>
	public static string EscapingSpecialXmlCharacters(string text) =>
		text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");

	public static string StrippingHttpOrHttpsScheme(string url) =>
		url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url[8..]
		: url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? url[7..]
		: url;

	/// <summary>NetNewsWire uses a link detector here; this finds URLs that start with a scheme, and mailto addresses.</summary>
	private static string LinkifiedEscapedLine(string line)
	{
		var html = new StringBuilder();
		var position = 0;
		foreach (Match match in Link().Matches(line))
		{
			var url = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', '\'', '"');
			if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !LinkSchemes.Contains(uri.Scheme))
			{
				continue;
			}
			html.Append(EscapingSpecialXmlCharacters(line[position..match.Index]));
			var escaped = EscapingSpecialXmlCharacters(url);
			html.Append($"<a href=\"{escaped}\">{escaped}</a>");
			position = match.Index + url.Length;
		}
		html.Append(EscapingSpecialXmlCharacters(line[position..]));
		return html.ToString();
	}

	[GeneratedRegex(@"(?:https?://|mailto:)[^\s<>""]+", RegexOptions.IgnoreCase)]
	private static partial Regex Link();
}
