using System.Text;
using WinNewsReader.Parser.Xml;

namespace WinNewsReader.Parser.Tests;

/// <summary>
/// NetNewsWire's XMLASCIITests. Its byte-constant and <c>ArraySlice.equals</c> tests have no counterpart
/// here: C# uses char literals and <c>SequenceEqual</c>.
/// </summary>
public class XmlAsciiTests
{
	[Theory]
	[InlineData((byte)' ')]
	[InlineData((byte)'\t')]
	[InlineData((byte)'\n')]
	[InlineData((byte)'\r')]
	public void WhitespaceIsRecognized(byte b) => Assert.True(AsciiBytes.IsWhitespace(b));

	[Theory]
	[InlineData((byte)'A')]
	[InlineData((byte)'0')]
	[InlineData((byte)'_')]
	[InlineData((byte)'<')]
	[InlineData(0x00)]
	[InlineData(0x0B)]
	[InlineData(0x0C)]
	[InlineData(0xFF)]
	public void NonWhitespaceIsRejected(byte b) => Assert.False(AsciiBytes.IsWhitespace(b));

	[Fact]
	public void DigitsAndLettersRecognized()
	{
		Assert.All(Bytes('0', '9'), b => Assert.True(AsciiBytes.IsDigit(b)));
		Assert.All(Bytes('A', 'Z').Concat(Bytes('a', 'z')), b => Assert.True(AsciiBytes.IsLetter(b)));
	}

	[Theory]
	[InlineData((byte)'A')]
	[InlineData((byte)'a')]
	[InlineData((byte)'/')]
	[InlineData((byte)':')]
	[InlineData((byte)' ')]
	[InlineData(0x00)]
	[InlineData(0xFF)]
	public void NonDigitIsRejected(byte b) => Assert.False(AsciiBytes.IsDigit(b));

	[Theory]
	[InlineData((byte)'0')]
	[InlineData((byte)'9')]
	[InlineData((byte)'@')]
	[InlineData((byte)'[')]
	[InlineData((byte)'`')]
	[InlineData((byte)'{')]
	[InlineData((byte)'-')]
	[InlineData(0x00)]
	[InlineData(0xFF)]
	public void NonLetterIsRejected(byte b) => Assert.False(AsciiBytes.IsLetter(b));

	[Fact]
	public void HexValueMatchesForEveryByte()
	{
		// Also guards against false positives from the `| 0x20` case-fold trick.
		for (var b = 0; b <= 0xFF; b++)
		{
			var expected = b switch
			{
				>= '0' and <= '9' => b - '0',
				>= 'A' and <= 'F' => b - 'A' + 10,
				>= 'a' and <= 'f' => b - 'a' + 10,
				_ => -1,
			};
			Assert.Equal(expected, AsciiBytes.HexValue((byte)b));
		}
	}

	[Fact]
	public void LowercasingFoldsOnlyLetters()
	{
		Assert.Equal(Bytes('a', 'z'), Bytes('A', 'Z').Select(AsciiBytes.ToLower));
		Assert.Equal(Bytes('a', 'z'), Bytes('a', 'z').Select(AsciiBytes.ToLower));
		byte[] nonLetters = [(byte)'0', (byte)'9', (byte)'@', (byte)'[', (byte)'`', (byte)'{', (byte)'/', (byte)'-', (byte)'.', 0x00, 0x7F, 0xFF];
		Assert.Equal(nonLetters, nonLetters.Select(AsciiBytes.ToLower));
	}

	[Fact]
	public void XmlNameStartAndChar()
	{
		Assert.All("azAZ_:"u8.ToArray(), b => Assert.True(AsciiBytes.IsXmlNameStart(b)));
		Assert.All("09-. </\0"u8.ToArray(), b => Assert.False(AsciiBytes.IsXmlNameStart(b)));
		Assert.All("aZ_:09-."u8.ToArray(), b => Assert.True(AsciiBytes.IsXmlNameChar(b)));
		Assert.All(" <>=/&\"\0"u8.ToArray(), b => Assert.False(AsciiBytes.IsXmlNameChar(b)));
	}

	[Theory]
	[InlineData("xmlns", "xmlns", true)]
	[InlineData("XMLNS", "xmlns", true)]
	[InlineData("XmLnS", "xmlns", true)]
	[InlineData("xmlnz", "xmlns", false)]
	[InlineData("xml", "xmlns", false)]
	[InlineData("utf-8", "utf-8", true)]
	[InlineData("utf-8", "utf_8", false)]
	[InlineData("", "", true)]
	public void EqualsIgnoreCase(string bytes, string lowercaseLiteral, bool expected) =>
		Assert.Equal(expected, AsciiBytes.EqualsIgnoreCase(Encoding.ASCII.GetBytes(bytes), Encoding.ASCII.GetBytes(lowercaseLiteral)));

	private static byte[] Bytes(char first, char last) => [.. Enumerable.Range(first, last - first + 1).Select(i => (byte)i)];
}

/// <summary>NetNewsWire's XMLAttributesTests, minus <c>forEach</c>, which has no counterpart here.</summary>
public class XmlAttributesTests
{
	private const string XmlUri = "http://www.w3.org/XML/1998/namespace";

	private static XmlAttributes.Attribute Attribute(string localName, string value, string? prefix = null, string? uri = null) =>
		new(new XmlNamespace(prefix, uri), localName, value);

	private static XmlAttributes Make(params XmlAttributes.Attribute[] attributes) => new([.. attributes]);

	[Fact]
	public void CountAndIsEmpty()
	{
		Assert.Equal(0, XmlAttributes.Empty.Count);
		Assert.True(XmlAttributes.Empty.IsEmpty);
		var attributes = Make(Attribute("href", "/"));
		Assert.Equal(1, attributes.Count);
		Assert.False(attributes.IsEmpty);
	}

	[Fact]
	public void QualifiedNames()
	{
		Assert.Equal("href", Attribute("href", "/").QualifiedName);
		Assert.Equal("xml:lang", Attribute("lang", "en", "xml", XmlUri).QualifiedName);
	}

	[Fact]
	public void IndexerIsCaseSensitiveQualifiedLookup()
	{
		var attributes = Make(Attribute("href", "/foo"));
		Assert.Equal("/foo", attributes["href"]);
		Assert.Null(attributes["HREF"]);
		Assert.Null(attributes["Href"]);
		Assert.Null(attributes["type"]);
		Assert.Null(Make(Attribute("lang", "en"))["xml:lang"]);

		var prefixed = Make(Attribute("lang", "en", "xml", XmlUri));
		Assert.Equal("en", prefixed["xml:lang"]);
		Assert.Null(prefixed["lang"]);
		Assert.Null(prefixed["foo:lang"]);
	}

	[Fact]
	public void ValueIgnoringCase()
	{
		var attributes = Make(Attribute("isPermaLink", "false"));
		Assert.Equal("false", attributes.ValueIgnoringCase("ispermalink"));
		Assert.Equal("false", attributes.ValueIgnoringCase("ISPERMALINK"));
		Assert.Equal("false", attributes.ValueIgnoringCase("IsPermaLink"));
		Assert.Equal("en", Make(Attribute("Lang", "en", "XML", XmlUri)).ValueIgnoringCase("xml:lang"));

		var dataId = Make(Attribute("data-id", "42"));
		Assert.Equal("42", dataId.ValueIgnoringCase("data-id"));
		Assert.Null(dataId.ValueIgnoringCase("data_id"));
	}

	[Fact]
	public void ToDictionaryUsesQualifiedKeysAndLastWins()
	{
		var dictionary = Make(Attribute("href", "/foo"), Attribute("lang", "en", "xml", XmlUri)).ToDictionary();
		Assert.Equal(2, dictionary.Count);
		Assert.Equal("/foo", dictionary["href"]);
		Assert.Equal("en", dictionary["xml:lang"]);

		Assert.Empty(XmlAttributes.Empty.ToDictionary());
		Assert.Equal("second", Make(Attribute("x", "first"), Attribute("x", "second")).ToDictionary()["x"]);
	}

	[Fact]
	public void ValuesRoundTripUnicode() =>
		Assert.Equal("Café — résumé 日本語", Make(Attribute("title", "Café — résumé 日本語"))["title"]);
}

public class XmlEncodingTests
{
	static XmlEncodingTests()
	{
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
	}

	private static byte[] Declaration(string encodingName, bool singleQuoted = false)
	{
		var q = singleQuoted ? "'" : "\"";
		return Encoding.ASCII.GetBytes($"<?xml version={q}1.0{q} encoding={q}{encodingName}{q}?>");
	}

	/// <summary><c>&lt;a&gt;é&lt;/a&gt;</c> with é as the single Latin-1 byte 0xE9.</summary>
	private static readonly byte[] Latin1Tail = [.. "<a>"u8, 0xE9, .. "</a>"u8];

	private static string ToUtf8String(byte[] input) => Encoding.UTF8.GetString(XmlEncoding.ToUtf8(input));

	[Theory]
	[InlineData("<root>hello</root>")]
	[InlineData("")]
	[InlineData("X")]
	[InlineData("XY")]
	[InlineData("<?xml version=\"1.0\" encoding=\"xyz-9000\"?><root>hello</root>")]
	[InlineData("<?xml version=\"1.0\"?><root>hello</root>")]
	public void ReturnedUnchanged(string text)
	{
		var input = Encoding.UTF8.GetBytes(text);
		Assert.Equal(input, XmlEncoding.ToUtf8(input));
	}

	[Fact]
	public void Utf8BomStripped() => Assert.Equal("<a/>"u8.ToArray(), XmlEncoding.ToUtf8([0xEF, 0xBB, 0xBF, .. "<a/>"u8]));

	[Fact]
	public void Utf16BomsDecoded()
	{
		Assert.Equal("<a>é</a>", ToUtf8String([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("<a>é</a>")]));
		Assert.Equal("<a>é</a>", ToUtf8String([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("<a>é</a>")]));
	}

	[Theory]
	[InlineData("ISO-8859-1")]
	[InlineData("iso-8859-1")]
	[InlineData("Iso-8859-1")]
	[InlineData("iSO-8859-1")]
	[InlineData("latin1")]
	[InlineData("latin-1")]
	[InlineData("LATIN-1")]
	public void Latin1NamesDecode(string name) => Assert.Contains("<a>é</a>", ToUtf8String([.. Declaration(name), .. Latin1Tail]));

	[Theory]
	[InlineData("UTF-8")]
	[InlineData("utf-8")]
	[InlineData("utf8")]
	[InlineData("UTF8")]
	[InlineData("us-ascii")]
	[InlineData("ASCII")]
	public void Utf8AliasesPassThrough(string name)
	{
		byte[] input = [.. Declaration(name), .. "<a>hello</a>"u8];
		Assert.Equal(input, XmlEncoding.ToUtf8(input));
	}

	[Theory]
	[InlineData("windows-1252")]
	[InlineData("Windows-1252")]
	[InlineData("cp1252")]
	[InlineData("CP1252")]
	public void Windows1252AliasesDecodeSmartQuotes(string name) =>
		Assert.Contains("<a>“Hi”</a>", ToUtf8String([.. Declaration(name), .. "<a>"u8, 0x93, .. "Hi"u8, 0x94, .. "</a>"u8]));

	[Fact]
	public void SingleQuotedEncodingAttribute() =>
		Assert.Contains("<a>é</a>", ToUtf8String([.. Declaration("ISO-8859-1", singleQuoted: true), .. Latin1Tail]));

	[Fact]
	public void LeadingWhitespaceBeforeDeclaration() =>
		Assert.Contains("<a>é</a>", ToUtf8String([.. "  \n\t"u8, .. Declaration("ISO-8859-1"), .. Latin1Tail]));

	[Theory]
	[InlineData("日本語", 932, "shift_jis")]
	[InlineData("日本語", 932, "Shift-JIS")]
	[InlineData("日本語", 51932, "euc-jp")]
	[InlineData("Русский", 1251, "windows-1251")]
	[InlineData("Český", 28592, "iso-8859-2")]
	[InlineData("€100", 28605, "iso-8859-15")]
	[InlineData("繁體中文", 950, "big5")]
	[InlineData("한국어", 51949, "euc-kr")]
	[InlineData("Русский", 20866, "koi8-r")]
	[InlineData("简体中文", 936, "gbk")]
	public void RoundTrip(string text, int codePage, string declaredAs)
	{
		byte[] input = [.. Declaration(declaredAs), .. "<a>"u8, .. Encoding.GetEncoding(codePage).GetBytes(text), .. "</a>"u8];
		Assert.Contains($"<a>{text}</a>", ToUtf8String(input));
	}
}

public class XmlNamespaceContextTests
{
	private static XmlNamespaceContext WithScopes(params (string? Prefix, string Uri)[][] scopes)
	{
		var context = new XmlNamespaceContext();
		foreach (var scope in scopes)
		{
			context.PushScope([.. scope]);
		}
		return context;
	}

	[Fact]
	public void BuiltInsAlwaysResolveAndCannotBeShadowed()
	{
		var context = WithScopes([("dc", "http://purl.org/dc/elements/1.1/")], [(null, "http://www.w3.org/2005/Atom")], [("xml", "http://evil.example.com/")]);
		Assert.Equal("http://www.w3.org/XML/1998/namespace", context.Resolve("xml"));
		Assert.Equal("http://www.w3.org/2000/xmlns/", context.Resolve("xmlns"));
		Assert.Equal("http://www.w3.org/XML/1998/namespace", new XmlNamespaceContext().Resolve("xml"));
	}

	[Fact]
	public void DefaultNamespace()
	{
		Assert.Null(new XmlNamespaceContext().Resolve(null));
		Assert.Equal("http://www.w3.org/2005/Atom", WithScopes([(null, "http://www.w3.org/2005/Atom")]).Resolve(null));
	}

	[Fact]
	public void PrefixesResolveAfterPush()
	{
		Assert.Null(new XmlNamespaceContext().Resolve("nosuchprefix"));
		var context = WithScopes([
			(null, "http://www.w3.org/2005/Atom"),
			("dc", "http://purl.org/dc/elements/1.1/"),
			("content", "http://purl.org/rss/1.0/modules/content/"),
		]);
		Assert.Equal("http://www.w3.org/2005/Atom", context.Resolve(null));
		Assert.Equal("http://purl.org/dc/elements/1.1/", context.Resolve("dc"));
		Assert.Equal("http://purl.org/rss/1.0/modules/content/", context.Resolve("content"));
	}

	[Fact]
	public void InnerScopeShadowsOuterUntilPopped()
	{
		var context = WithScopes([("x", "urn:outer")]);
		Assert.Equal("urn:outer", context.Resolve("x"));
		context.PushScope([("x", "urn:inner"), ("y", "urn:y")]);
		Assert.Equal("urn:inner", context.Resolve("x"));
		Assert.Equal("urn:y", context.Resolve("y"));
		context.PopScope();
		Assert.Equal("urn:outer", context.Resolve("x"));
		Assert.Null(context.Resolve("y"));
		context.PopScope();
		Assert.Null(context.Resolve("x"));
	}

	[Fact]
	public void EmptyScopesStayBalanced()
	{
		var context = WithScopes([("x", "urn:outer")], []);
		Assert.Equal("urn:outer", context.Resolve("x"));
		context.PopScope();
		Assert.Equal("urn:outer", context.Resolve("x"));
		context.PopScope();
		Assert.Null(context.Resolve("x"));
		context.PushScope(null);
		context.PopScope();
		Assert.Null(context.Resolve("x"));
	}

	[Fact]
	public void PopRemovesEveryBindingInTheScope()
	{
		var context = WithScopes([("a", "urn:a"), ("b", "urn:b"), ("c", "urn:c")]);
		context.PopScope();
		Assert.Null(context.Resolve("a"));
		Assert.Null(context.Resolve("b"));
		Assert.Null(context.Resolve("c"));
	}

	[Fact]
	public void PopWithoutPushIsNoOpAndPushWorksAfter()
	{
		var context = new XmlNamespaceContext();
		context.PopScope();
		Assert.Null(context.Resolve("x"));
		context.PushScope([("x", "urn:first")]);
		context.PopScope();
		context.PushScope([("x", "urn:second")]);
		Assert.Equal("urn:second", context.Resolve("x"));
	}
}
