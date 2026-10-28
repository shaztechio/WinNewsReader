using System.Text;
using WinNewsReader.Parser.Feeds;
using WinNewsReader.Parser.Utilities;

namespace WinNewsReader.Parser.Html;

/// <summary>An <c>&lt;a&gt;</c> element. Any field may be null, since HTML can be malformed.</summary>
/// <param name="Url">Absolute, resolved against the page URL.</param>
/// <param name="Text">Inner text, trimmed.</param>
/// <param name="Title">The <c>title</c> attribute.</param>
public sealed record HtmlLink(string? Url, string? Text, string? Title);

/// <summary>
/// Extracts every <c>&lt;a&gt;</c> element from an HTML page.
/// Text follows the libxml2 behavior NetNewsWire's tests were written against: a nested start tag resets
/// the text, so <c>&lt;a&gt;before &lt;b&gt;after&lt;/b&gt;&lt;/a&gt;</c> has the text "after".
/// </summary>
public static class HtmlLinkParser
{
	public static IReadOnlyList<HtmlLink> HtmlLinks(ParserData parserData)
	{
		var linkDelegate = new LinkParserDelegate(FoundationUrl.Parse(parserData.Url));
		new HtmlScanner(linkDelegate).Parse(parserData.Data);
		linkDelegate.FlushPending();
		return linkDelegate.Links;
	}

	private sealed class LinkParserDelegate(FoundationUrl.Parts? baseUrl) : IHtmlScannerDelegate
	{
		private readonly List<byte> _pendingText = [];
		private bool _collectingText;
		private string? _pendingUrl;
		private string? _pendingTitle;

		public List<HtmlLink> Links { get; } = [];

		public void DidStartTag(ReadOnlySpan<byte> name, HtmlAttributes attributes, bool selfClosing)
		{
			if (IsAnchorTag(name))
			{
				// A new <a> starts a fresh link, closing any still-open one.
				FlushPending();
				_pendingUrl = attributes["href"] is { } href ? FoundationUrl.Resolve(href, baseUrl) : null;
				_pendingTitle = attributes["title"];
				_pendingText.Clear();
				_collectingText = true;
				return;
			}
			// A nested tag inside an anchor resets the text, as libxml2's SAX parser does.
			if (_collectingText)
			{
				_pendingText.Clear();
			}
		}

		public void DidEndTag(ReadOnlySpan<byte> name)
		{
			if (IsAnchorTag(name) && _collectingText)
			{
				FlushPending();
			}
		}

		public void DidFindCharacters(ReadOnlySpan<byte> bytes)
		{
			if (_collectingText)
			{
				_pendingText.AddRange(bytes);
			}
		}

		public void FlushPending()
		{
			if (!_collectingText)
			{
				return;
			}
			var text = Encoding.UTF8.GetString([.. _pendingText]).Trim();
			Links.Add(new HtmlLink(_pendingUrl, text.Length == 0 ? null : text, _pendingTitle));
			_pendingUrl = null;
			_pendingTitle = null;
			_pendingText.Clear();
			_collectingText = false;
		}

		private static bool IsAnchorTag(ReadOnlySpan<byte> name) => name is [(byte)'a' or (byte)'A'];
	}
}
