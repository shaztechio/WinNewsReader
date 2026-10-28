using System.Text;

namespace WinNewsReader.Parser.Xml;

public interface IXmlSaxParserDelegate
{
	/// <summary>Called on each start tag. Self-closing tags also get a matching end.</summary>
	void DidStartElement(XmlSaxParser parser, ReadOnlySpan<byte> localName, XmlNamespace ns, XmlAttributes attributes) { }

	void DidEndElement(XmlSaxParser parser, ReadOnlySpan<byte> localName, XmlNamespace ns) { }

	/// <summary>Called for each run of character content, with entities expanded. Never empty.</summary>
	void DidFindCharacters(XmlSaxParser parser, ReadOnlySpan<byte> bytes) { }

	/// <summary>
	/// The raw inner bytes of an element, verbatim, after the delegate called
	/// <see cref="XmlSaxParser.CaptureRawInnerContent"/> from <see cref="DidStartElement"/>. No events are
	/// delivered for its children. The outer element's <see cref="DidEndElement"/> follows immediately.
	/// </summary>
	void DidCaptureRawInnerContent(XmlSaxParser parser, ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> localName, XmlNamespace ns) { }

	/// <summary>Called once at the end of the document, after any unclosed elements are closed.</summary>
	void DidEnd(XmlSaxParser parser) { }
}

/// <summary>
/// Namespace-aware SAX parser over <see cref="XmlScanner"/>. Liberal like libxml2's recovery mode:
/// mismatched end tags close intervening elements, and unclosed elements close at end of document.
/// </summary>
public sealed class XmlSaxParser
{
	private readonly IXmlSaxParserDelegate _delegate;
	private readonly XmlNamespaceContext _namespaceContext = new();
	private readonly List<(XmlNamespace Namespace, byte[] LocalName)> _elementStack = [];
	private readonly List<byte> _charactersBuffer = new(1024);
	private bool _storingCharacters;
	private bool _rawCaptureRequested;

	public XmlSaxParser(IXmlSaxParserDelegate parserDelegate)
	{
		_delegate = parserDelegate;
	}

	/// <summary>Request raw-inner-content capture for the element being started. Call only from DidStartElement.</summary>
	public void CaptureRawInnerContent() => _rawCaptureRequested = true;

	public void BeginStoringCharacters()
	{
		_storingCharacters = true;
		_charactersBuffer.Clear();
	}

	public void EndStoringCharacters()
	{
		_storingCharacters = false;
		_charactersBuffer.Clear();
	}

	/// <summary>Characters accumulated since <see cref="BeginStoringCharacters"/>, or null if not storing.</summary>
	public byte[]? CurrentCharacters => _storingCharacters ? [.. _charactersBuffer] : null;

	public string? CurrentStringWithTrimmedWhitespace
	{
		get
		{
			if (!_storingCharacters || _charactersBuffer.Count == 0)
			{
				return null;
			}
			// string.Trim's whitespace set matches Foundation's whitespacesAndNewlines.
			var trimmed = Encoding.UTF8.GetString([.. _charactersBuffer]).Trim();
			return trimmed.Length == 0 ? null : trimmed;
		}
	}

	public void Parse(byte[] bytes)
	{
		var utf8 = XmlEncoding.ToUtf8(bytes);
		var scanner = new XmlScanner(utf8);
		while (true)
		{
			var scanned = scanner.Next();
			switch (scanned.Kind)
			{
				case XmlScanner.EventKind.StartElement:
					// After a start event the scanner sits just past `>`: that's where raw capture begins.
					var innerStart = scanner.Position;
					var passThrough = HandleStart(utf8, scanned);
					if (passThrough is { } outer && !scanned.SelfClosing)
					{
						RunPassThrough(innerStart, outer.Namespace, outer.LocalName, scanner, utf8);
					}
					break;
				case XmlScanner.EventKind.EndElement:
					HandleEnd(utf8, scanned.Prefix, scanned.LocalName);
					break;
				case XmlScanner.EventKind.Characters:
					HandleCharacters(scanned.Characters!);
					break;
				case XmlScanner.EventKind.EndOfDocument:
					while (_elementStack.Count > 0)
					{
						var top = _elementStack[^1];
						_elementStack.RemoveAt(_elementStack.Count - 1);
						EmitEndElement(top.Namespace, top.LocalName);
					}
					_delegate.DidEnd(this);
					return;
			}
		}
	}

	private (XmlNamespace Namespace, byte[] LocalName)? HandleStart(byte[] utf8, XmlScanner.Event scanned)
	{
		var rawAttributes = scanned.Attributes;
		_namespaceContext.PushScope(ExtractNamespaceBindings(rawAttributes));

		var prefix = scanned.Prefix.IsNone ? null : Decode(utf8, scanned.Prefix);
		var elementNamespace = new XmlNamespace(prefix, _namespaceContext.Resolve(prefix));

		var attributes = XmlAttributes.Empty;
		if (rawAttributes is { Count: > 0 })
		{
			var visible = new List<XmlAttributes.Attribute>(rawAttributes.Count);
			foreach (var raw in rawAttributes)
			{
				if (IsXmlnsAttribute(raw))
				{
					continue;
				}
				// Unprefixed attributes have no namespace, per the spec.
				var attributePrefix = raw.Prefix is null ? null : Encoding.UTF8.GetString(raw.Prefix);
				var attributeUri = attributePrefix is null ? null : _namespaceContext.Resolve(attributePrefix);
				visible.Add(new XmlAttributes.Attribute(
					new XmlNamespace(attributePrefix, attributeUri),
					Encoding.UTF8.GetString(raw.LocalName),
					Encoding.UTF8.GetString(raw.Value)));
			}
			if (visible.Count > 0)
			{
				attributes = new XmlAttributes(visible);
			}
		}

		var localName = utf8[scanned.LocalName.Start..scanned.LocalName.End];
		// libxml clears accumulated characters on each new start tag.
		EndStoringCharacters();
		_rawCaptureRequested = false;
		_delegate.DidStartElement(this, localName, elementNamespace, attributes);
		var captureRequested = _rawCaptureRequested;
		_rawCaptureRequested = false;

		if (captureRequested && !scanned.SelfClosing)
		{
			// Pass-through owns the end tag, so the element isn't pushed.
			return (elementNamespace, localName);
		}
		if (scanned.SelfClosing)
		{
			EmitEndElement(elementNamespace, localName);
		}
		else
		{
			_elementStack.Add((elementNamespace, localName));
		}
		return null;
	}

	/// <summary>Drive the scanner to the matching end tag, then emit the captured bytes and the outer end.</summary>
	private void RunPassThrough(int innerStart, XmlNamespace outerNamespace, byte[] outerLocal, XmlScanner scanner, byte[] utf8)
	{
		var outerPrefix = outerNamespace.Prefix is null ? null : Encoding.UTF8.GetBytes(outerNamespace.Prefix);
		var depth = 0;
		while (true)
		{
			var eventStart = scanner.Position;
			var scanned = scanner.Next();
			switch (scanned.Kind)
			{
				case XmlScanner.EventKind.StartElement:
					if (!scanned.SelfClosing && IsSameName(scanned.Prefix, scanned.LocalName, utf8, outerLocal, outerPrefix))
					{
						depth++;
					}
					break;
				case XmlScanner.EventKind.EndElement:
					if (IsSameName(scanned.Prefix, scanned.LocalName, utf8, outerLocal, outerPrefix))
					{
						if (depth == 0)
						{
							EmitPassThroughEnd(utf8.AsSpan(innerStart, eventStart - innerStart), outerNamespace, outerLocal);
							return;
						}
						depth--;
					}
					break;
				case XmlScanner.EventKind.EndOfDocument:
					EmitPassThroughEnd(utf8.AsSpan(innerStart), outerNamespace, outerLocal);
					return;
			}
		}
	}

	private void EmitPassThroughEnd(ReadOnlySpan<byte> captured, XmlNamespace outerNamespace, byte[] outerLocal)
	{
		_delegate.DidCaptureRawInnerContent(this, captured, outerLocal, outerNamespace);
		_delegate.DidEndElement(this, outerLocal, outerNamespace);
		EndStoringCharacters();
		_namespaceContext.PopScope();
	}

	private static bool IsSameName(XmlScanner.Range prefix, XmlScanner.Range local, byte[] utf8, byte[] outerLocal, byte[]? outerPrefix)
	{
		if (!utf8.AsSpan(local.Start, local.Length).SequenceEqual(outerLocal))
		{
			return false;
		}
		if (!prefix.IsNone)
		{
			return outerPrefix is not null && utf8.AsSpan(prefix.Start, prefix.Length).SequenceEqual(outerPrefix);
		}
		return outerPrefix is null;
	}

	private void HandleEnd(byte[] utf8, XmlScanner.Range prefixRange, XmlScanner.Range localRange)
	{
		var incomingPrefix = prefixRange.IsNone ? null : Decode(utf8, prefixRange);
		var incomingLocal = utf8.AsSpan(localRange.Start, localRange.Length);
		var matchIndex = -1;
		for (var i = _elementStack.Count - 1; i >= 0; i--)
		{
			var entry = _elementStack[i];
			if (entry.Namespace.Prefix == incomingPrefix && incomingLocal.SequenceEqual(entry.LocalName))
			{
				matchIndex = i;
				break;
			}
		}
		if (matchIndex >= 0)
		{
			// Close intervening elements too.
			while (_elementStack.Count > matchIndex)
			{
				var top = _elementStack[^1];
				_elementStack.RemoveAt(_elementStack.Count - 1);
				EmitEndElement(top.Namespace, top.LocalName);
			}
			return;
		}
		// Orphan end tag: report it. No scope was pushed for it, so nothing to pop.
		var ns = new XmlNamespace(incomingPrefix, _namespaceContext.Resolve(incomingPrefix));
		EndStoringCharacters();
		_delegate.DidEndElement(this, incomingLocal, ns);
	}

	private void EmitEndElement(XmlNamespace ns, byte[] localName)
	{
		_delegate.DidEndElement(this, localName, ns);
		EndStoringCharacters();
		_namespaceContext.PopScope();
	}

	private void HandleCharacters(byte[] bytes)
	{
		if (bytes.Length == 0)
		{
			return;
		}
		if (_storingCharacters)
		{
			_charactersBuffer.AddRange(bytes);
		}
		_delegate.DidFindCharacters(this, bytes);
	}

	private static List<(string? Prefix, string Uri)>? ExtractNamespaceBindings(List<XmlScanner.RawAttribute>? rawAttributes)
	{
		if (rawAttributes is null)
		{
			return null;
		}
		List<(string? Prefix, string Uri)>? bindings = null;
		foreach (var attribute in rawAttributes)
		{
			if (attribute.Prefix is null)
			{
				if (attribute.LocalName.AsSpan().SequenceEqual("xmlns"u8))
				{
					(bindings ??= []).Add((null, Encoding.UTF8.GetString(attribute.Value)));
				}
			}
			else if (attribute.Prefix.AsSpan().SequenceEqual("xmlns"u8))
			{
				(bindings ??= []).Add((Encoding.UTF8.GetString(attribute.LocalName), Encoding.UTF8.GetString(attribute.Value)));
			}
		}
		return bindings;
	}

	private static bool IsXmlnsAttribute(XmlScanner.RawAttribute attribute) =>
		attribute.Prefix is not null
			? attribute.Prefix.AsSpan().SequenceEqual("xmlns"u8)
			: attribute.LocalName.AsSpan().SequenceEqual("xmlns"u8);

	private static string Decode(byte[] utf8, XmlScanner.Range range) => Encoding.UTF8.GetString(utf8, range.Start, range.Length);
}
