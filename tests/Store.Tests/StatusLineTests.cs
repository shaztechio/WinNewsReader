using WinNewsReader.Core;

namespace WinNewsReader.Store.Tests;

/// <summary>The status bar's text, and the order a click cycles through it.</summary>
public sealed class StatusLineTests
{
	[Fact]
	public void ClickingCyclesLastRefreshUnreadFeedsAndBack()
	{
		var mode = StatusMode.LastRefresh;
		var seen = new List<StatusMode>();
		for (var i = 0; i < 4; i++)
		{
			mode = StatusLine.Next(mode);
			seen.Add(mode);
		}

		Assert.Equal([StatusMode.Unread, StatusMode.FeedsAndFolders, StatusMode.LastRefresh, StatusMode.Unread], seen);
	}

	[Theory]
	[InlineData(0, "No unread articles")]
	[InlineData(1, "1 unread article")]
	[InlineData(1234, "1,234 unread articles")]
	public void UnreadCounts(int unread, string text) => Assert.Equal(text, StatusLine.Unread(unread));

	[Theory]
	[InlineData(1, 0, "1 feed")]
	[InlineData(10, 0, "10 feeds")]
	[InlineData(10, 1, "10 feeds in 1 folder")]
	[InlineData(1, 3, "1 feed in 3 folders")]
	public void FeedAndFolderCounts(int feeds, int folders, string text) => Assert.Equal(text, StatusLine.FeedsAndFolders(feeds, folders));
}
