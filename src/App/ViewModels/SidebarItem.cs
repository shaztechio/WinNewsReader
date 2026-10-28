using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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

	public SidebarItemKind Kind { get; }

	/// <summary>Identifies the row across sidebar rebuilds. A feed in two folders has two keys.</summary>
	public string Key { get; }

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

	public string UnreadCountText => UnreadCount > 0 ? UnreadCount.ToString("N0") : "";

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

	public bool HasGlyph => !IsGroup;

	public Windows.UI.Text.FontWeight TitleWeight => IsGroup ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

	public static SidebarItem Group(SidebarItemKind kind, string title, IEnumerable<SidebarItem> children)
	{
		var item = new SidebarItem(kind, kind.ToString(), title);
		foreach (var child in children)
		{
			item.Children.Add(child);
		}
		return item;
	}

	public static SidebarItem SmartFeed(SidebarItemKind kind, string title) => new(kind, kind.ToString(), title);

	public static SidebarItem ForFolder(StoredFolder folder, IEnumerable<SidebarItem> children)
	{
		var item = new SidebarItem(SidebarItemKind.Folder, $"folder:{folder.FolderId}", folder.Name) { Folder = folder };
		foreach (var child in children)
		{
			item.Children.Add(child);
		}
		return item;
	}

	public static SidebarItem ForFeed(StoredFeed feed, StoredFolder? parentFolder) =>
		new(SidebarItemKind.Feed, $"feed:{parentFolder?.FolderId}:{feed.FeedId}", Core.NameSorting.DisplayName(feed))
		{
			Feed = feed,
			ParentFolder = parentFolder,
		};

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
