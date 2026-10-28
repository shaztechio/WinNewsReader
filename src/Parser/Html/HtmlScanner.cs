using System.Text;
using WinNewsReader.Parser.Xml;

namespace WinNewsReader.Parser.Html;

internal interface IHtmlScannerDelegate
{
	/// <summary>Called on each start tag, including self-closing ones.</summary>
	void DidStartTag(ReadOnlySpan<byte> name, HtmlAttributes attributes, bool selfClosing) { }

	void DidEndTag(ReadOnlySpan<byte> name) { }

	/// <summary>Called for each run of character content, with entity references expanded.</summary>
	void DidFindCharacters(ReadOnlySpan<byte> bytes) { }
}

/// <summary>
/// Liberal, byte-oriented HTML scanner with a SAX-style delegate. Entity references are expanded;
/// comments, DOCTYPE and processing instructions are skipped.
/// Unlike <see cref="XmlScanner"/>: tag names compare case-insensitively, <c>&lt;script&gt;</c> and
/// <c>&lt;style&gt;</c> content is raw text, and attributes may be valueless, single-quoted or unquoted.
/// There's no void-element tracking.
/// </summary>
internal sealed class HtmlScanner(IHtmlScannerDelegate @delegate)
{
	private static readonly byte[] ScriptBytes = "script"u8.ToArray();
	private static readonly byte[] StyleBytes = "style"u8.ToArray();

	private byte[] _input = [];
	private int _pos;

	public void Parse(byte[] bytes)
	{
		_input = bytes;
		_pos = 0;
		while (_pos < _input.Length)
		{
			if (_input[_pos] == '<')
			{
				ScanMarkup();
			}
			else
			{
				ScanCharacters();
			}
		}
	}

	/// <summary>Everything starting with <c>&lt;</c>.</summary>
	private void ScanMarkup()
	{
		var next = Peek(1);
		if (next == '!')
		{
			if (MatchPrefix("<!--"u8))
			{
				ConsumeComment();
			}
			else if (MatchPrefix("<![CDATA["u8))
			{
				ConsumeCData();
			}
			else
			{
				// <!DOCTYPE and any other declaration: skip to `>`.
				ConsumeUntilGreaterThan();
			}
			return;
		}
		if (next == '?')
		{
			ConsumeProcessingInstruction();
			return;
		}
		if (next == '/')
		{
			ScanEndTag();
			return;
		}
		if (next is { } b && AsciiBytes.IsXmlNameStart(b))
		{
			ScanStartTag();
			return;
		}
		// A stray `<` is literal text.
		@delegate.DidFindCharacters("<"u8);
		_pos++;
	}

	private void ScanStartTag()
	{
		_pos++;
		var (nameStart, nameEnd) = ScanTagName();
		var attributes = new List<(byte[] Name, string Value)>();
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
				// Garbage: skip it and keep going.
				_pos++;
				continue;
			}
			if (ScanAttribute() is { } attribute)
			{
				attributes.Add(attribute);
			}
		}

		var name = _input.AsSpan(nameStart, nameEnd - nameStart);
		@delegate.DidStartTag(name, new HtmlAttributes(attributes), selfClosing);

		if (!selfClosing)
		{
			if (AsciiBytes.EqualsIgnoreCase(name, ScriptBytes))
			{
				ConsumeRawText(ScriptBytes);
			}
			else if (AsciiBytes.EqualsIgnoreCase(name, StyleBytes))
			{
				ConsumeRawText(StyleBytes);
			}
		}
	}

	private (byte[] Name, string Value)? ScanAttribute()
	{
		var start = _pos;
		while (_pos < _input.Length)
		{
			var b = _input[_pos];
			if (AsciiBytes.IsWhitespace(b) || b is (byte)'=' or (byte)'>' or (byte)'/')
			{
				break;
			}
			_pos++;
		}
		if (_pos == start)
		{
			return null;
		}
		var name = _input[start.._pos];

		SkipWhitespace();
		// No value, as in `<input disabled>`.
		if (_pos >= _input.Length || _input[_pos] != '=')
		{
			return (name, "");
		}
		_pos++;
		SkipWhitespace();
		return _pos >= _input.Length ? (name, "") : (name, ScanAttributeValue());
	}

	private string ScanAttributeValue()
	{
		var quote = _input[_pos];
		var quoted = quote is (byte)'"' or (byte)'\'';
		if (quoted)
		{
			_pos++;
		}
		var start = _pos;
		List<byte>? expanded = null;
		while (_pos < _input.Length)
		{
			var b = _input[_pos];
			// Unquoted values end at whitespace or `>`. A `/` is part of the value, as in
			// `href=https://example.com/feed` (HTML5 tokenizer behavior).
			if (quoted ? b == quote : AsciiBytes.IsWhitespace(b) || b == '>')
			{
				break;
			}
			if (b == '&')
			{
				expanded ??= [.. _input.AsSpan(start, _pos - start)];
				var (bytes, nextIndex) = XmlEntities.Decode(_input, _pos, XmlEntities.Mode.Html);
				expanded.AddRange(bytes);
				_pos = nextIndex;
				continue;
			}
			expanded?.Add(b);
			_pos++;
		}
		var value = expanded is null
			? Encoding.UTF8.GetString(_input, start, _pos - start)
			: Encoding.UTF8.GetString([.. expanded]);
		// Past the closing quote; at EOF without one, just stop.
		if (quoted && _pos < _input.Length)
		{
			_pos++;
		}
		return value;
	}

	private void ScanEndTag()
	{
		_pos += 2;
		var (nameStart, nameEnd) = ScanTagName();
		ConsumeUntilGreaterThan();
		@delegate.DidEndTag(_input.AsSpan(nameStart, nameEnd - nameStart));
	}

	/// <summary>
	/// After a <c>&lt;script&gt;</c> or <c>&lt;style&gt;</c> start tag, everything up to the matching end
	/// tag is characters, followed by the end tag.
	/// </summary>
	private void ConsumeRawText(byte[] tagName)
	{
		var start = _pos;
		while (_pos < _input.Length)
		{
			if (_input[_pos] == '<' && Peek(1) == '/')
			{
				var closerStart = _pos + 2;
				var nameEnd = closerStart;
				while (nameEnd < _input.Length && AsciiBytes.IsXmlNameChar(_input[nameEnd]))
				{
					nameEnd++;
				}
				var candidate = _input.AsSpan(closerStart, nameEnd - closerStart);
				if (AsciiBytes.EqualsIgnoreCase(candidate, tagName))
				{
					if (start < _pos)
					{
						@delegate.DidFindCharacters(_input.AsSpan(start, _pos - start));
					}
					_pos = nameEnd;
					ConsumeUntilGreaterThan();
					@delegate.DidEndTag(candidate);
					return;
				}
			}
			_pos++;
		}
		// EOF without a closer: flush what there is.
		if (start < _pos)
		{
			@delegate.DidFindCharacters(_input.AsSpan(start, _pos - start));
		}
	}

	private void ScanCharacters()
	{
		var start = _pos;
		List<byte>? expanded = null;
		while (_pos < _input.Length)
		{
			var b = _input[_pos];
			if (b == '<')
			{
				break;
			}
			if (b == '&')
			{
				expanded ??= [.. _input.AsSpan(start, _pos - start)];
				var (bytes, nextIndex) = XmlEntities.Decode(_input, _pos, XmlEntities.Mode.Html);
				expanded.AddRange(bytes);
				_pos = nextIndex;
				continue;
			}
			expanded?.Add(b);
			_pos++;
		}
		if (expanded is not null)
		{
			@delegate.DidFindCharacters([.. expanded]);
		}
		else if (start < _pos)
		{
			@delegate.DidFindCharacters(_input.AsSpan(start, _pos - start));
		}
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

	private void ConsumeCData()
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
		if (start < end)
		{
			@delegate.DidFindCharacters(_input.AsSpan(start, end - start));
		}
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

	/// <summary>Skips past the next <c>&gt;</c>, or to EOF.</summary>
	private void ConsumeUntilGreaterThan()
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

	private (int Start, int End) ScanTagName()
	{
		var start = _pos;
		while (_pos < _input.Length && AsciiBytes.IsXmlNameChar(_input[_pos]))
		{
			_pos++;
		}
		return (start, _pos);
	}

	private byte? Peek(int offset) => _pos + offset < _input.Length ? _input[_pos + offset] : null;

	private void SkipWhitespace()
	{
		while (_pos < _input.Length && AsciiBytes.IsWhitespace(_input[_pos]))
		{
			_pos++;
		}
	}

	private bool MatchPrefix(ReadOnlySpan<byte> literal) => _input.AsSpan(_pos).StartsWith(literal);
}

/// <summary>
/// Attributes from an HTML start tag. Names are case-insensitive; values are already entity-decoded.
/// </summary>
internal sealed class HtmlAttributes(List<(byte[] Name, string Value)> attributes)
{
	public bool IsEmpty => attributes.Count == 0;

	/// <summary>Case-insensitive lookup; <paramref name="lowercaseName"/> must be lowercase.</summary>
	public string? this[string lowercaseName]
	{
		get
		{
			var needle = Encoding.ASCII.GetBytes(lowercaseName);
			foreach (var (name, value) in attributes)
			{
				if (AsciiBytes.EqualsIgnoreCase(name, needle))
				{
					return value;
				}
			}
			return null;
		}
	}

	/// <summary>Attributes keyed by their names as written. A repeated name keeps its last value.</summary>
	public Dictionary<string, string> ToDictionary()
	{
		var dictionary = new Dictionary<string, string>(attributes.Count);
		foreach (var (name, value) in attributes)
		{
			dictionary[Encoding.UTF8.GetString(name)] = value;
		}
		return dictionary;
	}
}
