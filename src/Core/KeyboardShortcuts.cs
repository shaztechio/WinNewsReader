namespace WinNewsReader.Core;

/// <summary>One line of NetNewsWire's Keyboard Shortcuts window.</summary>
public sealed record ShortcutEntry(string Section, string Keys, string Action);

/// <summary>The app's keyboard shortcuts, as the Keyboard Shortcuts window and the help page list them. A test keeps the two together.</summary>
public static class KeyboardShortcuts
{
	public static IReadOnlyList<ShortcutEntry> All { get; } =
	[
		new("Reading", "Space", "Page down; at the end, go to the next unread article"),
		new("Reading", "Shift+Space", "Page up"),
		new("Reading", "N or +", "Next unread article"),
		new("Reading", "-", "Previous unread article"),
		new("Reading", "R or U", "Mark read or unread"),
		new("Reading", "S", "Star or unstar"),
		new("Reading", "K", "Mark all as read"),
		new("Reading", "M", "Mark unread and go to the next unread"),
		new("Reading", "L", "Mark all as read and go to the next unread"),
		new("Reading", "O", "Mark older articles as read"),
		new("Reading", "B or Enter", "Open in browser"),
		new("Reading", "A / Z", "Previous / next feed"),
		new("Moving around", "← →", "Move between the sidebar, the timeline and the article"),
		new("Moving around", ", / .", "Collapse / expand the selected folder (in the sidebar)"),
		new("Moving around", "; / '", "Collapse / expand all folders (in the sidebar)"),
		new("Moving around", "Ctrl+1, 2, 3", "Today, All Unread, Starred"),
		new("Moving around", "Ctrl+/", "Next unread"),
		new("Commands", "Ctrl+R or F5", "Refresh"),
		new("Commands", "Ctrl+N", "New feed"),
		new("Commands", "Ctrl+Shift+N", "New folder"),
		new("Commands", "Ctrl+K", "Mark all as read"),
		new("Commands", "Ctrl+Shift+U", "Mark read or unread"),
		new("Commands", "Ctrl+Shift+L", "Star or unstar"),
		new("Commands", "Ctrl+Shift+R", "Reader View"),
		new("Commands", "Ctrl+Shift+H", "Hide read articles"),
		new("Commands", "Ctrl+Shift+F", "Hide read feeds"),
		new("Commands", "Ctrl+'", "Clean up"),
		new("Commands", "Ctrl+Alt+S", "Show or hide the sidebar"),
		new("Commands", "Ctrl+Alt+F", "Search"),
		new("Commands", "Ctrl+Z / Ctrl+Y", "Undo / redo marking read or starred and deleting a feed or folder (Ctrl+Shift+Z also redoes)"),
		new("Commands", "Ctrl+F", "Find in the article; F3 / Shift+F3 for the next / previous match, Esc to close"),
		new("Commands", "Ctrl+Alt+K / Ctrl+Shift+K", "Mark above / below as read"),
		new("Commands", "Ctrl+Shift+C / Ctrl+Alt+C", "Copy the article URL / external URL"),
		new("Commands", "Ctrl+→", "Open in browser (with the article focused)"),
		new("Commands", "Ctrl+6, 7, 8, 9", "Dinosaurs, Current Activity, Activity Log, Error Log"),
		new("Commands", "F11", "Full screen"),
		new("Commands", "Ctrl+Alt+T", "Hide or show the toolbar (with it hidden, right-click empty space in the sidebar for Show Toolbar)"),
		new("Commands", "Ctrl+0", "Bring the main window forward"),
		new("Commands", "Ctrl+W", "Close the window"),
		new("Commands", "Ctrl+I", "Get Info"),
		new("Commands", "Ctrl+,", "Preferences"),
		new("Commands", "Ctrl+Alt+E", "Export subscriptions"),
		new("Commands", "Delete", "Delete the selected feed or folder (in the sidebar)"),
		new("Commands", "F1", "This help"),
		new("Commands", "Ctrl+Q", "Quit"),
	];
}
