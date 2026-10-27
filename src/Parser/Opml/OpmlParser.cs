using WinNewsReader.Parser.Feeds;
using WinNewsReader.Parser.Utilities;
using WinNewsReader.Parser.Xml;

namespace WinNewsReader.Parser.Opml;

public sealed class OpmlException(string fileName)
	: Exception($"The file ‘{fileName}’ can’t be parsed because it’s not an OPML file.")
{
	public string FileName { get; } = fileName;
}

public sealed record OpmlFeedSpecifier(string? Title, string? FeedDescription, string? HomePageUrl, string FeedUrl);

public class OpmlItem(Dictionary<string, string>? attributes = null)
{
	private List<OpmlItem>? _children;

	public Dictionary<string, string>? Attributes { get; } = attributes;

	public IReadOnlyList<OpmlItem>? Children => _children;

	public void AddChild(OpmlItem child) => (_children ??= []).Add(child);

	public string? Text => Attribute("text");

	public string? AttributeTitle => Attribute("title");

	/// <summary>OPML attribute fallback order: title, then text.</summary>
	public string? TitleFromAttributes => Attributes is null ? null : AttributeTitle ?? Text;

	/// <summary>True if this item has children, so it's a folder rather than a feed.</summary>
	public bool IsFolder => _children is { Count: > 0 };

	/// <summary>Built from the attributes when there's a non-empty <c>xmlUrl</c>; null for folders and notes.</summary>
	public OpmlFeedSpecifier? FeedSpecifier =>
		Attribute("xmlUrl") is { Length: > 0 } feedUrl
			? new OpmlFeedSpecifier(TitleFromAttributes, Attribute("description"), Attribute("htmlUrl"), feedUrl)
			: null;

	/// <summary>Exact key first, then case-insensitive (feeds write xmlurl, XMLURL, XmlUrl).</summary>
	public string? Attribute(string key)
	{
		if (Attributes is null)
		{
			return null;
		}
		if (Attributes.TryGetValue(key, out var value))
		{
			return value;
		}
		return Attributes.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
	}
}

public sealed class OpmlDocument(string? url) : OpmlItem
{
	public string? Title { get; set; }

	public string? Url { get; } = url;
}

public static class OpmlParser
{
	/// <summary>Throws <see cref="OpmlException"/> if the data doesn't look like OPML.</summary>
	public static OpmlDocument Parse(ParserData parserData)
	{
		ValidateLooksLikeOpml(parserData);
		var handler = new OpmlHandler(parserData.Url);
		new XmlSaxParser(handler).Parse(parserData.Data);
		return handler.Document;
	}

	/// <summary>Look for <c>&lt;opml</c>, case-insensitive, in the first 4 KB.</summary>
	private static void ValidateLooksLikeOpml(ParserData parserData)
	{
		var bytes = parserData.Data;
		var limit = Math.Min(bytes.Length, 4096);
		for (var i = 0; i + 5 <= limit; i++)
		{
			if (bytes[i] == '<' && AsciiBytes.EqualsIgnoreCase(bytes.AsSpan(i + 1, 4), "opml"u8))
			{
				return;
			}
		}
		var url = FoundationUrl.Parse(parserData.Url);
		var fileName = url?.Scheme == "file" ? Path.GetFileName(url.Value.Path) : parserData.Url;
		throw new OpmlException(fileName);
	}

	private sealed class OpmlHandler : IXmlSaxParserDelegate
	{
		private readonly List<OpmlItem> _itemStack;
		private bool _collectingTitleOnDocument;

		public OpmlHandler(string url)
		{
			Document = new OpmlDocument(url);
			_itemStack = [Document];
		}

		public OpmlDocument Document { get; }

		private OpmlItem CurrentItem => _itemStack[^1];

		public void DidStartElement(XmlSaxParser parser, ReadOnlySpan<byte> localName, XmlNamespace ns, XmlAttributes attributes)
		{
			if (localName.SequenceEqual("title"u8))
			{
				parser.BeginStoringCharacters();
				// Only the document-level title matters.
				_collectingTitleOnDocument = _itemStack.Count == 1;
				return;
			}
			if (!localName.SequenceEqual("outline"u8))
			{
				return;
			}
			var item = new OpmlItem(attributes.ToDictionary());
			CurrentItem.AddChild(item);
			_itemStack.Add(item);
		}

		public void DidEndElement(XmlSaxParser parser, ReadOnlySpan<byte> localName, XmlNamespace ns)
		{
			if (localName.SequenceEqual("title"u8))
			{
				if (_collectingTitleOnDocument)
				{
					Document.Title = parser.CurrentStringWithTrimmedWhitespace;
				}
				_collectingTitleOnDocument = false;
				return;
			}
			if (localName.SequenceEqual("outline"u8) && _itemStack.Count > 1)
			{
				_itemStack.RemoveAt(_itemStack.Count - 1);
			}
		}
	}
}
