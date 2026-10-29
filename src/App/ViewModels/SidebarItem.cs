using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinNewsReader.Core;
using WinNewsReader.Store;

namespace WinNewsReader.App.ViewModels;

public enum SidebarItemKind
{
	SmartFeedsGroup,
	AccountGroup,
	Today,
	AllUnread,
	Starred,
	Folder,
	Feed,
}

/// <summary>A row in the sidebar: a group header, a smart feed, a folder or a feed.</summary>
public sealed partial class SidebarItem : ObservableObject
{
	private SidebarItem(SidebarItemKind kind, string key, string title)
	{
		Kind = kind;
		Key = key;
		Title = title;
	}

	/// <summary><c>sidebarFontSize</c>: the size of the sidebar's text, in points. Set at launch, before rows are made.</summary>
	public static double TextSize { get; set; } = 14;

	public SidebarItemKind Kind { get; }

	/// <summary>Identifies the row across sidebar rebuilds. A feed in two folders has two keys.</summary>
	public string Key { get; }

	/// <summary>The account a group, folder or feed belongs to; null for smart feeds, which span every account.</summary>
	public Core.IAccount? Account { get; private init; }

	public StoredFolder? Folder { get; private init; }

	public StoredFeed? Feed { get; private set; }

	/// <summary>For a feed row, the folder it's in, or null at the top level.</summary>
	public StoredFolder? ParentFolder { get; private init; }

	public ObservableCollection<SidebarItem> Children { get; } = [];

	[ObservableProperty]
	public partial string Title { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(UnreadCountText))]
	public partial int UnreadCount { get; set; }

	[ObservableProperty]
	public partial bool IsExpanded { get; set; }

	/// <summary>The feed's favicon, once downloaded; until then, and for other rows, <see cref="Glyph"/> shows instead.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasIcon), nameof(ShowsGlyph))]
	public partial Microsoft.UI.Xaml.Media.ImageSource? Icon { get; set; }

	public bool HasIcon => Icon is not null;

	public bool ShowsGlyph => !IsGroup && Icon is null;

	/// <summary>NetNewsWire's "Unread Counts" preference, for every row.</summary>
	public static UnreadCountDisplay CountDisplay { get; set; }

	public string UnreadCountText => UnreadCount == 0 ? "" : CountDisplay switch
	{
		UnreadCountDisplay.Show => UnreadCount.ToString("N0"),
		UnreadCountDisplay.Dot => "●",
		_ => "",
	};

	public void RefreshCountText() => OnPropertyChanged(nameof(UnreadCountText));

	public bool IsGroup => Kind is SidebarItemKind.SmartFeedsGroup or SidebarItemKind.AccountGroup;

	public bool IsSmartFeed => Kind is SidebarItemKind.Today or SidebarItemKind.AllUnread or SidebarItemKind.Starred;

	/// <summary>Segoe Fluent Icons glyph. Feed icons come later (parity row SB-03).</summary>
	public string Glyph => Kind switch
	{
		SidebarItemKind.Today => "",
		SidebarItemKind.AllUnread => "",
		SidebarItemKind.Starred => "",
		SidebarItemKind.Folder => "",
		SidebarItemKind.Feed => "",
		_ => "",
	};

	public Windows.UI.Text.FontWeight TitleWeight => IsGroup ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

	public static SidebarItem Group(SidebarItemKind kind, string title, IEnumerable<SidebarItem> children, Core.IAccount? account = null)
	{
		var item = new SidebarItem(kind, KeyPrefix(account) + kind, title) { Account = account };
		foreach (var child in children)
		{
			item.Children.Add(child);
		}
		return item;
	}

	public static SidebarItem SmartFeed(SidebarItemKind kind, string title) => new(kind, kind.ToString(), title);

	public static SidebarItem ForFolder(StoredFolder folder, IEnumerable<SidebarItem> children, Core.IAccount account)
	{
		var item = new SidebarItem(SidebarItemKind.Folder, FolderKey(account, folder), folder.Name) { Folder = folder, Account = account };
		foreach (var child in children)
		{
			item.Children.Add(child);
		}
		return item;
	}

	public static SidebarItem ForFeed(StoredFeed feed, StoredFolder? parentFolder, Core.IAccount account) =>
		new(SidebarItemKind.Feed, FeedKey(account, feed.FeedId, parentFolder), Core.NameSorting.DisplayName(feed))
		{
			Feed = feed,
			ParentFolder = parentFolder,
			Account = account,
		};

	public static string FolderKey(Core.IAccount account, StoredFolder folder) => $"{KeyPrefix(account)}folder:{folder.FolderId}";

	public static string FeedKey(Core.IAccount account, string feedId, StoredFolder? parentFolder) => $"{KeyPrefix(account)}feed:{parentFolder?.FolderId}:{feedId}";

	/// <summary>Sync accounts' rows are keyed by account. The local account's keys have no prefix, as before there were accounts.</summary>
	private static string KeyPrefix(Core.IAccount? account) => account is null or { Kind: Core.AccountKind.Local } ? "" : account.AccountId + "/";

	/// <summary>Takes a refreshed copy of the feed, whose name may have changed.</summary>
	public void Update(StoredFeed feed)
	{
		Feed = feed;
		Title = Core.NameSorting.DisplayName(feed);
	}

	public IEnumerable<SidebarItem> SelfAndDescendants()
	{
		yield return this;
		foreach (var descendant in Children.SelectMany(child => child.SelfAndDescendants()))
		{
			yield return descendant;
		}
	}
}
