namespace WinNewsReader.Parser.Xml;

/// <summary>An element or attribute's namespace: the prefix as written and the resolved URI.</summary>
public readonly record struct XmlNamespace(string? Prefix, string? Uri)
{
	public bool IsAtom => Uri == Uris.Atom;

	/// <summary>Dublin Core elements (<c>dc:</c>), both the <c>/elements/1.1/</c> and <c>/terms/</c> variants.</summary>
	public bool IsDublinCore => Uri is Uris.DublinCore or Uris.DublinCoreTerms;

	public bool IsContent => Uri == Uris.Content;

	public bool IsXhtml => Uri == Uris.Xhtml;

	public bool IsMediaRss => Uri is Uris.MediaRss or Uris.MediaRssBoard;

	public bool IsITunes => Uri == Uris.ITunes;

	/// <summary>Scripting.com's <c>source:</c> namespace, used for <c>source:markdown</c> in WordLand feeds.</summary>
	public bool IsSource => Uri == Uris.Source;

	public static class Uris
	{
		public const string Atom = "http://www.w3.org/2005/Atom";
		public const string DublinCore = "http://purl.org/dc/elements/1.1/";
		public const string DublinCoreTerms = "http://purl.org/dc/terms/";
		public const string Content = "http://purl.org/rss/1.0/modules/content/";
		public const string Xhtml = "http://www.w3.org/1999/xhtml";
		public const string MediaRss = "http://search.yahoo.com/mrss/";
		public const string MediaRssBoard = "http://www.rssboard.org/media-rss";
		public const string ITunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
		public const string Source = "https://source.scripting.com/";
	}
}

/// <summary>Stack of xmlns bindings. Scopes are recorded as offsets so elements without xmlns allocate nothing.</summary>
internal sealed class XmlNamespaceContext
{
	private readonly List<(string? Prefix, string Uri)> _bindings = [];
	private readonly List<int> _scopeStarts = [];

	public void PushScope(List<(string? Prefix, string Uri)>? entries)
	{
		_scopeStarts.Add(_bindings.Count);
		if (entries is not null)
		{
			_bindings.AddRange(entries);
		}
	}

	public void PopScope()
	{
		if (_scopeStarts.Count == 0)
		{
			return;
		}
		var start = _scopeStarts[^1];
		_scopeStarts.RemoveAt(_scopeStarts.Count - 1);
		if (_bindings.Count > start)
		{
			_bindings.RemoveRange(start, _bindings.Count - start);
		}
	}

	/// <summary>Resolve a prefix to its URI, or null if unbound. Pass null for the default namespace.</summary>
	public string? Resolve(string? prefix)
	{
		if (prefix == "xml")
		{
			return "http://www.w3.org/XML/1998/namespace";
		}
		if (prefix == "xmlns")
		{
			return "http://www.w3.org/2000/xmlns/";
		}
		for (var i = _bindings.Count - 1; i >= 0; i--)
		{
			if (_bindings[i].Prefix == prefix)
			{
				return _bindings[i].Uri;
			}
		}
		return null;
	}
}
