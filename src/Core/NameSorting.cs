using System.Globalization;
using WinNewsReader.Store;

namespace WinNewsReader.Core;

/// <summary>How NetNewsWire names and orders feeds and folders in the sidebar and in OPML exports.</summary>
public static class NameSorting
{
	/// <summary>Swift's <c>localizedStandardCompare</c>: case-insensitive, with digit runs compared as numbers.</summary>
	public static readonly StringComparer Comparer =
		StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

	/// <summary>NetNewsWire's <c>nameForDisplay</c> for a feed.</summary>
	/// <summary>NetNewsWire's <c>nameForDisplay</c>: the reader's name for the feed, else its own.</summary>
	public static string DisplayName(StoredFeed feed) =>
		feed.EditedName is { Length: > 0 } edited ? edited : feed.Name is { Length: > 0 } name ? name : "Untitled";

	public static IEnumerable<StoredFeed> SortedByName(this IEnumerable<StoredFeed> feeds) => feeds.OrderBy(DisplayName, Comparer);

	public static IEnumerable<StoredFolder> SortedByName(this IEnumerable<StoredFolder> folders) => folders.OrderBy(folder => folder.Name, Comparer);
}
