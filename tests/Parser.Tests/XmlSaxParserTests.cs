using System.Text;
using WinNewsReader.Parser.Xml;

namespace WinNewsReader.Parser.Tests;

public class XmlSaxParserTests
{
	private abstract record Event;

	/// <summary><paramref name="Attributes"/> is <c>name=value</c> pairs sorted and joined with <c>;</c>, so records compare by value.</summary>
	private sealed record Start(string Name, string? Prefix = null, string? Uri = null, string Attributes = "") : Event;

	private sealed record End(string Name, string? Prefix = null, string? Uri = null) : Event;

	private sealed record Characters(string Text) : Event;

	private sealed record Done : Event;

	[Fact]
	public void SingleElement() => Assert.Equal([new Start("root"), new End("root"), new Done()], Parse("<root/>"));

	[Fact]
	public void ElementWithContent() =>
		Assert.Equal([new Start("root"), new Characters("hello"), new End("root"), new Done()], Parse("<root>hello</root>"));

	[Fact]
	public void NestedElements() =>
		Assert.Equal(
			[new Start("a"), new Start("b"), new Characters("x"), new End("b"), new Start("c"), new End("c"), new End("a"), new Done()],
			Parse("<a><b>x</b><c/></a>"));

	[Fact]
	public void Attributes() =>
		Assert.Equal(
			[new Start("a", Attributes: "href=http://example.com;title=X"), new End("a"), new Done()],
			Parse("<a href=\"http://example.com\" title='X'/>"));

	[Fact]
	public void NamespacesResolveUri()
	{
		var events = Parse("<a xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>foo</dc:title></a>");
		Assert.Equal(6, events.Count);
		Assert.Equal(new Start("title", "dc", "http://purl.org/dc/elements/1.1/"), events[1]);
	}

	[Fact]
	public void DefaultNamespace() =>
		Assert.Equal(new Start("child", null, "http://example.com/ns"), Parse("<root xmlns=\"http://example.com/ns\"><child/></root>")[1]);

	[Fact]
	public void BuiltInXmlPrefix() =>
		// xml:lang and xml:base don't need an xmlns declaration.
		Assert.Equal("xml:lang=en", ((Start)Parse("<a xml:lang=\"en\"><b xml:base=\"http://x.com\"/></a>")[0]).Attributes);

	[Fact]
	public void NamespacePrefixInheritedFromOuterScope()
	{
		var tag = Parse("<a xmlns:x=\"http://x.com\"><b><x:tag>ok</x:tag></b></a>").OfType<Start>().Single(start => start.Name == "tag");
		Assert.Equal("x", tag.Prefix);
		Assert.Equal("http://x.com", tag.Uri);
	}

	[Fact]
	public void XmlnsAttributeNotExposed() =>
		Assert.Equal("href=http://x", ((Start)Parse("<a xmlns=\"foo\" href=\"http://x\"/>")[0]).Attributes);

	[Theory]
	[InlineData("<a>&amp;&lt;&gt;&quot;&apos;</a>", "&<>\"'")]
	[InlineData("<a>&#8212;</a>", "—")]
	[InlineData("<a>&#x2014;</a>", "—")]
	[InlineData("<a>&#X2014;</a>", "—")]
	// Without a DTD, as in libxml2, HTML and unknown named entities stay literal.
	[InlineData("<a>&nbsp;&copy;&mdash;</a>", "&nbsp;&copy;&mdash;")]
	[InlineData("<a>&foo;&bar</a>", "&foo;&bar")]
	// CDATA is raw: no entity substitution, and lone `]` or `]]` are fine.
	[InlineData("<a><![CDATA[&amp;<b>&nbsp;</b>]]></a>", "&amp;<b>&nbsp;</b>")]
	[InlineData("<a><![CDATA[array[0] and [1]]==]]></a>", "array[0] and [1]]==")]
	[InlineData("<a><![CDATA[&#8212;]]></a>", "&#8212;")]
	[InlineData("<a>Fish & Chips</a>", "Fish & Chips")]
	public void CharacterContent(string xml, string expected) => Assert.Equal(new Characters(expected), Parse(xml)[1]);

	[Fact]
	public void EntitiesInAttributes() => Assert.Equal("href=x&y", ((Start)Parse("<a href=\"x&amp;y\"/>")[0]).Attributes);

	[Fact]
	public void StrayLessThan()
	{
		// `<` followed by a non-name character is literal text.
		var content = string.Concat(Parse("<a>a < b</a>").OfType<Characters>().Select(c => c.Text));
		Assert.Contains("<", content);
		Assert.Contains("a ", content);
		Assert.Contains("b", content);
	}

	[Fact]
	public void UnclosedTagsEndAtEof()
	{
		var ends = Parse("<a><b>hello").OfType<End>().Select(end => end.Name).ToList();
		Assert.Contains("a", ends);
		Assert.Contains("b", ends);
	}

	[Fact]
	public void MismatchedEndTagRecovers() => Assert.True(Parse("<a><b></a></b>").OfType<End>().Count() >= 2);

	[Theory]
	[InlineData("<a><!-- ignore me --><b/></a>")]
	[InlineData("<a><?target data?><b/></a>")]
	public void CommentsAndProcessingInstructionsIgnored(string xml) =>
		Assert.Equal(["a", "b"], Parse(xml).OfType<Start>().Select(start => start.Name));

	[Theory]
	[InlineData("<!DOCTYPE html [\n<!ENTITY foo \"bar\">\n]>\n<a/>")]
	[InlineData("<?xml version=\"1.0\" encoding=\"UTF-8\"?><a/>")]
	public void DoctypeAndXmlDeclarationIgnored(string xml) => Assert.Single(Parse(xml).OfType<Start>());

	[Fact]
	public void ByteOrderMarks()
	{
		Assert.Equal(new Characters("hello"), ParseBytes([0xEF, 0xBB, 0xBF, .. "<a>hello</a>"u8])[1]);
		Assert.Equal(new Characters("hello"), ParseBytes([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("<a>hello</a>")])[1]);
		Assert.Equal(new Characters("hello"), ParseBytes([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("<a>hello</a>")])[1]);
	}

	[Fact]
	public void DeclaredEncodings()
	{
		Assert.Equal(new Characters("café"), ParseBytes([.. "<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?><a>caf"u8, 0xE9, .. "</a>"u8])[1]);
		Assert.Equal(
			new Characters("“Hi”"),
			ParseBytes([.. "<?xml version=\"1.0\" encoding=\"windows-1252\"?><a>"u8, 0x93, .. "Hi"u8, 0x94, .. "</a>"u8])[1]);
	}

	[Fact]
	public void BeginStoringCharacters()
	{
		var testDelegate = new TestDelegate();
		testDelegate.OnStart = (parser, name, _) =>
		{
			if (name == "title")
			{
				parser.BeginStoringCharacters();
			}
		};
		testDelegate.OnEnd = (parser, name) =>
		{
			if (name == "title")
			{
				testDelegate.Accumulated = parser.CurrentStringWithTrimmedWhitespace;
			}
		};
		new XmlSaxParser(testDelegate).Parse("<root><title>  Hello  </title></root>"u8.ToArray());
		Assert.Equal("Hello", testDelegate.Accumulated);
	}

	[Fact]
	public void CaptureRawInnerContent()
	{
		var testDelegate = Capturing((name, attributes) => name == "content" && attributes["type"] == "xhtml");
		new XmlSaxParser(testDelegate).Parse("""<root><content type="xhtml"><div>hello <em>world</em>!</div></content></root>"""u8.ToArray());
		Assert.Equal(["root", "content"], testDelegate.Events.OfType<Start>().Select(start => start.Name));
		Assert.Equal("<div>hello <em>world</em>!</div>", testDelegate.Captured);
	}

	[Theory]
	[InlineData("<root><a><a>x</a>y<a>z</a></a></root>", "a", "<a>x</a>y<a>z</a>")]
	// Raw bytes: entities aren't expanded.
	[InlineData("<root><body>a &amp; b &lt; c</body></root>", "body", "a &amp; b &lt; c")]
	[InlineData("<root><body></body></root>", "body", "")]
	public void CaptureRawInnerContentCases(string xml, string element, string expected)
	{
		TestDelegate? testDelegate = null;
		testDelegate = Capturing((name, _) => name == element && testDelegate!.Captured is null);
		new XmlSaxParser(testDelegate).Parse(Encoding.UTF8.GetBytes(xml));
		Assert.Equal(expected, testDelegate.Captured);
	}

	[Fact]
	public void NamespaceConvenienceIsDublinCore()
	{
		var testDelegate = new TestDelegate();
		new XmlSaxParser(testDelegate).Parse("""<rss xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:creator>Ana</dc:creator></rss>"""u8.ToArray());
		Assert.True(testDelegate.StartNamespaces[1].IsDublinCore);
	}

	[Fact]
	public void NamespaceConvenienceIsAtom()
	{
		var testDelegate = new TestDelegate();
		new XmlSaxParser(testDelegate).Parse("<feed xmlns=\"http://www.w3.org/2005/Atom\"><entry/></feed>"u8.ToArray());
		Assert.Equal(2, testDelegate.StartNamespaces.Count(ns => ns.IsAtom));
	}

	[Fact]
	public void SelfClosingFiresEndEvent() => Assert.Single(Parse("<a/>").OfType<End>());

	private static List<Event> Parse(string xml) => ParseBytes(Encoding.UTF8.GetBytes(xml));

	private static List<Event> ParseBytes(byte[] bytes)
	{
		var testDelegate = new TestDelegate();
		new XmlSaxParser(testDelegate).Parse(bytes);
		return testDelegate.Events;
	}

	private static TestDelegate Capturing(Func<string, XmlAttributes, bool> shouldCapture)
	{
		var testDelegate = new TestDelegate();
		testDelegate.OnStart = (parser, name, attributes) =>
		{
			if (shouldCapture(name, attributes))
			{
				parser.CaptureRawInnerContent();
			}
		};
		return testDelegate;
	}

	private sealed class TestDelegate : IXmlSaxParserDelegate
	{
		public List<Event> Events { get; } = [];

		public List<XmlNamespace> StartNamespaces { get; } = [];

		public Action<XmlSaxParser, string, XmlAttributes>? OnStart { get; set; }

		public Action<XmlSaxParser, string>? OnEnd { get; set; }

		public string? Accumulated { get; set; }

		public string? Captured { get; private set; }

		public void DidStartElement(XmlSaxParser parser, ReadOnlySpan<byte> localName, XmlNamespace ns, XmlAttributes attributes)
		{
			var name = Encoding.UTF8.GetString(localName);
			var pairs = attributes.ToDictionary().Select(pair => $"{pair.Key}={pair.Value}").Order(StringComparer.Ordinal);
			Events.Add(new Start(name, ns.Prefix, ns.Uri, string.Join(";", pairs)));
			StartNamespaces.Add(ns);
			OnStart?.Invoke(parser, name, attributes);
		}

		public void DidEndElement(XmlSaxParser parser, ReadOnlySpan<byte> localName, XmlNamespace ns)
		{
			var name = Encoding.UTF8.GetString(localName);
			Events.Add(new End(name, ns.Prefix, ns.Uri));
			OnEnd?.Invoke(parser, name);
		}

		public void DidFindCharacters(XmlSaxParser parser, ReadOnlySpan<byte> bytes)
		{
			// Adjacent character runs are combined, to keep assertions simple.
			var text = Encoding.UTF8.GetString(bytes);
			if (Events is [.., Characters last])
			{
				Events[^1] = new Characters(last.Text + text);
			}
			else
			{
				Events.Add(new Characters(text));
			}
		}

		public void DidCaptureRawInnerContent(XmlSaxParser parser, ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> localName, XmlNamespace ns) =>
			Captured = Encoding.UTF8.GetString(bytes);

		public void DidEnd(XmlSaxParser parser) => Events.Add(new Done());
	}
}
