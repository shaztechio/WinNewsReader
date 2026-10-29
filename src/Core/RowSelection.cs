namespace WinNewsReader.Core;

/// <summary>The rules for selecting several rows with Ctrl+click and Shift+click, apart from any list control.</summary>
public static class RowSelection
{
	/// <summary>Ctrl+click: <paramref name="clicked"/> joins the selection, or leaves it if it's in it already.</summary>
	public static IReadOnlyList<T> Toggle<T>(IReadOnlyList<T> selected, T clicked) where T : notnull =>
		selected.Contains(clicked) ? [.. selected.Where(item => !EqualityComparer<T>.Default.Equals(item, clicked))] : [.. selected, clicked];

	/// <summary>Shift+click: every row from <paramref name="anchor"/> to <paramref name="clicked"/>, in list order.</summary>
	public static IReadOnlyList<T> Range<T>(IReadOnlyList<T> rows, T anchor, T clicked) where T : notnull
	{
		var from = IndexOf(rows, anchor);
		var to = IndexOf(rows, clicked);
		if (from < 0 || to < 0)
		{
			return [clicked];
		}
		return [.. rows.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1)];
	}

	private static int IndexOf<T>(IReadOnlyList<T> rows, T item)
	{
		for (var i = 0; i < rows.Count; i++)
		{
			if (EqualityComparer<T>.Default.Equals(rows[i], item))
			{
				return i;
			}
		}
		return -1;
	}
}
