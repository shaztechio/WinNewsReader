using System.Text;
using System.Text.RegularExpressions;
using WinNewsReader.Parser.Xml;

namespace WinNewsReader.Store;

/// <summary>NetNewsWire's search index text (<c>ArticleSearchInfo</c>) and its search query (<c>sqliteSearchString</c>).</summary>
public static partial class SearchText
{
	/// <summary>
	/// The body to index: the HTML content, else the text content, else the summary, as plain text, with the authors'
	/// names after it.
	/// </summary>
	public static string Body(string? contentHtml, string? contentText, string? summary, IEnumerable<string?>? authorNames)
	{
		var body = !string.IsNullOrEmpty(contentHtml) ? contentHtml : !string.IsNullOrEmpty(contentText) ? contentText : summary ?? "";
		body = Tags().Replace(ScriptsAndStyles().Replace(body.DecodingHtmlEntities(), " "), " ");
		var names = string.Join(" ", authorNames?.Where(name => !string.IsNullOrEmpty(name)) ?? []);
		return Normalized(names.Length > 0 ? body + " " + names : body);
	}

	public static string Title(string? title) => Normalized(title ?? "");

	/// <summary>
	/// The FTS query for what the reader typed: every word as a prefix, so "feed rea" finds "feeds reader". AND and OR
	/// stay operators. Null when there are no words to look for.
	/// </summary>
	public static string? Query(string searchString)
	{
		var terms = Words().Matches(searchString).Select(match => match.Value is "AND" or "OR" ? match.Value : match.Value + "*").ToList();
		// A query can't start or end with an operator.
		while (terms.Count > 0 && terms[0] is "AND" or "OR")
		{
			terms.RemoveAt(0);
		}
		while (terms.Count > 0 && terms[^1] is "AND" or "OR")
		{
			terms.RemoveAt(terms.Count - 1);
		}
		return terms.Count == 0 ? null : string.Join(" ", terms);
	}

	/// <summary>
	/// The tokenizer counts non-ASCII punctuation and symbols as part of words, so “‘BeReal’” wouldn't match "bereal".
	/// Swap them for spaces before indexing, as NetNewsWire does.
	/// </summary>
	private static string Normalized(string text)
	{
		var builder = new StringBuilder(text.Length);
		foreach (var c in text)
		{
			builder.Append(char.IsPunctuation(c) || char.IsSymbol(c) ? ' ' : c);
		}
		return Whitespace().Replace(builder.ToString(), " ").Trim();
	}

	[GeneratedRegex(@"<(script|style)\b.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
	private static partial Regex ScriptsAndStyles();

	[GeneratedRegex("<[^>]*>")]
	private static partial Regex Tags();

	[GeneratedRegex(@"\s+")]
	private static partial Regex Whitespace();

	/// <summary>Letters and digits, like NSString's word enumeration; quotes, stars and dashes can't reach the FTS syntax.</summary>
	[GeneratedRegex(@"[\p{L}\p{N}]+")]
	private static partial Regex Words();
}
