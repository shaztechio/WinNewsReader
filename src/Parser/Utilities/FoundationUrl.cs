using System.Text;
using System.Text.RegularExpressions;

namespace WinNewsReader.Parser.Utilities;

/// <summary>
/// RFC 3986 reference resolution that behaves like Foundation's <c>URL(string:relativeTo:).absoluteString</c>:
/// no normalization beyond dot-segment removal, so parsed links match NetNewsWire's byte for byte.
/// <see cref="System.Uri"/> is not used because it adds trailing slashes and re-escapes.
/// </summary>
public static partial class FoundationUrl
{
	public readonly record struct Parts(string? Scheme, string? Authority, string Path, string? Query, string? Fragment)
	{
		public override string ToString()
		{
			var sb = new StringBuilder();
			if (Scheme is not null)
			{
				sb.Append(Scheme).Append(':');
			}
			if (Authority is not null)
			{
				sb.Append("//").Append(Authority);
			}
			sb.Append(Path);
			if (Query is not null)
			{
				sb.Append('?').Append(Query);
			}
			if (Fragment is not null)
			{
				sb.Append('#').Append(Fragment);
			}
			return sb.ToString();
		}

		/// <summary>The host without userinfo or port, as Foundation's <c>host()</c> returns it.</summary>
		public string? Host
		{
			get
			{
				if (Authority is null)
				{
					return null;
				}
				var host = Authority;
				var at = host.LastIndexOf('@');
				if (at >= 0)
				{
					host = host[(at + 1)..];
				}
				if (host.StartsWith('['))
				{
					var close = host.IndexOf(']');
					return close > 0 ? host[1..close] : host;
				}
				var colon = host.IndexOf(':');
				return colon >= 0 ? host[..colon] : host;
			}
		}
	}

	/// <summary>Parse like <c>URL(string:)</c>: null for an empty string; invalid characters are percent-encoded.</summary>
	public static Parts? Parse(string? s)
	{
		if (string.IsNullOrEmpty(s))
		{
			return null;
		}
		var match = UriPattern().Match(EncodeInvalidCharacters(s));
		var scheme = match.Groups[2].Success ? match.Groups[2].Value : null;
		if (scheme is not null && !SchemePattern().IsMatch(scheme))
		{
			return null;
		}
		return new Parts(
			scheme,
			match.Groups[4].Success ? match.Groups[4].Value : null,
			match.Groups[5].Value,
			match.Groups[7].Success ? match.Groups[7].Value : null,
			match.Groups[9].Success ? match.Groups[9].Value : null);
	}

	/// <summary><c>URL(string: relative, relativeTo: URL(string: baseUrl))?.absoluteString</c>.</summary>
	public static string? Resolve(string? relative, string? baseUrl) => Resolve(relative, Parse(baseUrl));

	public static string? Resolve(string? relative, Parts? baseParts)
	{
		if (Parse(relative) is not { } reference)
		{
			return null;
		}
		// An absolute reference, or no base, is returned as written (Foundation doesn't normalize it).
		if (baseParts is not { } b || reference.Scheme is not null)
		{
			return reference.ToString();
		}
		string? authority;
		string path;
		string? query;
		if (reference.Authority is not null)
		{
			authority = reference.Authority;
			path = RemoveDotSegments(reference.Path);
			query = reference.Query;
		}
		else
		{
			authority = b.Authority;
			if (reference.Path.Length == 0)
			{
				path = b.Path;
				query = reference.Query ?? b.Query;
			}
			else
			{
				path = reference.Path.StartsWith('/')
					? RemoveDotSegments(reference.Path)
					: RemoveDotSegments(Merge(b, reference.Path));
				query = reference.Query;
			}
		}
		return new Parts(b.Scheme, authority, path, query, reference.Fragment).ToString();
	}

	/// <summary><c>URL.standardized.absoluteString</c>: dot segments removed from the path.</summary>
	public static string Standardize(string url) =>
		Parse(url) is { } parts ? (parts with { Path = RemoveDotSegments(parts.Path) }).ToString() : url;

	private static string Merge(Parts b, string referencePath)
	{
		if (b.Authority is not null && b.Path.Length == 0)
		{
			return "/" + referencePath;
		}
		var lastSlash = b.Path.LastIndexOf('/');
		return lastSlash < 0 ? referencePath : b.Path[..(lastSlash + 1)] + referencePath;
	}

	/// <summary>RFC 3986 section 5.2.4.</summary>
	private static string RemoveDotSegments(string path)
	{
		if (!path.Contains('.'))
		{
			return path;
		}
		var input = path;
		var output = new StringBuilder();
		while (input.Length > 0)
		{
			if (input.StartsWith("../"))
			{
				input = input[3..];
			}
			else if (input.StartsWith("./"))
			{
				input = input[2..];
			}
			else if (input.StartsWith("/./"))
			{
				input = input[2..];
			}
			else if (input == "/.")
			{
				input = "/";
			}
			else if (input.StartsWith("/../") || input == "/..")
			{
				input = input == "/.." ? "/" : input[3..];
				var s = output.ToString();
				var last = s.LastIndexOf('/');
				output.Clear().Append(last >= 0 ? s[..last] : "");
			}
			else if (input is "." or "..")
			{
				input = "";
			}
			else
			{
				var start = input.StartsWith('/') ? 1 : 0;
				var next = input.IndexOf('/', start);
				var segment = next < 0 ? input : input[..next];
				output.Append(segment);
				input = next < 0 ? "" : input[next..];
			}
		}
		return output.ToString();
	}

	/// <summary>Percent-encode characters that can't appear in a URL, as Foundation's URL(string:) does.</summary>
	private static string EncodeInvalidCharacters(string s)
	{
		StringBuilder? sb = null;
		for (var i = 0; i < s.Length; i++)
		{
			var c = s[i];
			var valid = c < 0x80 && c > 0x20 && c != 0x7F && !"\"<>\\^`{|}".Contains(c)
				&& (c != '%' || (i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2])));
			if (valid)
			{
				sb?.Append(c);
				continue;
			}
			sb ??= new StringBuilder(s, 0, i, s.Length + 16);
			var length = char.IsHighSurrogate(c) && i + 1 < s.Length ? 2 : 1;
			foreach (var b in Encoding.UTF8.GetBytes(s.Substring(i, length)))
			{
				sb.Append('%').Append(b.ToString("X2"));
			}
			i += length - 1;
		}
		return sb?.ToString() ?? s;
	}

	[GeneratedRegex(@"^(([^:/?#]+):)?(//([^/?#]*))?([^?#]*)(\?([^#]*))?(#(.*))?$", RegexOptions.Singleline)]
	private static partial Regex UriPattern();

	[GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]*$")]
	private static partial Regex SchemePattern();
}
