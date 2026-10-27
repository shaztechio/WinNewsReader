using System.Text;
using WinNewsReader.Parser.Utilities;
using WinNewsReader.Parser.Xml;

namespace WinNewsReader.Parser.Html;

/// <summary>
/// Rewrites relative <c>href</c>, <c>src</c>, <c>poster</c> and <c>srcset</c> URLs in an HTML fragment
/// against a base URL. Only those attribute values change; every other byte is copied through.
/// </summary>
public static class HtmlRelativeUrlResolver
{
	public static string ResolvingRelativeUrls(string html, string baseUrl)
	{
		var bytes = Encoding.UTF8.GetBytes(html);
		return RewrittenBytes(bytes, baseUrl) is { } rewritten ? Encoding.UTF8.GetString(rewritten) : html;
	}

	public static string ResolvingRelativeUrls(ReadOnlySpan<byte> bytes, string baseUrl)
	{
		var copy = bytes.ToArray();
		return Encoding.UTF8.GetString(RewrittenBytes(copy, baseUrl) ?? copy);
	}

	private static byte[]? RewrittenBytes(byte[] bytes, string baseUrl)
	{
		var replacements = new Scanner(bytes, FoundationUrl.Parse(baseUrl)).Scan();
		if (replacements.Count == 0)
		{
			return null;
		}
		var result = new List<byte>(bytes.Length + 64);
		var copyStart = 0;
		foreach (var (start, end, replacement) in replacements)
		{
			result.AddRange(bytes.AsSpan(copyStart, start - copyStart));
			result.AddRange(replacement);
			copyStart = end;
		}
		result.AddRange(bytes.AsSpan(copyStart));
		return [.. result];
	}

	private sealed class Scanner(byte[] bytes, FoundationUrl.Parts? baseUrl)
	{
		private readonly List<(int Start, int End, byte[] Bytes)> _replacements = [];
		private int _pos;

		private int End => bytes.Length;

		public List<(int Start, int End, byte[] Bytes)> Scan()
		{
			while (_pos < End)
			{
				if (bytes[_pos] == '<')
				{
					HandleLessThan();
				}
				else
				{
					_pos++;
				}
			}
			return _replacements;
		}

		private void HandleLessThan()
		{
			if (_pos + 1 >= End)
			{
				_pos = End;
				return;
			}
			var next = bytes[_pos + 1];
			if (next == '!')
			{
				if (Matches("<!--"u8))
				{
					SkipPast("-->"u8);
				}
				else if (Matches("<![CDATA["u8))
				{
					SkipPast("]]>"u8);
				}
				else
				{
					SkipPastGreaterThan();
				}
			}
			else if (next == '?')
			{
				SkipPast("?>"u8);
			}
			else if (next == '/')
			{
				SkipPastGreaterThan();
			}
			else if (AsciiBytes.IsLetter(next))
			{
				ScanStartTag();
			}
			else
			{
				_pos++;
			}
		}

		private void ScanStartTag()
		{
			var nameStart = _pos + 1;
			var i = nameStart;
			while (i < End && AsciiBytes.IsXmlNameChar(bytes[i]))
			{
				i++;
			}
			var tagName = bytes.AsSpan(nameStart, i - nameStart);
			_pos = i;
			var selfClosing = false;
			while (_pos < End)
			{
				SkipWhitespace();
				if (_pos >= End)
				{
					return;
				}
				var b = bytes[_pos];
				if (b == '>')
				{
					_pos++;
					break;
				}
				if (b == '/')
				{
					if (_pos + 1 < End && bytes[_pos + 1] == '>')
					{
						selfClosing = true;
						_pos += 2;
						break;
					}
					_pos++;
					continue;
				}
				if (AsciiBytes.IsXmlNameStart(b))
				{
					ScanAttribute();
				}
				else
				{
					_pos++;
				}
			}
			if (!selfClosing)
			{
				if (AsciiBytes.EqualsIgnoreCase(tagName, "script"u8))
				{
					SkipRawText("script"u8);
				}
				else if (AsciiBytes.EqualsIgnoreCase(tagName, "style"u8))
				{
					SkipRawText("style"u8);
				}
			}
		}

		private void ScanAttribute()
		{
			var nameStart = _pos;
			while (_pos < End)
			{
				var b = bytes[_pos];
				if (AsciiBytes.IsWhitespace(b) || b == '=' || b == '>' || b == '/')
				{
					break;
				}
				_pos++;
			}
			var nameEnd = _pos;
			SkipWhitespace();
			if (_pos >= End || bytes[_pos] != '=')
			{
				return;
			}
			_pos++;
			SkipWhitespace();
			if (_pos >= End)
			{
				return;
			}
			int valueStart, valueEnd;
			var quote = bytes[_pos];
			if (quote is (byte)'"' or (byte)'\'')
			{
				valueStart = _pos + 1;
				var i = valueStart;
				while (i < End && bytes[i] != quote)
				{
					i++;
				}
				if (i >= End)
				{
					_pos = End;
					return;
				}
				valueEnd = i;
				_pos = i + 1;
			}
			else
			{
				valueStart = _pos;
				var i = _pos;
				while (i < End && !AsciiBytes.IsWhitespace(bytes[i]) && bytes[i] != '>')
				{
					i++;
				}
				valueEnd = i;
				_pos = i;
			}
			var name = bytes.AsSpan(nameStart, nameEnd - nameStart);
			if (AsciiBytes.EqualsIgnoreCase(name, "href"u8) || AsciiBytes.EqualsIgnoreCase(name, "src"u8) || AsciiBytes.EqualsIgnoreCase(name, "poster"u8))
			{
				AddReplacementIfNeeded(valueStart, valueEnd);
			}
			else if (AsciiBytes.EqualsIgnoreCase(name, "srcset"u8))
			{
				HandleSrcset(valueStart, valueEnd);
			}
		}

		private void HandleSrcset(int i, int valueEnd)
		{
			while (i < valueEnd)
			{
				while (i < valueEnd && (AsciiBytes.IsWhitespace(bytes[i]) || bytes[i] == ','))
				{
					i++;
				}
				if (i >= valueEnd)
				{
					return;
				}
				var urlStart = i;
				while (i < valueEnd && !AsciiBytes.IsWhitespace(bytes[i]))
				{
					i++;
				}
				var urlEnd = i;
				while (urlEnd > urlStart && bytes[urlEnd - 1] == ',')
				{
					urlEnd--;
				}
				var tokenEndedWithComma = urlEnd < i;
				if (urlEnd > urlStart)
				{
					AddReplacementIfNeeded(urlStart, urlEnd);
				}
				if (!tokenEndedWithComma)
				{
					while (i < valueEnd && bytes[i] != ',')
					{
						i++;
					}
				}
			}
		}

		private void AddReplacementIfNeeded(int lower, int upper)
		{
			while (lower < upper && AsciiBytes.IsWhitespace(bytes[lower]))
			{
				lower++;
			}
			while (upper > lower && AsciiBytes.IsWhitespace(bytes[upper - 1]))
			{
				upper--;
			}
			var value = bytes.AsSpan(lower, upper - lower);
			if (value.Length == 0 || value[0] == '#' || HasScheme(value))
			{
				return;
			}
			var s = Encoding.UTF8.GetString(value);
			var resolved = FoundationUrl.Resolve(s, baseUrl);
			if (resolved is null || resolved == s)
			{
				return;
			}
			_replacements.Add((lower, upper, Encoding.UTF8.GetBytes(resolved)));
		}

		private static bool HasScheme(ReadOnlySpan<byte> value)
		{
			if (!AsciiBytes.IsLetter(value[0]))
			{
				return false;
			}
			for (var i = 1; i < value.Length; i++)
			{
				var b = value[i];
				if (b == ':')
				{
					return true;
				}
				if (!(AsciiBytes.IsLetter(b) || AsciiBytes.IsDigit(b) || b == '+' || b == '-' || b == '.'))
				{
					return false;
				}
			}
			return false;
		}

		private void SkipWhitespace()
		{
			while (_pos < End && AsciiBytes.IsWhitespace(bytes[_pos]))
			{
				_pos++;
			}
		}

		private void SkipPastGreaterThan()
		{
			while (_pos < End && bytes[_pos] != '>')
			{
				_pos++;
			}
			if (_pos < End)
			{
				_pos++;
			}
		}

		private bool Matches(ReadOnlySpan<byte> literal) =>
			_pos + literal.Length <= End && bytes.AsSpan(_pos, literal.Length).SequenceEqual(literal);

		private void SkipPast(ReadOnlySpan<byte> literal)
		{
			while (_pos + literal.Length <= End)
			{
				if (bytes.AsSpan(_pos, literal.Length).SequenceEqual(literal))
				{
					_pos += literal.Length;
					return;
				}
				_pos++;
			}
			_pos = End;
		}

		private void SkipRawText(ReadOnlySpan<byte> lowercaseCloseTagName)
		{
			while (_pos < End)
			{
				if (bytes[_pos] == '<' && _pos + 1 < End && bytes[_pos + 1] == '/')
				{
					var nameStart = _pos + 2;
					var nameEnd = nameStart + lowercaseCloseTagName.Length;
					if (nameEnd <= End
						&& AsciiBytes.EqualsIgnoreCase(bytes.AsSpan(nameStart, lowercaseCloseTagName.Length), lowercaseCloseTagName)
						&& (nameEnd >= End || bytes[nameEnd] == '>' || bytes[nameEnd] == '/' || AsciiBytes.IsWhitespace(bytes[nameEnd])))
					{
						_pos = nameEnd;
						SkipPastGreaterThan();
						return;
					}
				}
				_pos++;
			}
		}
	}
}
