namespace WinNewsReader.Parser.Xml;

/// <summary>
/// Liberal, byte-level XML tokenizer. Never throws on malformed input: stray <c>&lt;</c> becomes text,
/// missing quotes and end tags are tolerated, and unknown declarations are skipped.
/// </summary>
internal sealed class XmlScanner
{
	public enum EventKind
	{
		StartElement,
		EndElement,
		Characters,
		EndOfDocument,
	}

	/// <summary>A byte range into the input. Start is -1 for "no range".</summary>
	public readonly record struct Range(int Start, int End)
	{
		public static readonly Range None = new(-1, -1);

		public bool IsNone => Start < 0;

		public int Length => End - Start;
	}

	public readonly record struct RawAttribute(byte[]? Prefix, byte[] LocalName, byte[] Value);

	public readonly record struct Event(
		EventKind Kind,
		Range Prefix = default,
		Range LocalName = default,
		List<RawAttribute>? Attributes = null,
		bool SelfClosing = false,
		byte[]? Characters = null);

	private readonly byte[] _input;
	private int _pos;

	public XmlScanner(byte[] input)
	{
		_input = input;
	}

	/// <summary>Byte offset just past the last event, or where the next event starts scanning.</summary>
	public int Position => _pos;

	public Event Next()
	{
		while (_pos < _input.Length)
		{
			if (_input[_pos] == '<')
			{
				if (ScanMarkup() is { } markupEvent)
				{
					return markupEvent;
				}
				// Comment, PI or DOCTYPE consumed silently.
				continue;
			}
			return ScanCharacters();
		}
		return new Event(EventKind.EndOfDocument);
	}

	private Event? ScanMarkup()
	{
		var next = Peek(1);
		if (next == '!')
		{
			if (MatchPrefix("<!--"u8))
			{
				ConsumeComment();
				return null;
			}
			if (MatchPrefix("<![CDATA["u8))
			{
				return ScanCData();
			}
			if (MatchPrefix("<!DOCTYPE"u8) || MatchPrefix("<!doctype"u8))
			{
				ConsumeDocType();
				return null;
			}
			SkipPastGreaterThan();
			return null;
		}
		if (next == '?')
		{
			ConsumeProcessingInstruction();
			return null;
		}
		if (next == '/')
		{
			return ScanEndTag();
		}
		if (next >= 0 && AsciiBytes.IsXmlNameStart((byte)next))
		{
			return ScanStartTag();
		}
		// Stray `<`: emit it as literal text.
		_pos++;
		return new Event(EventKind.Characters, Characters: [(byte)'<']);
	}

	private Event ScanStartTag()
	{
		_pos++;
		var (prefix, local) = ScanQualifiedName();
		List<RawAttribute>? attributes = null;
		var selfClosing = false;
		while (_pos < _input.Length)
		{
			SkipWhitespace();
			if (_pos >= _input.Length)
			{
				break;
			}
			var b = _input[_pos];
			if (b == '>')
			{
				_pos++;
				break;
			}
			if (b == '/')
			{
				if (Peek(1) == '>')
				{
					selfClosing = true;
					_pos += 2;
					break;
				}
				_pos++;
				continue;
			}
			if (!AsciiBytes.IsXmlNameStart(b))
			{
				_pos++;
				continue;
			}
			(attributes ??= []).Add(ScanAttribute());
		}
		return new Event(EventKind.StartElement, prefix, local, attributes, selfClosing);
	}

	private RawAttribute ScanAttribute()
	{
		var (prefix, local) = ScanQualifiedName();
		var prefixBytes = prefix.IsNone ? null : _input[prefix.Start..prefix.End];
		var localBytes = _input[local.Start..local.End];
		SkipWhitespace();
		if (_pos >= _input.Length || _input[_pos] != '=')
		{
			return new RawAttribute(prefixBytes, localBytes, []);
		}
		_pos++;
		SkipWhitespace();
		if (_pos >= _input.Length)
		{
			return new RawAttribute(prefixBytes, localBytes, []);
		}
		return new RawAttribute(prefixBytes, localBytes, ScanAttributeValue());
	}

	private byte[] ScanAttributeValue()
	{
		var quote = _input[_pos];
		var quoted = quote is (byte)'"' or (byte)'\'';
		if (quoted)
		{
			_pos++;
		}
		var output = new List<byte>();
		while (_pos < _input.Length)
		{
			var b = _input[_pos];
			if (quoted ? b == quote : AsciiBytes.IsWhitespace(b) || b == '>' || b == '/')
			{
				break;
			}
			if (b == '&')
			{
				var (decoded, next) = XmlEntities.Decode(_input, _pos, XmlEntities.Mode.XmlStrict);
				output.AddRange(decoded);
				_pos = next;
				continue;
			}
			output.Add(b);
			_pos++;
		}
		if (quoted && _pos < _input.Length)
		{
			_pos++;
		}
		return [.. output];
	}

	private Event ScanEndTag()
	{
		_pos += 2;
		var (prefix, local) = ScanQualifiedName();
		SkipPastGreaterThan();
		return new Event(EventKind.EndElement, prefix, local);
	}

	private Event ScanCData()
	{
		_pos += 9;
		var start = _pos;
		var end = _pos;
		while (end + 2 < _input.Length)
		{
			if (_input[end] == ']' && _input[end + 1] == ']' && _input[end + 2] == '>')
			{
				break;
			}
			end++;
		}
		if (end + 2 >= _input.Length)
		{
			end = _input.Length;
			_pos = end;
		}
		else
		{
			_pos = end + 3;
		}
		// CDATA is raw: libxml2 performs no entity substitution inside it, and neither do we.
		return new Event(EventKind.Characters, Characters: _input[start..end]);
	}

	private void ConsumeComment()
	{
		_pos += 4;
		while (_pos + 2 < _input.Length)
		{
			if (_input[_pos] == '-' && _input[_pos + 1] == '-' && _input[_pos + 2] == '>')
			{
				_pos += 3;
				return;
			}
			_pos++;
		}
		_pos = _input.Length;
	}

	private void ConsumeProcessingInstruction()
	{
		_pos += 2;
		while (_pos + 1 < _input.Length)
		{
			if (_input[_pos] == '?' && _input[_pos + 1] == '>')
			{
				_pos += 2;
				return;
			}
			_pos++;
		}
		_pos = _input.Length;
	}

	private void ConsumeDocType()
	{
		_pos += 9;
		// The DOCTYPE may have an internal subset `[...]` before its closing `>`.
		var depth = 0;
		while (_pos < _input.Length)
		{
			var b = _input[_pos];
			if (b == '[')
			{
				depth++;
			}
			else if (b == ']')
			{
				if (depth > 0)
				{
					depth--;
				}
			}
			else if (b == '>' && depth == 0)
			{
				_pos++;
				return;
			}
			_pos++;
		}
	}

	private Event ScanCharacters()
	{
		var start = _pos;
		List<byte>? output = null;
		while (_pos < _input.Length)
		{
			var b = _input[_pos];
			if (b == '<')
			{
				break;
			}
			if (b == '&')
			{
				if (output is null)
				{
					output = new List<byte>(_input.Length - start);
					output.AddRange(_input.AsSpan(start, _pos - start));
				}
				var (decoded, next) = XmlEntities.Decode(_input, _pos, XmlEntities.Mode.XmlStrict);
				output.AddRange(decoded);
				_pos = next;
				continue;
			}
			output?.Add(b);
			_pos++;
		}
		return new Event(EventKind.Characters, Characters: output is null ? _input[start.._pos] : [.. output]);
	}

	/// <summary>Scan an XML name, splitting prefix and local name on the first colon.</summary>
	private (Range Prefix, Range Local) ScanQualifiedName()
	{
		var start = _pos;
		while (_pos < _input.Length && AsciiBytes.IsXmlNameChar(_input[_pos]))
		{
			_pos++;
		}
		var end = _pos;
		for (var i = start; i < end; i++)
		{
			if (_input[i] == ':')
			{
				return (new Range(start, i), new Range(i + 1, end));
			}
		}
		return (Range.None, new Range(start, end));
	}

	private int Peek(int offset)
	{
		var i = _pos + offset;
		return i < _input.Length ? _input[i] : -1;
	}

	private void SkipWhitespace()
	{
		while (_pos < _input.Length && AsciiBytes.IsWhitespace(_input[_pos]))
		{
			_pos++;
		}
	}

	private void SkipPastGreaterThan()
	{
		while (_pos < _input.Length && _input[_pos] != '>')
		{
			_pos++;
		}
		if (_pos < _input.Length)
		{
			_pos++;
		}
	}

	private bool MatchPrefix(ReadOnlySpan<byte> literal) =>
		_pos + literal.Length <= _input.Length && _input.AsSpan(_pos, literal.Length).SequenceEqual(literal);
}
