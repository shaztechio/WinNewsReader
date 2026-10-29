namespace WinNewsReader.Core;

/// <summary>What NetNewsWire's column layout sorts the timeline by.</summary>
public enum ArticleSortField
{
	Date,
	Feed,
	Title,
	Unread,
	Starred,
}

/// <summary>
/// The order of the timeline. By date it's newest first, ties broken by article ID. By another field it starts from that
/// field's natural order (feed and title A to Z, unread and starred first) and falls back to the date, newest first.
/// <c>reversed</c> turns the chosen field's order around: oldest first for date. "Group by Feed" then keeps each feed's
/// articles together, feeds in name order, with the chosen order inside each.
/// </summary>
public static class ArticleSorting
{
	public static IReadOnlyList<T> Sort<T>(
		IEnumerable<T> items,
		ArticleSortField field,
		bool reversed,
		bool groupByFeed,
		Func<T, DateTimeOffset> date,
		Func<T, string> id,
		Func<T, string> feed,
		Func<T, string> title,
		Func<T, bool> unread,
		Func<T, bool> starred)
	{
		var byDate = items.OrderByDescending(date).ThenBy(id, StringComparer.Ordinal).ToList();
		List<T> sorted;
		if (field == ArticleSortField.Date)
		{
			sorted = byDate;
			if (reversed)
			{
				sorted.Reverse();
			}
		}
		else
		{
			// OrderBy is stable, so what the primary key ties on stays in date order.
			sorted = (field switch
			{
				ArticleSortField.Feed => Order(byDate, feed, reversed),
				ArticleSortField.Title => Order(byDate, title, reversed),
				ArticleSortField.Unread => Order(byDate, item => unread(item) ? 0 : 1, reversed),
				_ => Order(byDate, item => starred(item) ? 0 : 1, reversed),
			}).ToList();
		}
		return groupByFeed
			? [.. sorted.OrderBy(feed, StringComparer.CurrentCultureIgnoreCase)]
			: sorted;
	}

	private static IOrderedEnumerable<T> Order<T, TKey>(List<T> items, Func<T, TKey> key, bool reversed) where TKey : IComparable<TKey> =>
		reversed
			? items.OrderByDescending(key, Comparer<TKey>.Create(Compare))
			: items.OrderBy(key, Comparer<TKey>.Create(Compare));

	private static int Compare<TKey>(TKey a, TKey b) where TKey : IComparable<TKey> =>
		a is string x && b is string y ? string.Compare(x, y, StringComparison.CurrentCultureIgnoreCase) : a.CompareTo(b);
}
