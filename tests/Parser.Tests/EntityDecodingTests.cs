using System.Text;
using WinNewsReader.Parser.Xml;

namespace WinNewsReader.Parser.Tests;

public class EntityDecodingTests
{
	[Fact]
	public void DecimalEntity39() =>
		// The single-quote bug from micro.blog.
		Assert.Equal("These are the times that try men's souls.", "These are the times that try men&#39;s souls.".DecodingHtmlEntities());

	[Theory]
	[InlineData("&#8230;", "…")]
	[InlineData("&#x2026;", "…")]
	[InlineData("&#039;", "'")]
	[InlineData("&#167;", "§")]
	[InlineData("&#XA3;", "£")]
	// The five predefined XML entities.
	[InlineData("&amp;", "&")]
	[InlineData("&lt;", "<")]
	[InlineData("&gt;", ">")]
	[InlineData("&quot;", "\"")]
	[InlineData("&apos;", "'")]
	// Zero-width joiner and non-joiner (NetNewsWire issue #3220).
	[InlineData("&zwj;", "‍")]
	[InlineData("&zwnj;", "‌")]
	[InlineData("a&zwj;b", "a‍b")]
	public void EntityDecodes(string input, string expected) => Assert.Equal(expected, input.DecodingHtmlEntities());

	[Theory]
	[InlineData("Tom &amp; Jerry", "Tom & Jerry")]
	[InlineData("&lt;script&gt;alert(1)&lt;/script&gt;", "<script>alert(1)</script>")]
	[InlineData("He said &quot;hi&quot;", "He said \"hi\"")]
	[InlineData("it&apos;s", "it's")]
	public void PredefinedXmlEntitiesInContext(string input, string expected) => Assert.Equal(expected, input.DecodingHtmlEntities());

	[Theory]
	// Unknown names.
	[InlineData("&foo;")]
	[InlineData("hello &bogus; world")]
	// No semicolon.
	[InlineData("&amp")]
	[InlineData("a&amp b")]
	// Empty reference and lonely ampersands.
	[InlineData("&;")]
	[InlineData("&")]
	[InlineData("a & b")]
	// Malformed numeric entities.
	[InlineData("&#;")]
	[InlineData("&#x;")]
	[InlineData("&#abc;")]
	[InlineData("&#xZZ;")]
	// Zero and out of range.
	[InlineData("&#0;")]
	[InlineData("&#x0;")]
	[InlineData("&#x110000;")]
	[InlineData("&#99999999;")]
	// No entities at all.
	[InlineData("Just some text with no entities at all.")]
	[InlineData("")]
	public void PassesThroughUnchanged(string input) => Assert.Equal(input, input.DecodingHtmlEntities());

	[Theory]
	[InlineData("&#128;", "€")]
	[InlineData("&#130;", "‚")]
	[InlineData("&#133;", "…")]
	[InlineData("&#134;", "†")]
	[InlineData("&#145;", "‘")]
	[InlineData("&#146;", "’")]
	[InlineData("&#147;", "“")]
	[InlineData("&#148;", "”")]
	[InlineData("&#150;", "–")]
	[InlineData("&#151;", "—")]
	[InlineData("&#153;", "™")]
	[InlineData("&#x80;", "€")]
	[InlineData("&#x96;", "–")]
	public void WindowsLatin1Remap(string input, string expected) => Assert.Equal(expected, input.DecodingHtmlEntities());

	[Theory]
	[InlineData("&amp;&lt;&gt;", "&<>")]
	[InlineData("&#8220;&#8221;", "“”")]
	[InlineData("One &amp; two: &#8216;three&#8217; — &#x2014; &mdash;.", "One & two: ‘three’ — — —.")]
	public void MultipleEntities(string input, string expected) => Assert.Equal(expected, input.DecodingHtmlEntities());

	[Theory]
	[InlineData("&#xab;")]
	[InlineData("&#xAB;")]
	[InlineData("&#xAb;")]
	[InlineData("&#xaB;")]
	public void HexEntityIsCaseInsensitiveForDigits(string input) => Assert.Equal("«", input.DecodingHtmlEntities());

	[Fact]
	public void UppercaseHexMarker() => Assert.Equal("…", "&#X2026;".DecodingHtmlEntities());

	[Theory]
	[InlineData("foo&amp;", "foo&")]
	[InlineData("foo&amp", "foo&amp")]
	[InlineData("foo&", "foo&")]
	[InlineData("foo&#", "foo&#")]
	[InlineData("foo&#x", "foo&#x")]
	public void EntityAtEndOfInputBoundary(string input, string expected) => Assert.Equal(expected, input.DecodingHtmlEntities());

	[Fact]
	public void FourByteUtf8EmojiDecodes()
	{
		Assert.Equal("😀", "&#x1F600;".DecodingHtmlEntities());
		Assert.Equal("😀", "&#128512;".DecodingHtmlEntities());
		Assert.Equal("👋", "&#x1F44B;".DecodingHtmlEntities());
		Assert.Equal([0xF0, 0x9F, 0x98, 0x80], Encoding.UTF8.GetBytes("&#x1F600;".DecodingHtmlEntities()));
	}
}

public class XmlEntitiesTests
{
	private static (string Text, int NextIndex) DecodeOne(string input, XmlEntities.Mode mode = XmlEntities.Mode.Html)
	{
		var (bytes, nextIndex) = XmlEntities.Decode(Encoding.UTF8.GetBytes(input), 0, mode);
		return (Encoding.UTF8.GetString(bytes), nextIndex);
	}

	private static void AssertDecodes(string input, string expected, XmlEntities.Mode mode = XmlEntities.Mode.Html)
	{
		var (text, nextIndex) = DecodeOne(input, mode);
		Assert.Equal(expected, text);
		Assert.Equal(Encoding.UTF8.GetByteCount(input), nextIndex);
	}

	/// <summary>Not an entity: the decoder returns a lone <c>&amp;</c> and moves one byte.</summary>
	private static void AssertRejected(string input, XmlEntities.Mode mode = XmlEntities.Mode.Html)
	{
		var (text, nextIndex) = DecodeOne(input, mode);
		Assert.Equal("&", text);
		Assert.Equal(1, nextIndex);
	}

	[Theory]
	[InlineData("&amp;", "&")]
	[InlineData("&lt;", "<")]
	[InlineData("&gt;", ">")]
	[InlineData("&quot;", "\"")]
	[InlineData("&apos;", "'")]
	public void PredefinedEntitiesExpandInBothModes(string input, string expected)
	{
		AssertDecodes(input, expected);
		AssertDecodes(input, expected, XmlEntities.Mode.XmlStrict);
	}

	[Theory]
	[InlineData("&mdash;")]
	[InlineData("&copy;")]
	[InlineData("&nbsp;")]
	public void HtmlNamedEntitiesLiteralInXmlStrict(string input) => AssertRejected(input, XmlEntities.Mode.XmlStrict);

	[Fact]
	public void NumericEntitiesExpandInXmlStrict()
	{
		Assert.Equal("’", DecodeOne("&#8217;", XmlEntities.Mode.XmlStrict).Text);
		Assert.Equal("A", DecodeOne("&#65;", XmlEntities.Mode.XmlStrict).Text);
	}

	[Theory]
	[InlineData("&#65;", "A")]
	[InlineData("&#8482;", "™")]
	[InlineData("&#8220;", "“")]
	[InlineData("&#9731;", "☃")]
	[InlineData("&#x41;", "A")]
	[InlineData("&#X41;", "A")]
	[InlineData("&#x2014;", "—")]
	[InlineData("&#xAD;", "­")]
	[InlineData("&#x1F600;", "\U0001F600")]
	public void NumericEntitiesDecode(string input, string expected) => AssertDecodes(input, expected);

	[Theory]
	[InlineData("&#0;")]
	[InlineData("&#12a;")]
	[InlineData("&#;")]
	[InlineData("&#x0;")]
	[InlineData("&#x;")]
	[InlineData("&#xZZ;")]
	// Surrogates.
	[InlineData("&#xD800;")]
	[InlineData("&#xDC00;")]
	[InlineData("&#xDFFF;")]
	[InlineData("&#55296;")]
	[InlineData("&#x110000;")]
	public void InvalidNumericEntitiesRejected(string input) => AssertRejected(input);

	[Theory]
	[InlineData("&#128;", "€")]
	[InlineData("&#133;", "…")]
	[InlineData("&#145;", "‘")]
	[InlineData("&#146;", "’")]
	[InlineData("&#147;", "“")]
	[InlineData("&#148;", "”")]
	[InlineData("&#150;", "–")]
	[InlineData("&#151;", "—")]
	[InlineData("&#153;", "™")]
	// The edges of the range aren't remapped.
	[InlineData("&#x7F;", "\u007F")]
	[InlineData("&#xA0;", " ")]
	public void Windows1252Remap(string input, string expected) => Assert.Equal(expected, DecodeOne(input).Text);

	[Theory]
	[InlineData("&copy;", "©")]
	[InlineData("&mdash;", "—")]
	[InlineData("&ndash;", "–")]
	[InlineData("&ldquo;", "“")]
	[InlineData("&rdquo;", "”")]
	[InlineData("&hellip;", "…")]
	[InlineData("&trade;", "™")]
	[InlineData("&eacute;", "é")]
	[InlineData("&nbsp;", " ")]
	[InlineData("&shy;", "­")]
	// Longest name, and case-sensitive distinct entries.
	[InlineData("&CounterClockwiseContourIntegral;", "∳")]
	[InlineData("&AElig;", "Æ")]
	public void HtmlNamedEntities(string input, string expected) => AssertDecodes(input, expected);

	[Theory]
	// Named entities are case-sensitive.
	[InlineData("&COPY;")]
	[InlineData("&notARealEntity;")]
	[InlineData("&amp")]
	[InlineData("&;")]
	// Whitespace, another `&` or `<` ends the scan before `;`.
	[InlineData("&amp ")]
	[InlineData("&amp\t")]
	[InlineData("&amp\n")]
	[InlineData("&amp&lt;")]
	[InlineData("&amp<")]
	public void NotEntitiesRejected(string input) => AssertRejected(input);

	[Fact]
	public void TooLongEntityIsRejected() => AssertRejected("&" + new string('x', 100) + ";");

	[Fact]
	public void DecodesAtNonZeroIndex()
	{
		var (bytes, nextIndex) = XmlEntities.Decode(Encoding.UTF8.GetBytes("Hello &amp; world"), 6, XmlEntities.Mode.Html);
		Assert.Equal("&", Encoding.UTF8.GetString(bytes));
		Assert.Equal(11, nextIndex);
	}

	[Fact]
	public void WindowsLatin1ExtensionTable()
	{
		Assert.Equal(32, XmlEntities.WindowsLatin1Extension.Length);
		Assert.Equal(0x20AC, XmlEntities.WindowsLatin1Extension[0]);
		Assert.Equal(0x2122, XmlEntities.WindowsLatin1Extension[0x99 - 0x80]);
	}
}
