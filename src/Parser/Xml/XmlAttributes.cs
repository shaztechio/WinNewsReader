using System.Text;

namespace WinNewsReader.Parser.Xml;

/// <summary>An element's attributes, with xmlns declarations removed. Values are entity-expanded.</summary>
public sealed class XmlAttributes
{
	internal readonly record struct Attribute(XmlNamespace Namespace, string LocalName, string Value)
	{
		public string QualifiedName => Namespace.Prefix is { } prefix ? $"{prefix}:{LocalName}" : LocalName;
	}

	private readonly List<Attribute> _attributes;

	internal XmlAttributes(List<Attribute> attributes)
	{
		_attributes = attributes;
	}

	public static XmlAttributes Empty { get; } = new([]);

	public int Count => _attributes.Count;

	public bool IsEmpty => _attributes.Count == 0;

	/// <summary>Case-sensitive qualified-name lookup. <paramref name="name"/> may be prefixed (<c>xml:lang</c>) or not (<c>href</c>).</summary>
	public string? this[string name] => Value(name, StringComparison.Ordinal);

	/// <summary>For feeds with messed-up casing (<c>isPermaLink</c> vs <c>ispermalink</c>).</summary>
	public string? ValueIgnoringCase(string name) => Value(name, StringComparison.OrdinalIgnoreCase);

	/// <summary>Qualified names to values. A later duplicate wins, as in a Swift dictionary.</summary>
	public Dictionary<string, string> ToDictionary()
	{
		var dict = new Dictionary<string, string>(_attributes.Count);
		foreach (var attribute in _attributes)
		{
			dict[attribute.QualifiedName] = attribute.Value;
		}
		return dict;
	}

	private string? Value(string name, StringComparison comparison)
	{
		var colon = name.IndexOf(':');
		foreach (var attribute in _attributes)
		{
			if (colon < 0)
			{
				if (attribute.Namespace.Prefix is null && string.Equals(attribute.LocalName, name, comparison))
				{
					return attribute.Value;
				}
				continue;
			}
			if (attribute.Namespace.Prefix is { } prefix
				&& string.Equals(prefix, name[..colon], comparison)
				&& string.Equals(attribute.LocalName, name[(colon + 1)..], comparison))
			{
				return attribute.Value;
			}
		}
		return null;
	}

	internal static string Decode(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
}
