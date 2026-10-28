using System.Text;

namespace WinNewsReader.Core.ArticleRendering;

/// <summary>
/// NetNewsWire's <c>MacroProcessor</c>: replaces <c>[[key]]</c> macros in a template with their values. Macros without a
/// value stay as they are, and values aren't scanned again, so a value containing a macro is left alone.
/// </summary>
public static class MacroProcessor
{
	public static string RenderedText(string template, IReadOnlyDictionary<string, string> substitutions, string macroStart = "[[", string macroEnd = "]]")
	{
		if (macroStart.Length == 0 || macroEnd.Length == 0)
		{
			throw new ArgumentException("Macro delimiters can't be empty.");
		}
		var result = new StringBuilder(template.Length);
		var index = 0;
		while (true)
		{
			var start = template.IndexOf(macroStart, index, StringComparison.Ordinal);
			if (start < 0)
			{
				break;
			}
			result.Append(template, index, start - index);
			var keyStart = start + macroStart.Length;
			var end = template.IndexOf(macroEnd, keyStart, StringComparison.Ordinal);
			if (end < 0)
			{
				index = start;
				break;
			}
			var key = template[keyStart..end];
			result.Append(substitutions.TryGetValue(key, out var value) ? value : macroStart + key + macroEnd);
			index = end + macroEnd.Length;
		}
		result.Append(template, index, template.Length - index);
		return result.ToString();
	}
}
