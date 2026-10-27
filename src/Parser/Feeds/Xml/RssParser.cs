using WinNewsReader.Parser.Utilities;
using WinNewsReader.Parser.Xml;

namespace WinNewsReader.Parser.Feeds.Xml;

/// <summary>RSS 2.0 and RSS 1.0 (RDF).</summary>
public static class RssParser
{
	public static ParsedFeed Parse(ParserData parserData)
	{
		var handler = new RssHandler(parserData.Url);
		new XmlSaxParser(handler).Parse(parserData.Data);
		return handler.BuildParsedFeed();
	}

	private sealed class RssHandler(string feedUrl) : IXmlSaxParserDelegate
	{
		private string? _title;
		private string? _homePageUrl;
		private string? _language;
		private string? _channelImageUrl;
		private bool _isRdf;
		private bool _endRssFound;

		private readonly List<RssItem> _items = [];
		private bool _parsingArticle;
		private bool _parsingAuthor;
		private bool _parsingChannelImage;
		private bool _parsingTextInput;
		private Dictionary<string, string> _currentAttributes = [];

		// Depth inside a skipped subtree, so an unprefixed <title> inside <s:variant> (for instance) is ignored.
		private int _namespaceElementDepth;
		private readonly MediaElementTracker _mediaElementTracker = new();

		private RssItem? CurrentItem => _items.Count > 0 ? _items[^1] : null;

		public ParsedFeed BuildParsedFeed() => new()
		{
			Type = FeedType.Rss,
			Title = _title,
			HomePageUrl = _homePageUrl,
			FeedUrl = feedUrl,
			Language = _language,
			IconUrl = _channelImageUrl,
			Items = _items.Select(item => item.ToParsedItem(feedUrl)).ToHashSet(),
		};

		public void DidStartElement(XmlSaxParser parser, ReadOnlySpan<byte> localName, XmlNamespace ns, XmlAttributes attributes)
		{
			if (_endRssFound)
			{
				return;
			}
			var isUnprefixed = ns.Prefix is null;
			if (_namespaceElementDepth > 0)
			{
				_namespaceElementDepth++;
				return;
			}
			if (_parsingArticle && ns.IsMediaRss)
			{
				if (CurrentItem is { } item && _mediaElementTracker.Start(localName, attributes, item, parser, ResolveUrl))
				{
					_namespaceElementDepth = 1;
				}
				return;
			}
			if (_parsingArticle && ns.IsITunes && localName.SequenceEqual("duration"u8))
			{
				parser.BeginStoringCharacters();
				return;
			}
			if (_parsingArticle && ns.IsITunes && localName.SequenceEqual("image"u8))
			{
				if (CurrentItem is { ITunesImageUrl: null } item && attributes.ValueIgnoringCase("href") is { Length: > 0 } href)
				{
					item.ITunesImageUrl = ResolveUrl(href);
				}
				_namespaceElementDepth = 1;
				return;
			}
			if (_mediaElementTracker.ContainerDepth > 0)
			{
				_namespaceElementDepth = 1;
				return;
			}
			if (_parsingArticle && ns.Prefix is not null && !ns.IsDublinCore && !ns.IsContent && !ns.IsSource)
			{
				// Skip an element in an unexpected namespace and its subtree, so a nested unprefixed element
				// (Shopify's <title>Default Title</title>) can't be mistaken for the item's own. The namespaces
				// RSS consumes (Dublin Core, content, source) still parse.
				_namespaceElementDepth = 1;
				return;
			}
			// The RDF root is always prefixed in practice (<rdf:RDF>): match on the local name only.
			if (localName.SequenceEqual("RDF"u8))
			{
				_isRdf = true;
				return;
			}
			// RSS 1.0 spells it textinput, RSS 2.0 textInput.
			if (isUnprefixed && AsciiBytes.EqualsIgnoreCase(localName, "textinput"u8))
			{
				_parsingTextInput = true;
				return;
			}
			if (_parsingTextInput)
			{
				return;
			}

			_currentAttributes = (_isRdf && localName.SequenceEqual("item"u8)) || localName.SequenceEqual("guid"u8) || localName.SequenceEqual("enclosure"u8)
				? attributes.ToDictionary()
				: [];

			if (isUnprefixed && localName.SequenceEqual("item"u8))
			{
				var item = new RssItem();
				_items.Add(item);
				_parsingArticle = true;
				_mediaElementTracker.Reset();
				if (_isRdf && _currentAttributes.TryGetValue("rdf:about", out var about) && about.Length > 0)
				{
					item.Guid = about;
					item.Permalink = about;
				}
			}
			else if (isUnprefixed && localName.SequenceEqual("image"u8))
			{
				_parsingChannelImage = true;
			}
			else if (isUnprefixed && localName.SequenceEqual("author"u8) && _parsingArticle)
			{
				_parsingAuthor = true;
			}

			if (!_parsingChannelImage || (isUnprefixed && localName.SequenceEqual("url"u8)))
			{
				parser.BeginStoringCharacters();
			}
		}

		public void DidEndElement(XmlSaxParser parser, ReadOnlySpan<byte> localName, XmlNamespace ns)
		{
			if (_endRssFound)
			{
				return;
			}
			var isUnprefixed = ns.Prefix is null;
			if (_namespaceElementDepth > 0)
			{
				_namespaceElementDepth--;
				return;
			}
			if (_parsingArticle && ns.IsMediaRss)
			{
				if (CurrentItem is { } item)
				{
					_mediaElementTracker.End(localName, item, parser);
				}
				return;
			}
			if ((_isRdf && localName.SequenceEqual("RDF"u8)) || (isUnprefixed && localName.SequenceEqual("rss"u8)))
			{
				_endRssFound = true;
				return;
			}
			if (isUnprefixed && AsciiBytes.EqualsIgnoreCase(localName, "textinput"u8))
			{
				_parsingTextInput = false;
				return;
			}
			if (_parsingTextInput)
			{
				return;
			}
			if (_parsingChannelImage && !_parsingArticle && isUnprefixed && localName.SequenceEqual("url"u8))
			{
				_channelImageUrl = parser.CurrentStringWithTrimmedWhitespace;
				return;
			}
			if (isUnprefixed && localName.SequenceEqual("image"u8))
			{
				_parsingChannelImage = false;
				return;
			}
			if (isUnprefixed && localName.SequenceEqual("item"u8))
			{
				_parsingArticle = false;
				return;
			}
			if (_parsingArticle)
			{
				HandleArticleElementEnd(parser, localName, ns);
				if (isUnprefixed && localName.SequenceEqual("author"u8))
				{
					_parsingAuthor = false;
				}
				return;
			}
			if (!_parsingChannelImage)
			{
				HandleFeedElementEnd(parser, localName, ns);
			}
		}

		private void HandleArticleElementEnd(XmlSaxParser parser, ReadOnlySpan<byte> localName, XmlNamespace ns)
		{
			if (CurrentItem is not { } article)
			{
				return;
			}
			if (ns.Prefix is not null)
			{
				if (ns.IsDublinCore)
				{
					if (localName.SequenceEqual("creator"u8))
					{
						AddAuthor(article, parser.CurrentStringWithTrimmedWhitespace);
					}
					else if (localName.SequenceEqual("date"u8) && parser.CurrentCharacters is { } dateBytes)
					{
						article.DatePublished = DateParser.Parse(dateBytes);
					}
				}
				else if (ns.IsContent && localName.SequenceEqual("encoded"u8))
				{
					if (parser.CurrentStringWithTrimmedWhitespace is { Length: > 0 } encoded)
					{
						article.Body = encoded;
					}
				}
				else if (ns.IsITunes && localName.SequenceEqual("duration"u8))
				{
					article.ITunesDurationInSeconds = ParsedAttachment.ParseDurationInSeconds(parser.CurrentStringWithTrimmedWhitespace);
				}
				else if (ns.IsSource && localName.SequenceEqual("markdown"u8))
				{
					// scripting.com's namespace, used by WordLand-generated feeds.
					article.Markdown = parser.CurrentStringWithTrimmedWhitespace;
				}
				return;
			}

			if (localName.SequenceEqual("guid"u8))
			{
				HandleGuid(article, parser);
			}
			else if (localName.SequenceEqual("pubDate"u8))
			{
				if (parser.CurrentCharacters is { } bytes)
				{
					article.DatePublished = DateParser.Parse(bytes);
				}
			}
			else if (localName.SequenceEqual("author"u8))
			{
				AddAuthor(article, parser.CurrentStringWithTrimmedWhitespace);
			}
			else if (localName.SequenceEqual("link"u8))
			{
				if (article.Link is null && parser.CurrentStringWithTrimmedWhitespace is { Length: > 0 } link)
				{
					article.Link = ResolveUrl(link);
				}
			}
			else if (localName.SequenceEqual("description"u8))
			{
				article.Body ??= parser.CurrentStringWithTrimmedWhitespace;
			}
			else if (!_parsingAuthor && localName.SequenceEqual("title"u8))
			{
				if (parser.CurrentStringWithTrimmedWhitespace is { } title)
				{
					article.Title = title;
				}
			}
			else if (localName.SequenceEqual("enclosure"u8))
			{
				HandleEnclosure(article);
			}
		}

		private void HandleGuid(RssItem article, XmlSaxParser parser)
		{
			if (parser.CurrentStringWithTrimmedWhitespace is not { } guid)
			{
				return;
			}
			article.Guid = guid;
			var isPermaLink = ValueIgnoringKeyCase(_currentAttributes, "ispermalink");
			if ((isPermaLink is null || !string.Equals(isPermaLink, "false", StringComparison.OrdinalIgnoreCase)) && IsProbablyUrlOrRelativePath(guid))
			{
				article.Permalink = ResolveUrl(guid);
			}
		}

		private void HandleEnclosure(RssItem article)
		{
			if (!_currentAttributes.TryGetValue("url", out var url) || url.Length == 0)
			{
				return;
			}
			var length = MediaRss.StrictInt(_currentAttributes.GetValueOrDefault("length")) ?? 0;
			if (ParsedAttachment.Create(url, _currentAttributes.GetValueOrDefault("type"), sizeInBytes: length > 0 ? length : null) is { } attachment)
			{
				article.Attachments.Add(attachment);
			}
		}

		private void HandleFeedElementEnd(XmlSaxParser parser, ReadOnlySpan<byte> localName, XmlNamespace ns)
		{
			if (ns.Prefix is not null)
			{
				return;
			}
			if (localName.SequenceEqual("link"u8))
			{
				if (string.IsNullOrEmpty(_homePageUrl))
				{
					_homePageUrl = parser.CurrentStringWithTrimmedWhitespace;
				}
			}
			else if (localName.SequenceEqual("title"u8))
			{
				_title = parser.CurrentStringWithTrimmedWhitespace;
			}
			else if (localName.SequenceEqual("language"u8))
			{
				_language = parser.CurrentStringWithTrimmedWhitespace;
			}
		}

		private static void AddAuthor(RssItem article, string? s)
		{
			if (string.IsNullOrEmpty(s))
			{
				return;
			}
			// RSS authors are supposed to be email addresses but often aren't. Classify by content.
			if (s.Contains('@'))
			{
				article.Authors.Add(new ParsedAuthor(null, null, null, s));
			}
			else if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
			{
				article.Authors.Add(new ParsedAuthor(null, s, null, null));
			}
			else
			{
				article.Authors.Add(new ParsedAuthor(s, null, null, null));
			}
		}

		/// <summary>Bad guids are often integers, and guids starting with tag: aren't URLs.</summary>
		private static bool IsProbablyUrlOrRelativePath(string s) =>
			!s.Contains(' ') && s.Contains('/') && !s.StartsWith("tag:", StringComparison.OrdinalIgnoreCase);

		private string ResolveUrl(string s)
		{
			if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(_homePageUrl)
				|| FoundationUrl.Parse(_homePageUrl) is not { } baseUrl)
			{
				return s;
			}
			return FoundationUrl.Resolve(s, baseUrl) ?? s;
		}

		private static string? ValueIgnoringKeyCase(Dictionary<string, string> dict, string key)
		{
			if (dict.TryGetValue(key, out var value))
			{
				return value;
			}
			return dict.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
		}
	}
}
