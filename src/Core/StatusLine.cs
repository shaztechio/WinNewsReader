namespace WinNewsReader.Core;

/// <summary>What clicking the status bar cycles through, as NetNewsWire's does: the last refresh, the unread count, and the feed and folder counts.</summary>
public enum StatusMode
{
	LastRefresh,
	Unread,
	FeedsAndFolders,
}

public static class StatusLine
{
	public static StatusMode Next(StatusMode mode) => mode switch
	{
		StatusMode.LastRefresh => StatusMode.Unread,
		StatusMode.Unread => StatusMode.FeedsAndFolders,
		_ => StatusMode.LastRefresh,
	};

	public static string Unread(int unread) => unread switch
	{
		0 => "No unread articles",
		1 => "1 unread article",
		_ => $"{unread:N0} unread articles",
	};

	public static string FeedsAndFolders(int feeds, int folders)
	{
		var feedText = feeds == 1 ? "1 feed" : $"{feeds:N0} feeds";
		return folders == 0 ? feedText : $"{feedText} in {(folders == 1 ? "1 folder" : $"{folders:N0} folders")}";
	}
}
