using WinNewsReader.Core;

namespace WinNewsReader.Store.Tests;

/// <summary>The timeline's order: by date by default, or by a column in the column layout, optionally grouped by feed.</summary>
public sealed class ArticleSortingTests
{
	private sealed record Row(string Id, string Feed, string Title, int DaysAgo, bool Unread, bool Starred);

	private static readonly Row[] Rows =
	[
		new("1", "Beta", "banana", 1, Unread: false, Starred: true),
		new("2", "alpha", "Cherry", 2, Unread: true, Starred: false),
		new("3", "Beta", "apple", 3, Unread: true, Starred: true),
		new("4", "alpha", "Apple pie", 4, Unread: false, Starred: false),
	];

	private static string[] Order(ArticleSortField field, bool reversed = false, bool group = false) =>
		[.. ArticleSorting.Sort(Rows, field, reversed, group,
			row => DateTimeOffset.UnixEpoch.AddDays(1000 - row.DaysAgo), row => row.Id, row => row.Feed, row => row.Title, row => row.Unread, row => row.Starred)
			.Select(row => row.Id)];

	[Fact]
	public void DateIsNewestFirstAndReversedIsOldestFirst()
	{
		Assert.Equal(["1", "2", "3", "4"], Order(ArticleSortField.Date));
		Assert.Equal(["4", "3", "2", "1"], Order(ArticleSortField.Date, reversed: true));
	}

	[Fact]
	public void FeedAndTitleGoFromAToZIgnoringCaseWithDateBreakingTies()
	{
		Assert.Equal(["2", "4", "1", "3"], Order(ArticleSortField.Feed));
		Assert.Equal(["3", "4", "1", "2"], Order(ArticleSortField.Title));
		Assert.Equal(["2", "1", "4", "3"], Order(ArticleSortField.Title, reversed: true));
	}

	[Fact]
	public void UnreadAndStarredPutThoseFirst()
	{
		Assert.Equal(["2", "3", "1", "4"], Order(ArticleSortField.Unread));
		Assert.Equal(["1", "3", "2", "4"], Order(ArticleSortField.Starred));
		Assert.Equal(["1", "4", "2", "3"], Order(ArticleSortField.Unread, reversed: true));
	}

	[Fact]
	public void GroupingKeepsEachFeedTogetherInTheChosenOrder()
	{
		Assert.Equal(["2", "4", "1", "3"], Order(ArticleSortField.Date, group: true));
		Assert.Equal(["4", "2", "3", "1"], Order(ArticleSortField.Date, reversed: true, group: true));
		Assert.Equal(["4", "2", "3", "1"], Order(ArticleSortField.Title, group: true));
	}
}
