using WinNewsReader.Parser.Html;
using WinNewsReader.Parser.Utilities;
using WinNewsReader.Parser.Xml;

namespace WinNewsReader.Parser.Feeds.Xml;

public static class AtomParser
{
	public static ParsedFeed Parse(ParserData parserData)
	{
		var handler = new AtomHandler(parserData.Url);
		new XmlSaxParser(handler).Parse(parserData.Data);
		return handler.BuildParsedFeed();
	}

	private sealed class AtomHandler(string feedUrl) : IXmlSaxParserDelegate
	{
		private readonly bool _isDaringFireball = feedUrl.Contains("daringfireball.net/");

		private string? _feedTitle;
		private string? _homePageUrl;
		private string? _language;
		private string? _iconUrl;
		private string? _logoUrl;

		private readonly List<RssItem> _items = [];
		private ParsedAuthor? _rootAuthor;
		private AtomAuthor? _currentAuthor;

		private bool _parsingArticle;
		private bool _parsingAuthor;
		private bool _parsingSource;
		private bool _endFeedFound;

		// Depth inside a skipped subtree, so an unprefixed <title> inside <s:variant> (for instance) is ignored.
		private int _namespaceElementDepth;
		private readonly MediaElementTracker _mediaElementTracker = new();

		// Attributes and effective xml:base per open element, kept exactly parallel.
		private readonly List<XmlAttributes> _attributesStack = [];
		private readonly List<FoundationUrl.Parts?> _baseUrlStack = [];

		private RssItem? CurrentArticle => _items.Count > 0 ? _items[^1] : null;

		private XmlAttributes CurrentAttributes => _attributesStack.Count > 0 ? _attributesStack[^1] : XmlAttributes.Empty;

		private FoundationUrl.Parts? EffectiveBaseUrl => _baseUrlStack.Count > 0 ? _baseUrlStack[^1] : null;

		public ParsedFeed BuildParsedFeed()
		{
			if (_rootAuthor is not null)
			{
				foreach (var item in _items.Where(item => item.Authors.Count == 0))
				{
					item.Authors.Add(_rootAuthor);
				}
			}
			// <logo> is the larger image and <icon> the favicon. With no logo, the icon serves for both.
			return new ParsedFeed
			{
				Type = FeedType.Atom,
				Title = _feedTitle,
				HomePageUrl = _homePageUrl,
				FeedUrl = feedUrl,
				Language = _language,
				IconUrl = _logoUrl ?? _iconUrl,
				FaviconUrl = _iconUrl,
				Items = _items.Select(item => item.ToParsedItem(feedUrl)).ToHashSet(),
			};
		}

		public void DidStartElement(XmlSaxParser parser, ReadOnlySpan<byte> localName, XmlNamespace ns, XmlAttributes attributes)
		{
			if (_endFeedFound)
			{
				return;
			}
			_attributesStack.Add(attributes);
			PushBaseUrl(attributes);

			if (_namespaceElementDepth > 0)
			{
				_namespaceElementDepth++;
				return;
			}
			if (_parsingArticle && ns.IsMediaRss)
			{
				if (CurrentArticle is { } article && _mediaElementTracker.Start(localName, attributes, article, parser, ResolvedUrlString))
				{
					_namespaceElementDepth = 1;
				}
				return;
			}
			if (_mediaElementTracker.ContainerDepth > 0)
			{
				_namespaceElementDepth = 1;
				return;
			}
			if (_parsingArticle && ns.Prefix is not null)
			{
				// Skip namespaced elements and their subtrees (Shopify's nested <title>Default Title</title>).
				_namespaceElementDepth = 1;
				return;
			}
			if (localName.SequenceEqual("entry"u8))
			{
				_parsingArticle = true;
				_items.Add(new RssItem());
				_mediaElementTracker.Reset();
				return;
			}
			if (localName.SequenceEqual("author"u8))
			{
				_parsingAuthor = true;
				_currentAuthor = new AtomAuthor();
				return;
			}
			if (localName.SequenceEqual("source"u8))
			{
				_parsingSource = true;
				return;
			}
			var isContent = localName.SequenceEqual("content"u8);
			if (_parsingArticle && (isContent || localName.SequenceEqual("summary"u8)))
			{
				if (isContent && CurrentArticle is { } article)
				{
					article.Language = CurrentAttributes["xml:lang"];
				}
				if (CurrentAttributes["type"] == "xhtml")
				{
					parser.CaptureRawInnerContent();
					return;
				}
			}
			if (!_parsingArticle && localName.SequenceEqual("link"u8))
			{
				AddHomePageLink();
				return;
			}
			if (localName.SequenceEqual("feed"u8))
			{
				_language = CurrentAttributes["xml:lang"];
			}
			parser.BeginStoringCharacters();
		}

		public void DidCaptureRawInnerContent(XmlSaxParser parser, ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> localName, XmlNamespace ns)
		{
			if (CurrentArticle is not { } article)
			{
				return;
			}
			// The content element is still on the stacks, so EffectiveBaseUrl includes its own xml:base.
			var html = EffectiveBaseUrl is { } baseUrl
				? HtmlRelativeUrlResolver.ResolvingRelativeUrls(bytes, baseUrl.ToString())
				: System.Text.Encoding.UTF8.GetString(bytes);
			if (localName.SequenceEqual("content"u8))
			{
				article.Body = html;
			}
			else if (localName.SequenceEqual("summary"u8))
			{
				article.Summary = html;
			}
		}

		public void DidEndElement(XmlSaxParser parser, ReadOnlySpan<byte> localName, XmlNamespace ns)
		{
			try
			{
				HandleEnd(parser, localName, ns);
			}
			finally
			{
				if (_attributesStack.Count > 0)
				{
					_attributesStack.RemoveAt(_attributesStack.Count - 1);
				}
				if (_baseUrlStack.Count > 0)
				{
					_baseUrlStack.RemoveAt(_baseUrlStack.Count - 1);
				}
			}
		}

		private void HandleEnd(XmlSaxParser parser, ReadOnlySpan<byte> localName, XmlNamespace ns)
		{
			if (localName.SequenceEqual("feed"u8))
			{
				_endFeedFound = true;
				return;
			}
			if (_endFeedFound)
			{
				return;
			}
			if (_namespaceElementDepth > 0)
			{
				_namespaceElementDepth--;
				return;
			}
			if (_parsingArticle && ns.IsMediaRss)
			{
				if (CurrentArticle is { } article)
				{
					_mediaElementTracker.End(localName, article, parser);
				}
				return;
			}
			if (_parsingAuthor)
			{
				HandleAuthorElementEnd(parser, localName);
				return;
			}
			if (localName.SequenceEqual("entry"u8))
			{
				_parsingArticle = false;
				return;
			}
			if (_parsingArticle && !_parsingSource)
			{
				HandleArticleElementEnd(parser, localName, ns);
				return;
			}
			if (localName.SequenceEqual("source"u8))
			{
				_parsingSource = false;
				return;
			}
			if (!_parsingArticle && !_parsingSource)
			{
				HandleFeedElementEnd(parser, localName, ns);
			}
		}

		private void HandleArticleElementEnd(XmlSaxParser parser, ReadOnlySpan<byte> localName, XmlNamespace ns)
		{
			if (CurrentArticle is not { } article || ns.Prefix is not null)
			{
				return;
			}
			if (localName.SequenceEqual("id"u8))
			{
				article.Guid = parser.CurrentStringWithTrimmedWhitespace;
			}
			else if (localName.SequenceEqual("title"u8))
			{
				article.Title = parser.CurrentStringWithTrimmedWhitespace;
			}
			else if (localName.SequenceEqual("content"u8))
			{
				// Only overwrite with characters: raw XHTML may already have been captured.
				if (parser.CurrentStringWithTrimmedWhitespace is { } s)
				{
					article.Body = ResolvedHtmlBody(s);
				}
			}
			else if (localName.SequenceEqual("summary"u8))
			{
				if (parser.CurrentStringWithTrimmedWhitespace is { } s)
				{
					article.Summary = ResolvedHtmlBody(s);
				}
			}
			else if (localName.SequenceEqual("link"u8))
			{
				HandleArticleLink(article);
			}
			else if (localName.SequenceEqual("published"u8))
			{
				article.DatePublished = DateFromParser(parser);
			}
			else if (localName.SequenceEqual("updated"u8))
			{
				article.DateModified = DateFromParser(parser);
			}
			else if (localName.SequenceEqual("issued"u8))
			{
				article.DatePublished ??= DateFromParser(parser);
			}
			else if (localName.SequenceEqual("modified"u8))
			{
				article.DateModified ??= DateFromParser(parser);
			}
		}

		private void HandleArticleLink(RssItem article)
		{
			var attributes = CurrentAttributes;
			if (attributes["href"] is not { Length: > 0 } href || ResolvedUrlString(href) is not { } resolved)
			{
				return;
			}
			var rel = attributes["rel"] ?? "alternate";
			if (rel == "enclosure")
			{
				var length = MediaRss.StrictInt(attributes["length"]) ?? 0;
				if (ParsedAttachment.Create(resolved, attributes["type"], attributes["title"], length > 0 ? length : null) is { } attachment)
				{
					article.Attachments.Add(attachment);
				}
				return;
			}
			if (_isDaringFireball)
			{
				if (resolved.StartsWith("https://daringfireball.net/"))
				{
					if (string.IsNullOrEmpty(article.Permalink))
					{
						article.Permalink = resolved;
					}
				}
				else if (string.IsNullOrEmpty(article.Link))
				{
					article.Link = resolved;
				}
				return;
			}
			if (rel == "related" && string.IsNullOrEmpty(article.Link))
			{
				article.Link = resolved;
			}
			if (rel == "alternate" && string.IsNullOrEmpty(article.Permalink))
			{
				article.Permalink = resolved;
			}
		}

		private void HandleAuthorElementEnd(XmlSaxParser parser, ReadOnlySpan<byte> localName)
		{
			if (localName.SequenceEqual("author"u8))
			{
				_parsingAuthor = false;
				if (_currentAuthor?.ToParsedAuthor() is { } author)
				{
					if (_parsingArticle)
					{
						CurrentArticle?.Authors.Add(author);
					}
					else
					{
						_rootAuthor ??= author;
					}
				}
				_currentAuthor = null;
				return;
			}
			if (_currentAuthor is not { } current)
			{
				return;
			}
			if (localName.SequenceEqual("name"u8))
			{
				current.Name = parser.CurrentStringWithTrimmedWhitespace;
			}
			else if (localName.SequenceEqual("email"u8))
			{
				current.EmailAddress = parser.CurrentStringWithTrimmedWhitespace;
			}
			else if (localName.SequenceEqual("uri"u8) && parser.CurrentStringWithTrimmedWhitespace is { } uri)
			{
				current.Url = ResolvedUrlString(uri) ?? uri;
			}
		}

		private void HandleFeedElementEnd(XmlSaxParser parser, ReadOnlySpan<byte> localName, XmlNamespace ns)
		{
			if (ns.Prefix is not null)
			{
				return;
			}
			if (localName.SequenceEqual("title"u8))
			{
				if (string.IsNullOrEmpty(_feedTitle))
				{
					_feedTitle = parser.CurrentStringWithTrimmedWhitespace;
				}
			}
			else if (localName.SequenceEqual("icon"u8))
			{
				if (parser.CurrentStringWithTrimmedWhitespace is { Length: > 0 } s)
				{
					_iconUrl = ResolvedUrlString(s) ?? s;
				}
			}
			else if (localName.SequenceEqual("logo"u8))
			{
				if (_logoUrl is null && parser.CurrentStringWithTrimmedWhitespace is { Length: > 0 } s)
				{
					_logoUrl = ResolvedUrlString(s) ?? s;
				}
			}
		}

		private void AddHomePageLink()
		{
			if (!string.IsNullOrEmpty(_homePageUrl))
			{
				return;
			}
			var attributes = CurrentAttributes;
			if (attributes["href"] is not { Length: > 0 } rawLink)
			{
				return;
			}
			// rel="alternate" is the default.
			if (attributes["rel"] is null or "alternate")
			{
				_homePageUrl = ResolvedUrlString(rawLink);
			}
		}

		private static DateTimeOffset? DateFromParser(XmlSaxParser parser) =>
			parser.CurrentCharacters is { } bytes ? DateParser.Parse(bytes) : null;

		// RFC 4287: xml:base applies to its element and below, and a relative xml:base resolves against the
		// nearest enclosing base, or the feed URL when there is none.
		private void PushBaseUrl(XmlAttributes attributes)
		{
			var inherited = EffectiveBaseUrl;
			if (attributes.IsEmpty || attributes["xml:base"] is not { Length: > 0 } xmlBase)
			{
				_baseUrlStack.Add(inherited);
				return;
			}
			var outerBase = inherited ?? FoundationUrl.Parse(feedUrl);
			var resolved = FoundationUrl.Parse(FoundationUrl.Resolve(xmlBase, outerBase));
			// A garbage or non-http base (tag:, urn:) would poison resolution: inherit instead.
			_baseUrlStack.Add(resolved?.Scheme?.ToLowerInvariant() is "http" or "https" ? resolved : inherited);
		}

		/// <summary>
		/// Rewrite relative URLs in an escaped-HTML body when an xml:base is in effect. RFC 4287 allows a
		/// MIME type as well as the "html" keyword. Text content is never rewritten.
		/// </summary>
		private string ResolvedHtmlBody(string html)
		{
			if (CurrentAttributes["type"] is not ("html" or "text/html") || EffectiveBaseUrl is not { } baseUrl)
			{
				return html;
			}
			return HtmlRelativeUrlResolver.ResolvingRelativeUrls(html, baseUrl.ToString());
		}

		private string? ResolvedUrlString(string s)
		{
			if (IsValidUrlString(s))
			{
				return s;
			}
			if (EffectiveBaseUrl is { } baseUrl && FoundationUrl.Resolve(s, baseUrl) is { } againstBase && IsValidUrlString(againstBase))
			{
				return againstBase;
			}
			var fallbackBase = string.IsNullOrEmpty(_homePageUrl) ? feedUrl : _homePageUrl;
			if (string.IsNullOrEmpty(fallbackBase) || FoundationUrl.Parse(fallbackBase) is not { } fallback)
			{
				return null;
			}
			var resolved = FoundationUrl.Resolve(s, fallback);
			return !string.IsNullOrEmpty(resolved) && IsValidUrlString(resolved) ? resolved : null;
		}

		private static bool IsValidUrlString(string s) =>
			s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
	}

	private sealed class AtomAuthor
	{
		public string? Name;
		public string? EmailAddress;
		public string? Url;

		public ParsedAuthor? ToParsedAuthor() =>
			Name is null && EmailAddress is null && Url is null ? null : new ParsedAuthor(Name, Url, null, EmailAddress);
	}
}
