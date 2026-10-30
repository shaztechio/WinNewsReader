using System.ComponentModel;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using WinNewsReader.App.ViewModels;
using WinNewsReader.Core;
using WinNewsReader.Core.ArticleRendering;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using WinNewsReader.Parser.Opml;
using WinNewsReader.Store;

namespace WinNewsReader.App;

public sealed partial class MainWindow : Window
{
	private PreferencesWindow? _preferences;

	private readonly TaskbarBadge _badge;

	private DispatcherQueueTimer? _refreshTimer;
	private bool _syncingSelection;
	private bool _articleViewReady;

	private DispatcherQueueTimer? _searchTimer;

	/// <summary>For a notification click: the window comes forward, unminimized.</summary>
	public void BringToFront()
	{
		if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
		{
			presenter.Restore();
		}
		Activate();
	}

	private double _sidebarWidthBeforeHiding = 250;

	/// <summary>Navigations the app started and hasn't seen begin yet; the article pane allows only those.</summary>
	private int _pendingArticleNavigations;

	/// <summary>
	/// Proves a key message came from <see cref="ArticleKeysScript"/>, which keeps it out of reach of article and theme
	/// scripts, rather than from one of them posting a shortcut of their own.
	/// </summary>
	private static readonly string ArticleKeysToken = Guid.NewGuid().ToString("N");
	private bool _dialogOpen;
	private string? _shownArticleId;
	private RectInt32 _normalBounds;

	/// <summary>NetNewsWire's New Window: the app opens another main window, with a selection of its own.</summary>
	public event EventHandler? NewWindowRequested;

	/// <summary>
	/// <paramref name="viewModel"/>'s <see cref="MainViewModel.IsPrimary"/> says whether this is the window the app opened
	/// with, which restores the last session and does the background work, or one opened since.
	/// </summary>
	public MainWindow(MainViewModel viewModel)
	{
		ViewModel = viewModel;
		InitializeComponent();

		RestorePlacement(ViewModel.IsPrimary ? ViewModel.Settings.Window : null);
		if (ViewModel.Settings.SidebarHidden)
		{
			ShowSidebar(false);
		}
		AppWindow.Changed += (_, args) =>
		{
			// Remember the normal bounds, so a window closed while maximized un-maximizes to the right place.
			if ((args.DidPositionChange || args.DidSizeChange) && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
			{
				_normalBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
			}
		};
		AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
		SystemBackdrop = MicaController.IsSupported() ? new MicaBackdrop() : new DesktopAcrylicBackdrop();
		Sidebar.ContextFlyout = SidebarEmptySpaceMenu();
		BuildMenuCommands();
		RefreshViewMenu();
		ViewMenuBarItem.PointerEntered += (_, _) => RefreshViewMenu();
		ViewMenuBarItem.GotFocus += (_, _) => RefreshViewMenu();
		ViewModel.ArticleAppearanceChanged += (_, _) => { RefreshViewMenu(); ShowThemeName(); };
		ShowThemeName();
		// Ctrl+click and Shift+click add to the selection; the tree has already handled the click by the time it bubbles here.
		Sidebar.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnSidebarPointerPressed), true);
		ViewModel.Undo.Changed += (_, _) => UpdateUndoMenu();
		SidebarSplitter.Column = SidebarColumn;
		ArticleSplitter.Row = TimelineRow;
		TimelineSplitter.Column = TimelineColumn;
		Timeline.ItemsSource = ViewModel.Articles;
		ViewModel.PropertyChanged += OnViewModelPropertyChanged;
		ViewModel.SidebarExpansionChanged += (_, _) => ScheduleExpansionSync();
		ScheduleExpansionSync();
		// After the first layout, when the sidebar has rows to show the restored selection.
		if (ViewModel.IsPrimary)
		{
			Root.Loaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ViewModel.RestoreSelection);
		}
		Closed += (_, _) => ViewModel.SaveState(new WindowPlacement(
			_normalBounds.X, _normalBounds.Y, _normalBounds.Width, _normalBounds.Height,
			AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized },
			ViewModel.Settings.SidebarHidden ? _sidebarWidthBeforeHiding : SidebarColumn.ActualWidth, TimelineColumn.ActualWidth));
		AddShortcuts();
		_ = InitializeArticleViewAsync();

		if (ViewModel.IsPrimary)
		{
			StartBackgroundWork(refreshNow: !ViewModel.Settings.SuppressRefreshOnLaunch);
		}
		ApplyToolbarVisibility();
		ApplyStatusBarVisibility();
		ApplyPaneStyles();
		ViewModel.ColumnLayoutChanged += (_, _) => ApplyColumnLayout();
		ApplyColumnLayout();

		_badge = new TaskbarBadge(this);
		ViewModel.UnreadBadgeChanged += (_, _) => _badge.Update(ViewModel.TotalUnread, ViewModel.Settings.UnreadCounts);
		// The taskbar button may not exist yet when the first count arrives; trying again as the window comes forward catches it.
		Activated += (_, _) => _badge.Reapply(ViewModel.TotalUnread, ViewModel.Settings.UnreadCounts);
		Root.Loaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
			() => _badge.Update(ViewModel.TotalUnread, ViewModel.Settings.UnreadCounts));

		WindowAppearance.Track(this, Root);
		WindowAppearance.Apply(this, Root, ViewModel.Settings.Appearance);
		ViewModel.AppearanceChanged += (_, _) =>
		{
			WindowAppearance.Apply(this, Root, ViewModel.Settings.Appearance);
			ApplyWebColorScheme();
		};
		Closed += (_, _) =>
		{
			_refreshTimer?.Stop();
			ViewModel.Detach();
			// The app ends with its last window, so Preferences goes with its main window.
			_preferences?.Close();
			foreach (var window in _utilityWindows.Values.ToList())
			{
				window.Close();
			}
		};
	}

	/// <summary>
	/// What only one window does: refresh at launch, then as often as the reader's Refresh Feeds preference says (hourly, as
	/// in NetNewsWire, by default), and index old articles for search. When the first window closes, another takes it over.
	/// </summary>
	private void StartBackgroundWork(bool refreshNow)
	{
		_refreshTimer = DispatcherQueue.CreateTimer();
		_refreshTimer.Tick += (_, _) => _ = ViewModel.RefreshAsync();
		ApplyRefreshInterval();
		ViewModel.RefreshIntervalChanged += (_, _) => ApplyRefreshInterval();
		_ = ViewModel.PrepareSearchAsync();
		if (refreshNow)
		{
			_ = ViewModel.RefreshAsync();
		}
	}

	/// <summary>The window the app opened with has closed: this one now owns the refresh timer and the saved state.</summary>
	public void BecomePrimary()
	{
		if (!ViewModel.IsPrimary)
		{
			ViewModel.IsPrimary = true;
			StartBackgroundWork(refreshNow: false);
		}
	}

	public MainViewModel ViewModel { get; }

	/// <summary>
	/// Puts the window where it last was, if that's still on a display; otherwise opens it at the default size.
	/// Also restores the pane widths.
	/// </summary>
	private void RestorePlacement(WindowPlacement? placement)
	{
		var bounds = placement is null ? (RectInt32?)null : new RectInt32(placement.X, placement.Y, placement.Width, placement.Height);
		if (bounds is { } saved && saved.Width >= 400 && saved.Height >= 300 && DisplayArea.GetFromRect(saved, DisplayAreaFallback.None) is not null)
		{
			AppWindow.MoveAndResize(saved);
		}
		else
		{
			AppWindow.Resize(new SizeInt32(1400, 860));
		}
		_normalBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
		if (placement is null)
		{
			return;
		}
		if (placement.IsMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
		{
			presenter.Maximize();
		}
		SidebarColumn.Width = new GridLength(Math.Clamp(placement.SidebarWidth, SidebarSplitter.MinimumWidth, SidebarSplitter.MaximumWidth));
		_sidebarWidthBeforeHiding = SidebarColumn.Width.Value;
		TimelineColumn.Width = new GridLength(Math.Clamp(placement.TimelineWidth, TimelineSplitter.MinimumWidth, TimelineSplitter.MaximumWidth));
	}

	private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)
		{
			case nameof(MainViewModel.WindowTitle):
				Title = ViewModel.WindowTitle;
				break;
			case nameof(MainViewModel.SelectedSidebarItem):
				// After layout: when the sidebar was just rebuilt, the new row has no control to highlight yet.
				DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
				{
					if (Sidebar.SelectedItem != ViewModel.SelectedSidebarItem)
					{
						SyncSelection(() => Sidebar.SelectedItem = ViewModel.SelectedSidebarItem);
					}
					// Outside the tree's selection change, since it may rebuild the tree.
					ViewModel.UpdateHiddenReadFeeds();
				});
				break;
			case nameof(MainViewModel.Articles):
				// Swapping the list removes the focused row, so keep keyboard focus in the timeline.
				var timelineFocused = PaneOf(FocusManager.GetFocusedElement(Content.XamlRoot) as DependencyObject) == Pane.Timeline;
				SyncSelection(() => Timeline.ItemsSource = ViewModel.Articles);
				if (timelineFocused)
				{
					// After layout, once the new rows exist to take focus. Focusing the list selects the focused row,
					// which would mark it read, so put back the view model's selection (usually none).
					DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => SyncSelection(() =>
					{
						Timeline.Focus(FocusState.Keyboard);
						Timeline.SelectedItems.Clear();
						foreach (var selected in ViewModel.SelectedArticles)
						{
							Timeline.SelectedItems.Add(selected);
						}
					}));
				}
				break;
			case nameof(MainViewModel.SelectedArticles):
				// A selection the view model chose, such as one kept across a refresh: the list follows it.
				if (!Timeline.SelectedItems.OfType<ArticleItem>().SequenceEqual(ViewModel.SelectedArticles))
				{
					SyncSelection(() =>
					{
						Timeline.SelectedItems.Clear();
						foreach (var selected in ViewModel.SelectedArticles)
						{
							Timeline.SelectedItems.Add(selected);
						}
					});
				}
				if (ViewModel.HasMultipleSelection)
				{
					ShowArticle(null);
				}
				break;
			case nameof(MainViewModel.SelectedArticle):
				if (ViewModel.SelectedArticle is not null && !(Timeline.SelectedItems.Count == 1 && Timeline.SelectedItem == ViewModel.SelectedArticle))
				{
					SyncSelection(() => Timeline.SelectedItem = ViewModel.SelectedArticle);
				}
				if (ViewModel.SelectedArticle is { } article)
				{
					Timeline.ScrollIntoView(article);
				}
				ShowArticle(ViewModel.SelectedArticle);
				break;
		}
	}

	/// <summary>Pushes a view-model change into a control without the control's change event echoing back.</summary>
	private void SyncSelection(Action action)
	{
		_syncingSelection = true;
		try
		{
			action();
		}
		finally
		{
			_syncingSelection = false;
		}
	}

	private void OnSidebarSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
	{
		// SelectedItem still holds the old item while this event runs.
		if (!_syncingSelection)
		{
			ViewModel.SelectedSidebarItem = args.AddedItems.FirstOrDefault() as SidebarItem;
		}
	}

	// Dragging feeds between folders and accounts. Only feeds drag; folders and accounts stay put. The rows run the
	// drag themselves: the tree's built-in item dragging rearranges its own rows on a drop instead of letting the
	// app decide.

	private SidebarItem? _draggedFeed;
	private SidebarItem? _draggedFolder;

	private void OnSidebarDragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs args)
	{
		if (args.Items.FirstOrDefault() is SidebarItem { Kind: SidebarItemKind.Folder } folderItem)
		{
			// A folder can go to another account, taking its feeds.
			_draggedFolder = folderItem;
			args.Data.SetText(folderItem.Title);
			args.Data.RequestedOperation = DataPackageOperation.Move | DataPackageOperation.Copy;
		}
		else if (args.Items.FirstOrDefault() is SidebarItem { Kind: SidebarItemKind.Feed, Feed: { } feed } item)
		{
			_draggedFeed = item;
			// Dropped on another app, the feed is its URL.
			args.Data.SetText(feed.Url);
			args.Data.RequestedOperation = DataPackageOperation.Move | DataPackageOperation.Copy;
		}
		else
		{
			args.Cancel = true;
		}
	}

	private void OnSidebarDragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs args)
	{
		_draggedFeed = null;
		_draggedFolder = null;
	}

	/// <summary>
	/// NetNewsWire's drop rules: a folder or an account's top level. Within an account its own rules apply; another account
	/// subscribes to the feed. Ctrl copies instead of moving.
	/// </summary>
	private void OnSidebarRowDragOver(object sender, DragEventArgs e)
	{
		var copy = e.Modifiers.HasFlag(Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Control);
		if (_draggedFeed is { } feed && DataContextOf(sender as FrameworkElement) is SidebarItem target && MainViewModel.CanDrop(feed, target, copy))
		{
			e.AcceptedOperation = copy ? DataPackageOperation.Copy : DataPackageOperation.Move;
			var place = target.Account != feed.Account && target.Kind == SidebarItemKind.Folder ? $"{target.Account!.Name} › {target.Title}" : target.Title;
			e.DragUIOverride.Caption = $"{(copy ? "Copy" : "Move")} to {place}";
		}
		else if (_draggedFolder is { } folder && DataContextOf(sender as FrameworkElement) is SidebarItem folderTarget && MainViewModel.CanDropFolder(folder, folderTarget))
		{
			e.AcceptedOperation = copy ? DataPackageOperation.Copy : DataPackageOperation.Move;
			e.DragUIOverride.Caption = $"{(copy ? "Copy" : "Move")} to {folderTarget.Account!.Name}";
		}
		else if (_draggedFeed is null && _draggedFolder is null && CanSubscribeFrom(e.DataView))
		{
			e.AcceptedOperation = DataPackageOperation.Copy;
			e.DragUIOverride.Caption = "Subscribe";
		}
		else
		{
			e.AcceptedOperation = DataPackageOperation.None;
		}
		e.Handled = true;
	}

	/// <summary>NetNewsWire's drop of a URL on the sidebar: a link or text dragged from a browser starts Add Feed.</summary>
	private static bool CanSubscribeFrom(DataPackageView data) =>
		data.Contains(StandardDataFormats.WebLink) || data.Contains(StandardDataFormats.Text);

	private async Task SubscribeFromDropAsync(DataPackageView data, SidebarItem? target)
	{
		string? text = null;
		if (data.Contains(StandardDataFormats.WebLink))
		{
			text = (await data.GetWebLinkAsync()).AbsoluteUri;
		}
		else if (data.Contains(StandardDataFormats.Text))
		{
			text = await data.GetTextAsync();
		}
		if (DroppedUrl.Feed(text) is { } url)
		{
			await ShowNewFeedDialogAsync(url, target is { Account: not null } ? target : null);
		}
	}

	/// <summary>Dropping a link on the empty part of the sidebar subscribes in the default account.</summary>
	private void OnSidebarDragOver(object sender, DragEventArgs e)
	{
		if (_draggedFeed is null && CanSubscribeFrom(e.DataView))
		{
			e.AcceptedOperation = DataPackageOperation.Copy;
			e.DragUIOverride.Caption = "Subscribe";
		}
	}

	private async void OnSidebarDrop(object sender, DragEventArgs e)
	{
		if (_draggedFeed is null && CanSubscribeFrom(e.DataView))
		{
			var deferral = e.GetDeferral();
			try
			{
				await SubscribeFromDropAsync(e.DataView, null);
			}
			finally
			{
				deferral.Complete();
			}
		}
	}

	/// <summary>NetNewsWire's drag of articles out of the timeline: dropped on a browser or a note, they're their web addresses.</summary>
	private void OnTimelineDragItemsStarting(object sender, DragItemsStartingEventArgs args)
	{
		var links = args.Items.OfType<ArticleItem>().Select(article => article.LinkUrl).Where(link => !string.IsNullOrEmpty(link)).Select(link => link!).ToList();
		if (links.Count == 0)
		{
			args.Cancel = true;
			return;
		}
		args.Data.SetText(string.Join(Environment.NewLine, links));
		if (links.Count == 1 && Uri.TryCreate(links[0], UriKind.Absolute, out var uri))
		{
			args.Data.SetWebLink(uri);
		}
		args.Data.RequestedOperation = DataPackageOperation.Copy;
	}

	private async void OnSidebarRowDrop(object sender, DragEventArgs e)
	{
		e.Handled = true;
		var copy = e.Modifiers.HasFlag(Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Control);
		if (_draggedFeed is { } feed && DataContextOf(sender as FrameworkElement) is SidebarItem target && MainViewModel.CanDrop(feed, target, copy))
		{
			_draggedFeed = null;
			await RunReportingErrorsAsync("Couldn’t Move Feed", () => ViewModel.MoveFeedAsync(feed, target.Account!, target.Folder, copy));
		}
		else if (_draggedFolder is { } folder && DataContextOf(sender as FrameworkElement) is SidebarItem folderTarget && MainViewModel.CanDropFolder(folder, folderTarget))
		{
			_draggedFolder = null;
			string? problem = null;
			await RunReportingErrorsAsync("Couldn’t Move Folder", async () => problem = await ViewModel.MoveFolderAsync(folder, folderTarget.Account!, copy));
			if (problem is not null)
			{
				await ShowMessageAsync("Couldn’t Move Folder", problem);
			}
		}
		else if (_draggedFeed is null && _draggedFolder is null && CanSubscribeFrom(e.DataView))
		{
			var deferral = e.GetDeferral();
			try
			{
				await SubscribeFromDropAsync(e.DataView, DataContextOf(sender as FrameworkElement) as SidebarItem);
			}
			finally
			{
				deferral.Complete();
			}
		}
	}

	private bool _expansionSyncPending;

	private void ScheduleExpansionSync()
	{
		if (_expansionSyncPending)
		{
			return;
		}
		_expansionSyncPending = true;
		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
		{
			_expansionSyncPending = false;
			ApplyExpansion(pass: 0);
		});
	}

	/// <summary>
	/// Sets each row's expansion from the view model. Expanding a group creates its folders' rows, so this repeats after
	/// layout until nothing changes.
	/// </summary>
	private void ApplyExpansion(int pass)
	{
		var changed = false;
		foreach (var item in ViewModel.AllSidebarItems())
		{
			if (Sidebar.ContainerFromItem(item) is TreeViewItem row && row.IsExpanded != item.IsExpanded)
			{
				row.IsExpanded = item.IsExpanded;
				changed = true;
			}
		}
		if (changed && pass < 3)
		{
			DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ApplyExpansion(pass + 1));
		}
	}

	private void OnSidebarExpanding(TreeView sender, TreeViewExpandingEventArgs args)
	{
		if (args.Item is SidebarItem item && !item.IsExpanded)
		{
			ViewModel.SetExpanded(item, true);
		}
	}

	private void OnSidebarCollapsed(TreeView sender, TreeViewCollapsedEventArgs args)
	{
		if (args.Item is SidebarItem item && item.IsExpanded)
		{
			ViewModel.SetExpanded(item, false);
		}
	}

	private void OnTimelineSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_syncingSelection)
		{
			ViewModel.SetSelectedArticles([.. Timeline.SelectedItems.OfType<ArticleItem>()]);
		}
	}

	// Article view

	private async Task InitializeArticleViewAsync()
	{
		ArticleView.DefaultBackgroundColor = Colors.Transparent;
		await ArticleView.EnsureCoreWebView2Async();
		var core = ArticleView.CoreWebView2;
		// The page has keyboard focus while reading, so it passes the app's shortcuts back (see ArticleKeysScript).
		// Browser shortcuts such as Ctrl+R (reload) and Ctrl+P (print) are off; copy and select all still work.
		core.Settings.AreBrowserAcceleratorKeysEnabled = false;
		core.Profile.PreferredColorScheme = WindowAppearance.WebColorSchemeFor(ViewModel.Settings.Appearance);
		await core.AddScriptToExecuteOnDocumentCreatedAsync(ArticleKeysScript.Replace("[[token]]", ArticleKeysToken));
		// NetNewsWire's page scripts tidy up article content. Its Mac script reports scrolling and link hovers to the app
		// through WebKit message handlers; WebView2 shows hovered links itself, so those messages go nowhere.
		await core.AddScriptToExecuteOnDocumentCreatedAsync(WebKitMessageHandlersShim);
		foreach (var script in ViewModel.Themes.Scripts)
		{
			await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
		}
		// Feed icons for the article header, served from the favicon cache.
		Directory.CreateDirectory(MainViewModel.FeedIconsDirectory);
		core.SetVirtualHostNameToFolderMapping(MainViewModel.FeedIconsHost, MainViewModel.FeedIconsDirectory, CoreWebView2HostResourceAccessKind.Allow);
		core.WebMessageReceived += OnArticleMessage;
		// Reader View's fallback for sites that turn a plain download away: load the page in a WebView2 the reader never sees.
		ViewModel.SetBrowserFetcher(new HiddenBrowserFetcher(core.Environment, WinRT.Interop.WindowNative.GetWindowHandle(this)).FetchAsync);
		ViewModel.ArticleAppearanceChanged += (_, _) =>
		{
			_shownArticleId = "";
			ShowArticle(ViewModel.SelectedArticle);
		};
		// The pane only ever shows the article the app put there. A link the reader clicks opens in the default browser;
		// anything else, such as a script sending the page to a file:// URL or another app's protocol, goes nowhere.
		core.NavigationStarting += (_, args) =>
		{
			if (_pendingArticleNavigations > 0 && !IsWebUrl(args.Uri))
			{
				_pendingArticleNavigations--;
				return;
			}
			args.Cancel = true;
			if (args.IsUserInitiated && IsWebUrl(args.Uri))
			{
				MainViewModel.OpenInBrowser(args.Uri);
			}
		};
		// Matches are counted by the page, and a new article starts the search over.
		core.Find.MatchCountChanged += (find, _) => UpdateFindCount(find);
		core.Find.ActiveMatchIndexChanged += (find, _) => UpdateFindCount(find);
		core.NavigationCompleted += (_, _) =>
		{
			if (FindBar.Visibility == Visibility.Visible && FindBox.Text.Length > 0)
			{
				_ = StartFindAsync();
			}
		};
		// NetNewsWire's content rules: requests to ad and tracking hosts from an article are refused.
		var contentRules = ContentRules.Load(Path.Combine(AppContext.BaseDirectory, "ArticleRendering", "Resources", "ContentRules.json"));
		core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
		core.WebResourceRequested += (_, args) =>
		{
			if (contentRules.Blocks(args.Request.Uri))
			{
				args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
			}
		};
		// The status bar shows the address of a link while the pointer is over it, as NetNewsWire's does.
		core.StatusBarTextChanged += (_, _) => ViewModel.HoverUrl = core.StatusBarText ?? "";
		core.NewWindowRequested += (_, args) =>
		{
			args.Handled = true;
			if (args.IsUserInitiated)
			{
				MainViewModel.OpenInBrowser(args.Uri);
			}
		};
		core.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
		core.DownloadStarting += (_, args) => args.Cancel = true;
		// Camera, microphone, location, notifications and the rest: an article has no use for them.
		core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
		_articleViewReady = true;
		_shownArticleId = "";
		ShowArticle(ViewModel.SelectedArticle);
	}

	private static bool IsWebUrl(string url) => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

	private void ShowArticle(ArticleItem? item)
	{
		var shownId = item is null && ViewModel.HasMultipleSelection
			? MultipleSelectionShown + string.Join("|", ViewModel.SelectedArticles.Select(article => article.ArticleId))
			: item?.ArticleId;
		if (!_articleViewReady || shownId == _shownArticleId)
		{
			return;
		}
		_shownArticleId = shownId;
		var html = ViewModel.ArticlePageHtml(item);
		// Takes effect on the navigation that follows. Scripts the app adds to the page aren't affected.
		ArticleView.CoreWebView2.Settings.IsScriptEnabled = ViewModel.AllowsContentJavaScript(item);
		_pendingArticleNavigations++;
		if (html.Length < MaxNavigateToStringLength)
		{
			ArticleView.NavigateToString(html);
		}
		else
		{
			// WebView2 takes at most 2 MB of HTML as a string; a bigger article goes through a file.
			var path = Path.Combine(Path.GetTempPath(), "WinNewsReader-article.html");
			File.WriteAllText(path, html);
			ArticleView.CoreWebView2.Navigate(new Uri(path).AbsoluteUri);
		}
	}

	/// <summary>Stands for the "Multiple selection" page in <see cref="_shownArticleId"/>.</summary>
	private const string MultipleSelectionShown = "\0multiple";

	/// <summary>Under WebView2's 2 MB limit for NavigateToString, leaving room for UTF-8 encoding of non-ASCII text.</summary>
	private const int MaxNavigateToStringLength = 600_000;

	/// <summary>Stands in for the WebKit message handlers NetNewsWire's Mac page script posts to.</summary>
	private const string WebKitMessageHandlersShim = """
		window.webkit = { messageHandlers: {
			windowDidScroll: { postMessage() {} },
			mouseDidEnter: { postMessage() {} },
			mouseDidExit: { postMessage() {} },
		} };
		""";

	// Toolbar

	private void OnRefresh(object sender, RoutedEventArgs e) => _ = ViewModel.RefreshAsync();

	private async void OnNewFeed(object sender, RoutedEventArgs e) => await ShowNewFeedDialogAsync();

	private async void OnNewFolder(object sender, RoutedEventArgs e) => await ShowNewFolderDialogAsync();

	private async void OnImport(object sender, RoutedEventArgs e) => await ImportAsync();

	private async void OnExport(object sender, RoutedEventArgs e) => await ExportAsync();

	private async void OnAddAccount(object sender, RoutedEventArgs e) => await ShowAddAccountDialogAsync();

	private void OnMarkAllRead(object sender, RoutedEventArgs e) => ViewModel.MarkAllRead();

	private void OnToggleHideReadArticles(object sender, RoutedEventArgs e) => ViewModel.ToggleHideReadArticles();

	private void OnToggleHideReadFeeds(object sender, RoutedEventArgs e) => ViewModel.ToggleHideReadFeeds();

	private void OnSortNewestFirst(object sender, RoutedEventArgs e) => SetSort(oldestFirst: false, (AppBarToggleButton)sender);

	private void OnSortOldestFirst(object sender, RoutedEventArgs e) => SetSort(oldestFirst: true, (AppBarToggleButton)sender);

	/// <summary>The two sort items act as a pair; clicking the checked one leaves it checked.</summary>
	private void SetSort(bool oldestFirst, AppBarToggleButton button)
	{
		ViewModel.SetSortOldestFirst(oldestFirst);
		button.IsChecked = true;
	}

	private DataTemplate? _rowTemplate;

	/// <summary>How tall the table is in the column layout, kept for the next time it is turned on.</summary>
	private double _tableHeight = 280;

	private void OnToggleColumnLayout(object sender, RoutedEventArgs e) => ViewModel.SetColumnLayout(((ToggleMenuFlyoutItem)sender).IsChecked);

	private void OnSortByField(object sender, RoutedEventArgs e) => ViewModel.SortBy(Enum.Parse<ArticleSortField>((string)((FrameworkElement)sender).Tag));

	private void OnColumnHeader(object sender, RoutedEventArgs e) => ViewModel.SortBy(Enum.Parse<ArticleSortField>((string)((FrameworkElement)sender).Tag));

	/// <summary>NetNewsWire's column layout: the timeline as a table, with a header to sort by.</summary>
	private void ApplyColumnLayout()
	{
		_rowTemplate ??= Timeline.ItemTemplate;
		var on = ViewModel.UseColumnLayout;
		Timeline.ItemTemplate = on ? (DataTemplate)Root.Resources["ColumnRowTemplate"] : _rowTemplate;
		ColumnHeader.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
		Timeline.Margin = new Thickness(0, on ? ColumnHeader.Height : 0, 0, 0);
		// NetNewsWire's column layout, as a mail reader has it: the table across the top and the article below it,
		// both as wide as everything right of the sidebar.
		var span = on ? 3 : 1;
		Grid.SetColumnSpan(Timeline, span);
		Grid.SetColumnSpan(ColumnHeader, span);
		Grid.SetColumnSpan(NoFeedPanel, span);
		Grid.SetRow(ArticlePane, on ? 3 : 1);
		Grid.SetColumn(ArticlePane, on ? 2 : 4);
		Grid.SetColumnSpan(ArticlePane, span);
		TimelineSplitter.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
		ArticleSplitter.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
		SplitterRow.Height = new GridLength(on ? 6 : 0);
		if (on)
		{
			TimelineRow.Height = new GridLength(_tableHeight);
			ArticleRow.Height = new GridLength(1, GridUnitType.Star);
		}
		else
		{
			if (TimelineRow.ActualHeight > 0 && ArticleRow.Height.IsStar)
			{
				_tableHeight = TimelineRow.ActualHeight;
			}
			TimelineRow.Height = new GridLength(1, GridUnitType.Star);
			ArticleRow.Height = new GridLength(0);
		}
		UpdateColumnHeaders();
	}

	/// <summary>The sort column's header is bold, with an arrow for its direction where there's room for one.</summary>
	private void UpdateColumnHeaders()
	{
		var field = ViewModel.ColumnSortField;
		var up = field switch
		{
			ArticleSortField.Date or ArticleSortField.Feed or ArticleSortField.Title => ViewModel.ColumnSortReversed == (field == ArticleSortField.Date),
			_ => ViewModel.ColumnSortReversed,
		};
		var arrow = up ? " ▲" : " ▼";
		void Set(TextBlock text, string name, ArticleSortField column)
		{
			text.Text = field == column ? name + arrow : name;
			text.FontWeight = field == column ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
		}
		Set(HeaderFeedText, "Feed", ArticleSortField.Feed);
		Set(HeaderTitleText, "Title", ArticleSortField.Title);
		Set(HeaderDateText, "Date", ArticleSortField.Date);
		// The icon columns have no room for an arrow, so the sort column's icon is just stronger than the others.
		HeaderUnread.Opacity = field == ArticleSortField.Unread ? 1 : 0.5;
		HeaderStarred.Opacity = field == ArticleSortField.Starred ? 1 : 0.5;
	}

	private void OnToggleGroupByFeed(object sender, RoutedEventArgs e) => ViewModel.SetGroupByFeed(((ToggleMenuFlyoutItem)sender).IsChecked);

	private void OnToggleSearchScope(object sender, RoutedEventArgs e) => ViewModel.SearchCurrentTimeline = ((Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)sender).IsChecked == true;

	/// <summary>NetNewsWire's hidden preferences for the panes: font sizes and separators between timeline rows.</summary>
	private void ApplyPaneStyles()
	{
		var settings = ViewModel.Settings;
		DebugMenu.Visibility = settings.ShowDebugMenu ? Visibility.Visible : Visibility.Collapsed;
		// The rows' own styles set a font size, so it has to be set on them rather than inherited from the list.
		if (settings.SidebarFontSize > 0)
		{
			SidebarItem.TextSize = settings.SidebarFontSize;
		}
		if (settings.TimelineFontSize > 0)
		{
			var style = new Style(typeof(ListViewItem)) { BasedOn = (Style)Application.Current.Resources["DefaultListViewItemStyle"] };
			style.Setters.Add(new Setter(Control.FontSizeProperty, settings.TimelineFontSize));
			Timeline.ItemContainerStyle = style;
		}
		ArticleItem.SeparatorThickness = settings.TimelineSeparators ? new Thickness(0, 0, 0, 1) : new Thickness(0);
	}

	private void OnCleanUp(object sender, RoutedEventArgs e) => ViewModel.CleanUp();

	/// <summary>Searches once typing pauses, as NetNewsWire's search field does, rather than on every key.</summary>
	private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
	{
		_searchTimer ??= CreateSearchTimer();
		_searchTimer.Stop();
		_searchTimer.Start();
	}

	private DispatcherQueueTimer CreateSearchTimer()
	{
		var timer = DispatcherQueue.CreateTimer();
		timer.Interval = TimeSpan.FromMilliseconds(250);
		timer.IsRepeating = false;
		timer.Tick += (_, _) => ViewModel.ApplySearch();
		return timer;
	}

	/// <summary>Enter searches now and moves to the results; Escape ends the search.</summary>
	private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key == VirtualKey.Escape)
		{
			_searchTimer?.Stop();
			ViewModel.EndSearch();
			FocusPane(Pane.Timeline);
			e.Handled = true;
		}
		else if (e.Key == VirtualKey.Enter)
		{
			_searchTimer?.Stop();
			ViewModel.ApplySearch();
			FocusPane(Pane.Timeline);
			e.Handled = true;
		}
	}

	private void OnToggleArticleJavaScript(object sender, RoutedEventArgs e) => ViewModel.SetArticleJavaScriptEnabled(!ViewModel.IsArticleJavaScriptEnabled);

	/// <summary>Fills the New menu's Article Theme and Text Size submenus, which change as themes come and go.</summary>
	/// <summary>
	/// The View menu's Article Theme and Text Size submenus change as themes come and go and as the reader picks them, so they're
	/// filled again when something might have changed: as the menu is reached for, and when a theme or size is chosen.
	/// </summary>
	private void RefreshViewMenu()
	{
		FillThemeMenu(ThemeMenu.Items);
		FillTextSizeMenu(TextSizeMenu);
	}

	private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

	/// <summary>Raised by Exit (Ctrl+Q): the app closes every window.</summary>
	public event EventHandler? ExitRequested;

	private Dictionary<string, Action> _menuCommands = [];

	/// <summary>The menu bar's items that have no handler of their own: each is named by its <c>Tag</c>.</summary>
	private void BuildMenuCommands() => _menuCommands = new()
	{
		["Exit"] = () => ExitRequested?.Invoke(this, EventArgs.Empty),
		["CopyUrl"] = () => CopyToClipboard(ViewModel.SelectedArticle?.LinkUrl),
		["CopyExternalUrl"] = CopyExternalUrl,
		["ArticleSearch"] = () => SearchBox.Focus(FocusState.Keyboard),
		["Today"] = () => ViewModel.SelectSmartFeed(SidebarItemKind.Today),
		["AllUnread"] = () => ViewModel.SelectSmartFeed(SidebarItemKind.AllUnread),
		["Starred"] = () => ViewModel.SelectSmartFeed(SidebarItemKind.Starred),
		["MarkAbove"] = ViewModel.MarkAboveRead,
		["MarkBelow"] = ViewModel.MarkBelowRead,
		["MarkOlder"] = ViewModel.MarkOlderRead,
		["MainWindow"] = Activate,
		["Info"] = () => _ = ShowInspectorAsync(null),
	};

	private void OnMenuCommand(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { Tag: string key } && _menuCommands.TryGetValue(key, out var run))
		{
			run();
		}
	}

	/// <summary>The article's external link, when it has one that differs from its own address.</summary>
	private void CopyExternalUrl() =>
		CopyToClipboard(ViewModel.SelectedArticle is { } article && article.Article.ExternalUrl is { } external && external != article.LinkUrl ? external : null);

	/// <summary>The themes, with the current one checked, then the themes folder, where the reader adds .nnwtheme folders.</summary>
	private void FillThemeMenu(IList<MenuFlyoutItemBase> items)
	{
		items.Clear();
		ViewModel.Themes.Reload();
		var current = ViewModel.CurrentTheme.Name;
		foreach (var theme in ViewModel.Themes.All)
		{
			var item = new RadioMenuFlyoutItem { Text = theme.Name, GroupName = "ArticleTheme", IsChecked = theme.Name == current };
			item.Click += (_, _) => ViewModel.SetTheme(theme.Name);
			items.Add(item);
			if (theme == ViewModel.Themes.Default)
			{
				items.Add(new MenuFlyoutSeparator());
			}
		}
		items.Add(new MenuFlyoutSeparator());
		items.Add(MenuItem("Install Theme…", async () => await InstallThemeFromFileAsync()));
		items.Add(MenuItem("Install Theme from URL…", async () => await InstallThemeFromUrlAsync()));
		items.Add(MenuItem("Open Themes Folder", ViewModel.OpenThemesFolder));
	}

	/// <summary>A zipped theme, or the Info.plist in a .nnwtheme folder: the file picker can't pick folders.</summary>
	private async Task InstallThemeFromFileAsync()
	{
		var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
		picker.FileTypeFilter.Add(".zip");
		picker.FileTypeFilter.Add(".plist");
		WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
		if (await picker.PickSingleFileAsync() is { } file)
		{
			await InstallThemeAsync(() => Task.FromResult(ViewModel.ThemeInstaller.Open(file.Path)));
		}
	}

	private async Task InstallThemeFromUrlAsync()
	{
		if (await PromptAsync("Install Theme from URL", "Download", "URL of a zipped NetNewsWire theme", await ClipboardUrlAsync() ?? "") is { } url)
		{
			await InstallThemeAsync(() => ViewModel.ThemeInstaller.DownloadAsync(url));
		}
	}

	/// <summary>NetNewsWire's <c>importTheme(url:)</c>: confirm with the theme's author, ask before overwriting, then confirm it's in.</summary>
	private async Task InstallThemeAsync(Func<Task<ArticleThemePackage>> open)
	{
		ArticleThemePackage package;
		try
		{
			package = await open();
		}
		catch (Exception ex) when (ex is ArticleThemeInstallException or IOException or UnauthorizedAccessException or HttpRequestException)
		{
			await ShowMessageAsync("Couldn’t Install Theme", ex.Message);
			return;
		}
		using (package)
		{
			var theme = package.Theme;
			var details = new StackPanel { Spacing = 4, MaxWidth = 420 };
			if (theme.CreatorHomePage is { Length: > 0 } homePage)
			{
				details.Children.Add(new TextBlock { Text = "Author’s website:" });
				details.Children.Add(Uri.TryCreate(homePage, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
					? new HyperlinkButton { Content = homePage, NavigateUri = uri, Padding = new Thickness(0) }
					: new TextBlock { Text = homePage, TextWrapping = TextWrapping.Wrap });
			}
			// The author's name and website come from the theme itself; where it was downloaded from is the one fact it can't fake.
			if (package.Source is { } source)
			{
				details.Children.Add(new TextBlock { Text = $"Downloaded from {source.Host}" + (source.Scheme == "http" ? " over an insecure connection (http)." : "."), TextWrapping = TextWrapping.Wrap });
			}
			if (package.RunsScripts)
			{
				details.Children.Add(new TextBlock
				{
					Text = "This theme runs scripts in every article you read with it. Install it only if you trust where it came from.",
					TextWrapping = TextWrapping.Wrap,
					Style = (Style)Application.Current.Resources["CautionTextStyle"],
				});
			}
			if (await ShowDialogAsync(Dialog($"Install theme “{theme.Name}” by {theme.CreatorName}?", "Install Theme", details)) != ContentDialogResult.Primary)
			{
				return;
			}
			if (ViewModel.ThemeInstaller.Exists(package)
				&& await ShowDialogAsync(Dialog($"The theme “{theme.Name}” already exists. Overwrite it?", "Overwrite", "")) != ContentDialogResult.Primary)
			{
				return;
			}
			try
			{
				ViewModel.ThemeInstaller.Install(package);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				await ShowMessageAsync("Couldn’t Install Theme", ex.Message);
				return;
			}
			ViewModel.ReloadThemes();
			await ShowMessageAsync("Theme installed", $"The theme “{theme.Name}” has been installed.");
		}
	}

	private void FillTextSizeMenu(MenuFlyoutSubItem menu)
	{
		menu.Items.Clear();
		foreach (var (size, name) in new[]
		{
			(ArticleTextSize.Small, "Small"),
			(ArticleTextSize.Medium, "Medium"),
			(ArticleTextSize.Large, "Large"),
			(ArticleTextSize.ExtraLarge, "Extra Large"),
			(ArticleTextSize.ExtraExtraLarge, "Extra Extra Large"),
		})
		{
			var item = new RadioMenuFlyoutItem { Text = name, GroupName = "TextSize", IsChecked = ViewModel.Settings.ArticleTextSize == size };
			item.Click += (_, _) => ViewModel.SetTextSize(size);
			menu.Items.Add(item);
		}
	}

	private void OnToggleRead(object sender, RoutedEventArgs e) => ViewModel.ToggleRead();

	// Undo and Redo (Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z) for marking read and starred, and deleting feeds and folders.

	private Task UndoAsync() => RunReportingErrorsAsync("Couldn’t Undo", ViewModel.Undo.UndoAsync);

	private Task RedoAsync() => RunReportingErrorsAsync("Couldn’t Redo", ViewModel.Undo.RedoAsync);

	private void OnUndo(object sender, RoutedEventArgs e) => _ = UndoAsync();

	private void OnRedo(object sender, RoutedEventArgs e) => _ = RedoAsync();

	private void UpdateUndoMenu()
	{
		var undo = ViewModel.Undo;
		UndoItem.Text = undo.UndoLabel is { } undoLabel ? $"Undo {undoLabel}" : "Undo";
		UndoItem.IsEnabled = undo.CanUndo;
		RedoItem.Text = undo.RedoLabel is { } redoLabel ? $"Redo {redoLabel}" : "Redo";
		RedoItem.IsEnabled = undo.CanRedo;
	}

	// Find in Article: Ctrl+F opens the bar, F3 and Shift+F3 move between matches, Esc closes it.

	private void ShowFindBar()
	{
		FindBar.Visibility = Visibility.Visible;
		FindBox.Focus(FocusState.Keyboard);
		FindBox.SelectAll();
		if (FindBox.Text.Length > 0)
		{
			_ = StartFindAsync();
		}
	}

	private void HideFindBar()
	{
		FindBar.Visibility = Visibility.Collapsed;
		if (_articleViewReady)
		{
			ArticleView.CoreWebView2.Find.Stop();
		}
		FocusPane(Pane.Timeline);
	}

	private async Task StartFindAsync()
	{
		if (!_articleViewReady)
		{
			return;
		}
		var find = ArticleView.CoreWebView2.Find;
		if (FindBox.Text.Length == 0)
		{
			find.Stop();
			FindCount.Text = "";
			return;
		}
		var options = ArticleView.CoreWebView2.Environment.CreateFindOptions();
		options.FindTerm = FindBox.Text;
		options.ShouldHighlightAllMatches = true;
		options.SuppressDefaultFindDialog = true;
		await find.StartAsync(options);
		UpdateFindCount(find);
	}

	/// <summary>The page reports which match is active a moment after it moves, so the count is read again once it has.</summary>
	private void RefreshFindCountSoon()
	{
		_findCountTimer ??= DispatcherQueue.CreateTimer();
		_findCountTimer.Interval = TimeSpan.FromMilliseconds(120);
		_findCountTimer.IsRepeating = false;
		_findCountTimer.Tick -= OnFindCountTimer;
		_findCountTimer.Tick += OnFindCountTimer;
		_findCountTimer.Stop();
		_findCountTimer.Start();
	}

	private void OnFindCountTimer(DispatcherQueueTimer sender, object args)
	{
		if (_articleViewReady)
		{
			UpdateFindCount(ArticleView.CoreWebView2.Find);
		}
	}

	private DispatcherQueueTimer? _findCountTimer;

	private void UpdateFindCount(CoreWebView2Find find)
	{
		FindCount.Text = FindBox.Text.Length == 0 ? "" : find.MatchCount == 0 ? "No matches" : $"{find.ActiveMatchIndex} of {find.MatchCount}";
	}

	private void FindNext()
	{
		if (FindBar.Visibility != Visibility.Visible || FindBox.Text.Length == 0)
		{
			ShowFindBar();
		}
		else if (_articleViewReady)
		{
			ArticleView.CoreWebView2.Find.FindNext();
			RefreshFindCountSoon();
		}
	}

	private void FindPrevious()
	{
		if (FindBar.Visibility != Visibility.Visible || FindBox.Text.Length == 0)
		{
			ShowFindBar();
		}
		else if (_articleViewReady)
		{
			ArticleView.CoreWebView2.Find.FindPrevious();
			RefreshFindCountSoon();
		}
	}

	private void OnFindTextChanged(object sender, TextChangedEventArgs e) => _ = StartFindAsync();

	private void OnFindKeyDown(object sender, KeyRoutedEventArgs e)
	{
		switch (e.Key)
		{
			case VirtualKey.Escape:
				HideFindBar();
				e.Handled = true;
				break;
			case VirtualKey.Enter when CurrentModifiers() == VirtualKeyModifiers.Shift:
			case VirtualKey.F3 when CurrentModifiers() == VirtualKeyModifiers.Shift:
				FindPrevious();
				e.Handled = true;
				break;
			case VirtualKey.Enter:
			case VirtualKey.F3:
				FindNext();
				e.Handled = true;
				break;
		}
	}

	private void OnFindInArticle(object sender, RoutedEventArgs e) => ShowFindBar();

	private void OnFullScreen(object sender, RoutedEventArgs e) => ToggleFullScreen();

	private void OnFindNext(object sender, RoutedEventArgs e) => FindNext();

	private void OnFindPrevious(object sender, RoutedEventArgs e) => FindPrevious();

	private void OnCloseFind(object sender, RoutedEventArgs e) => HideFindBar();

	/// <summary>NetNewsWire's Full Screen (F11).</summary>
	private void ToggleFullScreen() => AppWindow.SetPresenter(AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen
		? AppWindowPresenterKind.Overlapped
		: AppWindowPresenterKind.FullScreen);

	private void ApplyRefreshInterval()
	{
		if (_refreshTimer is null)
		{
			return;
		}
		_refreshTimer.Stop();
		if (RefreshIntervals.ToTimeSpan(ViewModel.Settings.RefreshIntervalMinutes) is { } interval)
		{
			_refreshTimer.Interval = interval;
			_refreshTimer.Start();
		}
	}

	/// <summary>Articles see the app's Light or Dark choice as <c>prefers-color-scheme</c>, so a theme's dark styles go with it.</summary>
	private void ApplyWebColorScheme()
	{
		if (_articleViewReady)
		{
			ArticleView.CoreWebView2.Profile.PreferredColorScheme = WindowAppearance.WebColorSchemeFor(ViewModel.Settings.Appearance);
		}
	}

	private void OnPreferences(object sender, RoutedEventArgs e) => OpenPreferences();

	/// <summary>Add Feed with an address from a <c>feed:</c> link someone opened.</summary>
	public void OpenFeedLink(string url)
	{
		BringToFront();
		_ = ShowNewFeedDialogAsync(url);
	}

	/// <summary>A theme file someone opened, such as a downloaded .nnwtheme zip.</summary>
	public void InstallThemeFile(string path)
	{
		BringToFront();
		_ = InstallThemeAsync(() => Task.FromResult(ViewModel.ThemeInstaller.Open(path)));
	}

	/// <summary>What the first window does once it's up: show the report of a crash last time, then look for an update.</summary>
	public async Task StartupChecksAsync()
	{
		await ShowCrashReportsAsync();
		if (ViewModel.Settings.CheckForUpdatesAutomatically)
		{
			await CheckForUpdatesAsync(userAsked: false);
		}
	}

	/// <summary>
	/// NetNewsWire's crash reporter, kept on this PC: after a crash, the next start says so and offers the report. Nothing is
	/// sent; Report a Problem opens a new issue for the reader to describe and paste the report into.
	/// </summary>
	private async Task ShowCrashReportsAsync()
	{
		var pending = CrashLog.Pending(App.CrashDirectory);
		if (pending.Count == 0)
		{
			return;
		}
		var dialog = new ContentDialog
		{
			XamlRoot = Content.XamlRoot,
			Title = "WinNewsReader quit unexpectedly",
			Content = new TextBlock
			{
				Text = "A report of what went wrong was saved on this PC. Nothing was sent anywhere. You can read it, and if you'd like to help fix the problem, report it on GitHub and paste the report in.",
				TextWrapping = TextWrapping.Wrap,
			},
			PrimaryButtonText = "Show Report",
			SecondaryButtonText = "Report a Problem",
			CloseButtonText = "Dismiss",
			DefaultButton = ContentDialogButton.Primary,
		};
		dialog.CloseButtonClick += (_, _) => dialog.Tag = "closed-by-reader";
		var result = await ShowDialogAsync(dialog);
		if (result == ContentDialogResult.None && dialog.Tag is not "closed-by-reader")
		{
			// Another dialog was open, so this one never appeared; the report stays for the next start.
			return;
		}
		if (result == ContentDialogResult.Primary)
		{
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pending[^1]) { UseShellExecute = true });
		}
		else if (result == ContentDialogResult.Secondary)
		{
			MainViewModel.OpenInBrowser("https://github.com/shaztechio/WinNewsReader/issues/new?title=" + Uri.EscapeDataString("Crash: " + Path.GetFileNameWithoutExtension(pending[^1])));
		}
		foreach (var report in pending)
		{
			CrashLog.Dismiss(report);
		}
	}

	/// <summary>
	/// NetNewsWire's Check for Updates. At launch it says nothing unless there's something new; asked for, it also says when
	/// there isn't, or why it can't look.
	/// </summary>
	public async Task CheckForUpdatesAsync(bool userAsked)
	{
		var updates = new UpdateService(ViewModel.Settings.UpdateTestBuilds);
		if (!updates.IsInstalled)
		{
			if (userAsked)
			{
				await ShowMessageAsync("Check for Updates", UpdateService.IsStorePackage
					? $"WinNewsReader {UpdateService.CurrentVersion} came from the Microsoft Store, which installs its updates."
					: $"This copy of WinNewsReader {UpdateService.CurrentVersion} isn’t installed, so it can’t update itself. Installed copies check for updates.");
			}
			return;
		}
		try
		{
			if (await updates.CheckAsync() is not { } update)
			{
				if (userAsked)
				{
					await ShowMessageAsync("Check for Updates", $"WinNewsReader {UpdateService.CurrentVersion} is the latest version.");
				}
				return;
			}
			var dialog = Dialog("Update Available", "Install and Restart", new TextBlock
			{
				Text = $"WinNewsReader {update.TargetFullRelease.Version} is available; you have {UpdateService.CurrentVersion}.",
				TextWrapping = TextWrapping.Wrap,
			});
			dialog.CloseButtonText = "Later";
			if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
			{
				await updates.InstallAsync(update);
			}
		}
		catch (Exception ex) when (userAsked)
		{
			await ShowMessageAsync("Couldn’t Check for Updates", ex.Message);
		}
		catch (Exception)
		{
			// A failed check at launch isn't worth interrupting for; the next launch tries again.
		}
	}

	/// <summary>NetNewsWire's Preferences (Cmd+,): one window, brought forward if it's already open.</summary>
	public void OpenPreferences()
	{
		if (_preferences is null)
		{
			_preferences = new PreferencesWindow(ViewModel, () => CheckForUpdatesAsync(userAsked: true));
			_preferences.Closed += (_, _) => _preferences = null;
		}
		_preferences.Activate();
	}

	// NetNewsWire's utility windows: each opens once and is brought forward if it's already open.
	private readonly Dictionary<string, Window> _utilityWindows = [];

	private void OnUtilityWindow(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { Tag: string key })
		{
			OpenUtilityWindow(key);
		}
	}

	private void OpenUtilityWindow(string key)
	{
		if (!_utilityWindows.TryGetValue(key, out var window))
		{
			window = key switch
			{
				"Dinosaurs" => new DinosaursWindow(ViewModel),
				"CurrentActivity" => new CurrentActivityWindow(ViewModel),
				"ActivityLog" => new ActivityLogWindow(ViewModel, errorsOnly: false),
				"ErrorLog" => new ActivityLogWindow(ViewModel, errorsOnly: true),
				"Shortcuts" => new KeyboardShortcutsWindow(ViewModel),
				_ => new AccountStatsWindow(ViewModel),
			};
			_utilityWindows[key] = window;
			window.Closed += (_, _) => _utilityWindows.Remove(key);
		}
		window.Activate();
	}

	/// <summary>NetNewsWire's double-click on a feed: its home page opens in the browser.</summary>
	private void OnSidebarDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
	{
		if ((e.OriginalSource as FrameworkElement)?.DataContext is SidebarItem { Feed: not null } item)
		{
			if (ViewModel.Settings.DoubleClickFeedMarksRead)
			{
				ViewModel.MarkAllRead(item);
			}
			else if (item.Feed!.HomePageUrl is { } homePage)
			{
				MainViewModel.OpenInBrowser(homePage);
			}
			e.Handled = true;
		}
	}

	/// <summary>NetNewsWire's menu for right-clicking nothing in the sidebar: New Feed and New Folder.</summary>
	private MenuFlyout SidebarEmptySpaceMenu()
	{
		var menu = new MenuFlyout();
		menu.Items.Add(MenuItem("New Feed…", () => _ = ShowNewFeedDialogAsync()));
		menu.Items.Add(MenuItem("New Folder…", () => _ = ShowNewFolderDialogAsync()));
		if (ViewModel.Settings.ToolbarHidden)
		{
			menu.Items.Add(new MenuFlyoutSeparator());
			menu.Items.Add(MenuItem("Show Toolbar", ToggleToolbar));
		}
		return menu;
	}

	private void OnToggleSidebar(object sender, RoutedEventArgs e) => ToggleSidebar();

	/// <summary>NetNewsWire's Hide Toolbar / Show Toolbar (Ctrl+Alt+T). With it hidden, the sidebar's empty-space menu still has Show Toolbar.</summary>
	private void ToggleToolbar()
	{
		ViewModel.Settings.ToolbarHidden = !ViewModel.Settings.ToolbarHidden;
		ViewModel.SaveSettings();
		ApplyToolbarVisibility();
	}

	private void ApplyToolbarVisibility()
	{
		var visibility = ViewModel.Settings.ToolbarHidden ? Visibility.Collapsed : Visibility.Visible;
		ToolbarMenuItem.IsChecked = !ViewModel.Settings.ToolbarHidden;
		SidebarToolbar.Visibility = visibility;
		TimelineToolbar.Visibility = visibility;
		ArticleToolbar.Visibility = visibility;
	}

	private void OnToggleToolbar(object sender, RoutedEventArgs e) => ToggleToolbar();

	/// <summary>NetNewsWire's Show Status Bar / Hide Status Bar.</summary>
	private void OnToggleStatusBar(object sender, RoutedEventArgs e)
	{
		ViewModel.Settings.StatusBarHidden = !((ToggleMenuFlyoutItem)sender).IsChecked;
		ViewModel.SaveSettings();
		ApplyStatusBarVisibility();
	}

	private void ApplyStatusBarVisibility()
	{
		StatusBar.Visibility = ViewModel.Settings.StatusBarHidden ? Visibility.Collapsed : Visibility.Visible;
		StatusBarItem.IsChecked = !ViewModel.Settings.StatusBarHidden;
	}

	private void OnNewWindow(object sender, RoutedEventArgs e) => NewWindowRequested?.Invoke(this, EventArgs.Empty);

	// NetNewsWire's debug items, behind the ShowDebugMenu setting.

	private void OnOpenWebInspector(object sender, RoutedEventArgs e)
	{
		if (_articleViewReady)
		{
			ArticleView.CoreWebView2.OpenDevToolsWindow();
		}
	}

	private void OnOpenIconCache(object sender, RoutedEventArgs e)
	{
		Directory.CreateDirectory(MainViewModel.FeedIconsDirectory);
		System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(MainViewModel.FeedIconsDirectory) { UseShellExecute = true });
	}

	private async void OnRebuildSearchIndex(object sender, RoutedEventArgs e) =>
		await RunReportingErrorsAsync("Couldn’t Rebuild the Search Index", () => Task.Run(() =>
		{
			foreach (var account in ViewModel.Accounts.ToList())
			{
				account.Store.RebuildSearchIndex();
			}
		}));

	private void OnOpenAppDataFolder(object sender, RoutedEventArgs e)
	{
		Directory.CreateDirectory(App.DataDirectory);
		System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(App.DataDirectory) { UseShellExecute = true });
	}

	/// <summary>NetNewsWire's About window: the version, and credit to NetNewsWire, whose code and design this app ports.</summary>
	private void OnAbout(object sender, RoutedEventArgs e) => _ = ShowAboutAsync();

	private async Task ShowAboutAsync()
	{
		var version = typeof(MainWindow).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";
		var panel = new StackPanel { Spacing = 8, MaxWidth = 420 };
		panel.Children.Add(new TextBlock { Text = $"Version {version}", Style = (Style)Application.Current.Resources["SecondaryTextStyle"] });
		panel.Children.Add(new TextBlock
		{
			Text = "A Windows port of NetNewsWire, the free and open source feed reader by Brent Simmons and the NetNewsWire team. "
				+ "The parser, store, article themes and design follow NetNewsWire's.",
			TextWrapping = TextWrapping.Wrap,
		});
		panel.Children.Add(new TextBlock
		{
			Text = "NetNewsWire is Copyright © 2002–present Brent Simmons, released under the MIT License.",
			TextWrapping = TextWrapping.Wrap,
			Style = (Style)Application.Current.Resources["SecondaryCaptionStyle"],
		});
		var link = new HyperlinkButton { Content = "winnewsreader.com", NavigateUri = new Uri("https://winnewsreader.com/"), Padding = new Thickness(0) };
		panel.Children.Add(link);
		await ShowDialogAsync(new ContentDialog { XamlRoot = Content.XamlRoot, Title = "About WinNewsReader", Content = panel, CloseButtonText = "OK" });
	}

	// NetNewsWire's Help menu (HelpURL), with this project's pages: help on the website, the repository and its issues.
	private const string HelpUrl = "https://winnewsreader.com/help/";

	private void OnHelp(object sender, RoutedEventArgs e) => MainViewModel.OpenInBrowser(HelpUrl);

	/// <summary>NetNewsWire's Keyboard Shortcuts window: the shortcuts, listed in a window of their own.</summary>
	private void OnKeyboardShortcutsHelp(object sender, RoutedEventArgs e) => OpenUtilityWindow("Shortcuts");

	private async void OnCheckForUpdates(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(userAsked: true);

	private void OnReleaseNotes(object sender, RoutedEventArgs e) => MainViewModel.OpenInBrowser("https://winnewsreader.com/news/");

	private void OnWebsite(object sender, RoutedEventArgs e) => MainViewModel.OpenInBrowser("https://winnewsreader.com/");

	private void OnSourceCode(object sender, RoutedEventArgs e) => MainViewModel.OpenInBrowser("https://github.com/shaztechio/WinNewsReader");

	/// <summary>NetNewsWire's Help → Add NetNewsWire News Feed: subscribes On This PC to this app's own news.</summary>
	private async void OnAddNewsFeed(object sender, RoutedEventArgs e) =>
		await RunReportingErrorsAsync("Couldn’t Add the News Feed", () => ViewModel.SubscribeAsync("https://winnewsreader.com/feed.xml", ViewModel.Accounts[0], null));

	private void OnReportProblem(object sender, RoutedEventArgs e) => MainViewModel.OpenInBrowser("https://github.com/shaztechio/WinNewsReader/issues");

	/// <summary>
	/// NetNewsWire's Toggle Sidebar. The sidebar's toolbar buttons stay in reach by moving over the timeline, as the
	/// Mac's toolbar does when the sidebar collapses.
	/// </summary>
	private void ToggleSidebar() => ShowSidebar(ViewModel.Settings.SidebarHidden);

	private void ShowSidebar(bool show)
	{
		if (show)
		{
			SidebarColumn.Width = new GridLength(Math.Max(_sidebarWidthBeforeHiding, SidebarSplitter.MinimumWidth));
		}
		else if (SidebarColumn.ActualWidth > 0)
		{
			_sidebarWidthBeforeHiding = SidebarColumn.ActualWidth;
			SidebarColumn.Width = new GridLength(0);
		}
		Sidebar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
		SidebarMenuItem.IsChecked = show;
		SidebarSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
		// Over the timeline, the group sits at the left, its buttons together, clear of the timeline's own.
		Grid.SetColumn(SidebarToolbar, show ? 0 : 2);
		SidebarToolbar.HorizontalAlignment = show ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
		if (ViewModel.Settings.SidebarHidden == show)
		{
			ViewModel.Settings.SidebarHidden = !show;
			ViewModel.SaveSettings();
		}
	}

	/// <summary>Shares the article's link and title through Windows' share sheet, in place of NetNewsWire's sharing services.</summary>
	private void OnShare(object sender, RoutedEventArgs e) => Share(ViewModel.SelectedArticles);

	/// <summary>
	/// The articles' links through Windows' share sheet: one article as a web link with its title, several as their
	/// links, oldest first, as NetNewsWire orders them.
	/// </summary>
	private void Share(IReadOnlyList<ArticleItem> articles)
	{
		var links = articles.OrderBy(article => article.Article.LogicalDatePublished).Select(article => article.LinkUrl).OfType<string>().ToList();
		if (links.Count == 0)
		{
			return;
		}
		var window = WinRT.Interop.WindowNative.GetWindowHandle(this);
		var manager = DataTransferManagerInterop.GetForWindow(window);
		void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
		{
			sender.DataRequested -= OnDataRequested;
			if (links.Count == 1 && Uri.TryCreate(links[0], UriKind.Absolute, out var uri))
			{
				args.Request.Data.Properties.Title = articles.First(article => article.LinkUrl == links[0]).Title is { Length: > 0 } title ? title : links[0];
				args.Request.Data.SetWebLink(uri);
			}
			else
			{
				args.Request.Data.Properties.Title = $"{links.Count} Articles";
			}
			args.Request.Data.SetText(string.Join(Environment.NewLine, links));
		}
		manager.DataRequested += OnDataRequested;
		DataTransferManagerInterop.ShowShareUIForWindow(window);
	}

	private void OnToggleStarred(object sender, RoutedEventArgs e) => ViewModel.ToggleStarred();

	private void OnNextUnread(object sender, RoutedEventArgs e) => ViewModel.SelectNextUnread();

	private void OnToggleReaderView(object sender, RoutedEventArgs e) => ViewModel.ToggleReaderView();

	/// <summary>NetNewsWire opens every selected article, each in its own browser tab.</summary>
	private void OnOpenInBrowser(object sender, RoutedEventArgs e)
	{
		foreach (var article in ViewModel.SelectedArticles)
		{
			MainViewModel.OpenInBrowser(article.LinkUrl);
		}
	}

	// Context menus

	/// <summary>NetNewsWire's sidebar menus (parity rows SB-11 to SB-13), minus items whose features aren't built yet.</summary>
	private void OnSidebarMenuOpening(object? sender, object e)
	{
		var menu = (MenuFlyout)sender!;
		menu.Items.Clear();
		if (DataContextOf(menu.Target) is SidebarItem item)
		{
			FillSidebarMenu(menu, item);
		}
	}

	/// <summary>The status bar's gear menu: the sidebar's menu for the row that's selected, as in NetNewsWire.</summary>
	private void OnStatusActionsMenuOpening(object? sender, object e)
	{
		var menu = (MenuFlyout)sender!;
		menu.Items.Clear();
		if (ViewModel.SelectedSidebarItem is { } item)
		{
			FillSidebarMenu(menu, item);
		}
	}

	private void OnStatusThemeMenuOpening(object? sender, object e)
	{
		var menu = (MenuFlyout)sender!;
		FillThemeMenu(menu.Items);
	}

	private void ShowThemeName() => StatusThemeText.Text = ViewModel.CurrentTheme.Name;

	private void OnStatusClicked(object sender, RoutedEventArgs e) => ViewModel.CycleStatus();

	private void OnSidebarPointerPressed(object sender, PointerRoutedEventArgs e)
	{
		if (!e.GetCurrentPoint(Sidebar).Properties.IsLeftButtonPressed || DataContextOf(e.OriginalSource as FrameworkElement) is not SidebarItem clicked)
		{
			return;
		}
		var modifiers = CurrentModifiers();
		ViewModel.SidebarClick(clicked, modifiers.HasFlag(VirtualKeyModifiers.Control), modifiers.HasFlag(VirtualKeyModifiers.Shift));
	}

	/// <summary>NetNewsWire's menu for several selected rows: Mark All as Read, and Delete when they can all be deleted.</summary>
	private void FillMultiSidebarMenu(MenuFlyout menu)
	{
		var rows = ViewModel.MultiSelection;
		menu.Items.Add(MenuItem($"Mark All as Read in {rows.Count} Items", ViewModel.MarkAllReadInMultiSelection));
		if (rows.Any(row => row.Feed is not null || row.Folder is not null))
		{
			menu.Items.Add(new MenuFlyoutSeparator());
			menu.Items.Add(MenuItem("Delete", async () => await ConfirmDeleteMultipleAsync()));
		}
	}

	private async Task ConfirmDeleteMultipleAsync()
	{
		var count = ViewModel.MultiSelection.Count(row => row.Feed is not null || row.Folder is not null);
		var dialog = Dialog("Delete", "Delete", new TextBlock { Text = $"Are you sure you want to delete these {count} items?", TextWrapping = TextWrapping.Wrap });
		if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
		{
			await RunReportingErrorsAsync("Couldn’t Delete", ViewModel.DeleteMultiSelectionAsync);
		}
	}

	private void FillSidebarMenu(MenuFlyout menu, SidebarItem item)
	{
		if (ViewModel.MultiSelection.Count > 1 && ViewModel.MultiSelection.Contains(item))
		{
			FillMultiSidebarMenu(menu);
			return;
		}
		if (item.Kind != SidebarItemKind.SmartFeedsGroup)
		{
			menu.Items.Add(MenuItem("Mark All as Read", () => ViewModel.MarkAllRead(item)));
		}
		if (item.Feed is { } feed)
		{
			menu.Items.Add(new MenuFlyoutSeparator());
			menu.Items.Add(MenuItem("Open Home Page", () => MainViewModel.OpenInBrowser(feed.HomePageUrl), feed.HomePageUrl is not null));
			menu.Items.Add(new MenuFlyoutSeparator());
			menu.Items.Add(MenuItem("Copy Feed URL", () => CopyToClipboard(feed.Url)));
			menu.Items.Add(MenuItem("Copy Home Page URL", () => CopyToClipboard(feed.HomePageUrl), feed.HomePageUrl is not null));
			menu.Items.Add(new MenuFlyoutSeparator());
			var notifications = new ToggleMenuFlyoutItem { Text = "Show Notifications for New Articles", IsChecked = ViewModel.IsNotifyingNewArticles(item) };
			notifications.Click += (_, _) => ViewModel.SetNotifiesNewArticles(item, notifications.IsChecked);
			menu.Items.Add(notifications);
			var readerView = new ToggleMenuFlyoutItem { Text = "Always Use Reader View", IsChecked = ViewModel.IsReaderViewAlwaysOn(item) };
			readerView.Click += (_, _) => ViewModel.SetReaderViewAlwaysOn(item, readerView.IsChecked);
			menu.Items.Add(readerView);
			menu.Items.Add(new MenuFlyoutSeparator());
			// Also reachable by keyboard and screen reader, unlike dragging.
			var moveTo = new MenuFlyoutSubItem { Text = "Move to" };
			foreach (var (name, folder) in MainViewModel.MoveDestinations(item))
			{
				moveTo.Items.Add(MenuItem(name, async () => await RunReportingErrorsAsync("Couldn’t Move Feed", () => ViewModel.MoveFeedAsync(item, item.Account!, folder, copy: false))));
			}
			// Other accounts get a submenu each: moving there subscribes there.
			foreach (var (account, destinations) in ViewModel.OtherAccountDestinations(item))
			{
				if (moveTo.Items.Count > 0)
				{
					moveTo.Items.Add(new MenuFlyoutSeparator());
				}
				var accountMenu = new MenuFlyoutSubItem { Text = account.Name };
				foreach (var (name, folder) in destinations)
				{
					accountMenu.Items.Add(MenuItem(name, async () => await RunReportingErrorsAsync("Couldn’t Move Feed", () => ViewModel.MoveFeedAsync(item, account, folder, copy: false))));
				}
				moveTo.Items.Add(accountMenu);
			}
			moveTo.IsEnabled = moveTo.Items.Count > 0;
			menu.Items.Add(moveTo);
			menu.Items.Add(MenuItem("Rename…", () => BeginRenameAfterMenu(item)));
			menu.Items.Add(MenuItem("Delete", async () => await ConfirmDeleteAsync(item)));
			menu.Items.Add(new MenuFlyoutSeparator());
			menu.Items.Add(MenuItem("Get Info", async () => await ShowInspectorAsync(item)));
		}
		else if (item.Folder is not null)
		{
			menu.Items.Add(new MenuFlyoutSeparator());
			menu.Items.Add(MenuItem("Rename…", () => BeginRenameAfterMenu(item)));
			menu.Items.Add(MenuItem("Delete", async () => await ConfirmDeleteAsync(item)));
			menu.Items.Add(new MenuFlyoutSeparator());
			menu.Items.Add(MenuItem("Get Info", async () => await ShowInspectorAsync(item)));
		}
		else if (item is { Kind: SidebarItemKind.AccountGroup, Account: { } account })
		{
			menu.Items.Add(new MenuFlyoutSeparator());
			menu.Items.Add(MenuItem("New Feed…", async () => await ShowNewFeedDialogAsync()));
			if (account.Kind == AccountKind.Local)
			{
				menu.Items.Add(MenuItem("New Folder…", async () => await ShowNewFolderDialogAsync()));
			}
			else
			{
				menu.Items.Add(new MenuFlyoutSeparator());
				menu.Items.Add(MenuItem("Remove Account…", async () => await ConfirmRemoveAccountAsync(account)));
			}
		}
	}

	/// <summary>NetNewsWire's timeline menu (parity row TL-08), minus items whose features aren't built yet.</summary>
	/// <summary>
	/// NetNewsWire's timeline menu (<c>menu(for:)</c>): for the clicked article, or for all the selected ones when it's one
	/// of them. Each item shows only when it applies.
	/// </summary>
	private void OnArticleMenuOpening(object? sender, object e)
	{
		var menu = (MenuFlyout)sender!;
		menu.Items.Clear();
		if (DataContextOf(menu.Target) is not ArticleItem article)
		{
			return;
		}
		IReadOnlyList<ArticleItem> targets = ViewModel.SelectedArticles.Contains(article) ? ViewModel.SelectedArticles : [article];
		if (targets.Any(target => !target.IsRead))
		{
			menu.Items.Add(MenuItem("Mark as Read", () => ViewModel.MarkRead(targets)));
		}
		if (targets.Any(target => target.IsRead))
		{
			menu.Items.Add(MenuItem("Mark as Unread", () => ViewModel.ToggleRead([.. targets.Where(target => target.IsRead)])));
		}
		if (targets.Any(target => !target.IsStarred))
		{
			menu.Items.Add(MenuItem("Mark as Starred", () => ViewModel.ToggleStarred([.. targets.Where(target => !target.IsStarred)])));
		}
		if (targets.Any(target => target.IsStarred))
		{
			menu.Items.Add(MenuItem("Mark as Unstarred", () => ViewModel.ToggleStarred([.. targets.Where(target => target.IsStarred)])));
		}
		if (ViewModel.UnreadAbove(targets) is { Count: > 0 } above)
		{
			menu.Items.Add(MenuItem("Mark Above as Read", () => ViewModel.MarkRead(above)));
		}
		if (ViewModel.UnreadBelow(targets) is { Count: > 0 } below)
		{
			menu.Items.Add(MenuItem("Mark Below as Read", () => ViewModel.MarkRead(below)));
		}
		if (targets.Count == 1)
		{
			var feedName = MainViewModel.FeedFor(article) is { } feed ? NameSorting.DisplayName(feed) : article.FeedName;
			var feedItems = new List<MenuFlyoutItemBase>();
			if (!ViewModel.IsShowingOnlyFeedOf(article) && ViewModel.FeedRowFor(article) is not null)
			{
				feedItems.Add(MenuItem($"Select “{feedName}” in Sidebar", () => ViewModel.SelectFeedInSidebar(article)));
			}
			if (ViewModel.UnreadCountInFeedOf(article) > 0)
			{
				feedItems.Add(MenuItem($"Mark All as Read in “{feedName}”", () => ViewModel.MarkAllReadInFeedOf(article)));
			}
			AddGroup(menu, feedItems);
			if (article.LinkUrl is { } link)
			{
				// The toolbar's article actions, for the article being read.
				var linkItems = new List<MenuFlyoutItemBase> { MenuItem("Open in Browser", () => MainViewModel.OpenInBrowser(link)) };
				if (article == ViewModel.SelectedArticle)
				{
					linkItems.Add(MenuItem(ViewModel.IsShowingReaderView ? "Hide Reader View" : "Show Reader View", ViewModel.ToggleReaderView));
				}
				AddGroup(menu, linkItems);
				var copyItems = new List<MenuFlyoutItemBase> { MenuItem("Copy Article URL", () => CopyToClipboard(link)) };
				if (article.Article.ExternalUrl is { } external && external != link)
				{
					copyItems.Add(MenuItem("Copy External URL", () => CopyToClipboard(external)));
				}
				AddGroup(menu, copyItems);
			}
		}
		else
		{
			AddGroup(menu, [MenuItem($"Open {targets.Count} Articles in Browser", () =>
			{
				foreach (var target in targets)
				{
					MainViewModel.OpenInBrowser(target.LinkUrl);
				}
			})]);
		}
		AddGroup(menu, [MenuItem("Share…", () => Share(targets), targets.Any(target => target.LinkUrl is not null))]);

		static void AddGroup(MenuFlyout menu, IReadOnlyList<MenuFlyoutItemBase> items)
		{
			if (items.Count == 0)
			{
				return;
			}
			if (menu.Items.Count > 0)
			{
				menu.Items.Add(new MenuFlyoutSeparator());
			}
			foreach (var item in items)
			{
				menu.Items.Add(item);
			}
		}
	}

	private static object? DataContextOf(FrameworkElement? element) =>
		element switch
		{
			null => null,
			{ DataContext: SidebarItem or ArticleItem } => element.DataContext,
			ContentControl { Content: SidebarItem or ArticleItem } control => control.Content,
			_ => (element as ContentControl)?.Content is FrameworkElement content ? content.DataContext : null,
		};

	private static MenuFlyoutItem MenuItem(string text, Action action, bool isEnabled = true)
	{
		var item = new MenuFlyoutItem { Text = text, IsEnabled = isEnabled };
		item.Click += (_, _) => action();
		return item;
	}

	private static void CopyToClipboard(string? text)
	{
		if (text is null)
		{
			return;
		}
		var package = new DataPackage();
		package.SetText(text);
		Clipboard.SetContent(package);
	}

	// Dialogs

	/// <summary>NetNewsWire's Add Feed sheet: URL, prefilled from the clipboard, then the account and folder.</summary>
	/// <summary>
	/// NetNewsWire's Add Feed sheet. A link dragged onto the sidebar fills in the address, and the row it was dropped on
	/// picks the account and folder; otherwise the address on the clipboard is offered.
	/// </summary>
	private async Task ShowNewFeedDialogAsync(string? droppedUrl = null, SidebarItem? target = null)
	{
		var url = new TextBox { Header = "URL", PlaceholderText = "Feed or website address" };
		if ((droppedUrl ?? await ClipboardUrlAsync()) is { } prefill)
		{
			url.Text = prefill;
			url.SelectAll();
		}
		var accounts = ViewModel.Accounts.ToList();
		var accountBox = new ComboBox
		{
			Header = "Account",
			ItemsSource = accounts.Select(account => account.Name).ToList(),
			SelectedIndex = Math.Max(0, accounts.IndexOf(target?.Account ?? ViewModel.DefaultAccountForNewFeed)),
			HorizontalAlignment = HorizontalAlignment.Stretch,
			Visibility = accounts.Count > 1 ? Visibility.Visible : Visibility.Collapsed,
		};
		// The folder list follows the chosen account: its top level first, then its folders.
		var folders = new List<(string Name, StoredFolder? Folder)>();
		var folderBox = new ComboBox { Header = "Folder", HorizontalAlignment = HorizontalAlignment.Stretch };
		void ShowFolders(StoredFolder? select)
		{
			var account = accounts[Math.Max(0, accountBox.SelectedIndex)];
			folders = [(account.Name, null), .. MainViewModel.FoldersOf(account).Select(folder => (folder.Name, (StoredFolder?)folder))];
			folderBox.ItemsSource = folders.Select(folder => folder.Name).ToList();
			folderBox.SelectedIndex = Math.Max(0, folders.FindIndex(folder => folder.Folder?.FolderId == select?.FolderId));
		}
		ShowFolders(target is null ? ViewModel.DefaultFolderForNewFeed : target.Folder ?? target.ParentFolder);
		accountBox.SelectionChanged += (_, _) => ShowFolders(null);
		var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
		var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Style = (Style)Application.Current.Resources["ErrorTextStyle"] };
		// NetNewsWire's Add Feed from List: when the address offers several feeds, the reader picks one.
		var choiceHeading = new TextBlock { Text = "This address has more than one feed. Choose one:", TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
		var choiceList = new ListView { SelectionMode = ListViewSelectionMode.Single, Visibility = Visibility.Collapsed, MaxHeight = 240 };
		AutomationProperties.SetName(choiceList, "Feeds found");
		List<FeedSpecifier>? choices = null;
		var dialog = Dialog("New Feed", "Add", new StackPanel { Spacing = 12, Width = 420, Children = { url, accountBox, folderBox, choiceHeading, choiceList, progress, error } });
		dialog.IsPrimaryButtonEnabled = url.Text.Length > 0;
		url.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = url.Text.Trim().Length > 0;
		dialog.PrimaryButtonClick += async (_, args) =>
		{
			var deferral = args.GetDeferral();
			progress.Visibility = Visibility.Visible;
			error.Visibility = Visibility.Collapsed;
			dialog.IsPrimaryButtonEnabled = false;
			try
			{
				var account = accounts[Math.Max(0, accountBox.SelectedIndex)];
				var address = url.Text.Trim();
				if (choices is null)
				{
					var found = await account.FindFeedsAsync(address);
					if (found.Count > 1)
					{
						choices = [.. found];
						choiceList.ItemsSource = choices.Select(choice => string.IsNullOrWhiteSpace(choice.Title) ? choice.Url : $"{choice.Title}  ·  {choice.Url}").ToList();
						choiceList.SelectedIndex = 0;
						url.Visibility = accountBox.Visibility = folderBox.Visibility = Visibility.Collapsed;
						choiceHeading.Visibility = choiceList.Visibility = Visibility.Visible;
						args.Cancel = true;
						return;
					}
				}
				else
				{
					address = choices[Math.Max(0, choiceList.SelectedIndex)].Url;
				}
				await ViewModel.SubscribeAsync(address, account, folders[Math.Max(0, folderBox.SelectedIndex)].Folder);
			}
			catch (Exception ex)
			{
				error.Text = ex.Message;
				error.Visibility = Visibility.Visible;
				args.Cancel = true;
			}
			finally
			{
				progress.Visibility = Visibility.Collapsed;
				dialog.IsPrimaryButtonEnabled = true;
				deferral.Complete();
			}
		};
		await ShowDialogAsync(dialog);
	}

	private async Task ShowNewFolderDialogAsync()
	{
		if (await PromptAsync("New Folder", "Add", "Name", "") is { } name)
		{
			ViewModel.AddFolder(name);
		}
	}

	/// <summary>NetNewsWire's rename in place (F2, or Rename… in the menu): the row's name becomes a box, Enter keeps it and Esc drops it.</summary>
	private void BeginRename(SidebarItem? item)
	{
		if (item is { IsRenaming: false } && (item.Feed is not null || item.Folder is not null))
		{
			item.EditText = item.Title;
			_renameStarted = Environment.TickCount64;
			item.IsRenaming = true;
		}
	}

	/// <summary>From a menu: waits for the menu to close and hand the focus back before the box asks for it.</summary>
	private void BeginRenameAfterMenu(SidebarItem item)
	{
		var timer = DispatcherQueue.CreateTimer();
		timer.Interval = TimeSpan.FromMilliseconds(400);
		timer.IsRepeating = false;
		timer.Tick += (_, _) => BeginRename(item);
		timer.Start();
	}

	/// <summary>
	/// When a rename began. A menu hands the focus back to where it came from as it closes, which would end a rename that
	/// opened from the menu at once, so losing the focus just after it began only makes the box take it again.
	/// </summary>
	private long _renameStarted;

	private void OnRenameBoxLoaded(object sender, RoutedEventArgs e)
	{
		var box = (TextBox)sender;
		// The box is always in the row, hidden; when it's shown for a rename it takes the focus with its text selected.
		box.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) =>
		{
			if (box.Visibility == Visibility.Visible)
			{
				DispatcherQueue.TryEnqueue(() =>
				{
					box.Focus(FocusState.Programmatic);
					box.SelectAll();
				});
			}
		});
	}

	private void OnRenameKeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (DataContextOf((FrameworkElement)sender) is not SidebarItem item)
		{
			return;
		}
		if (e.Key == VirtualKey.Enter)
		{
			e.Handled = true;
			_ = CommitRenameAsync(item, ((TextBox)sender).Text);
		}
		else if (e.Key == VirtualKey.Escape)
		{
			e.Handled = true;
			item.IsRenaming = false;
			FocusPane(Pane.Sidebar);
		}
	}

	/// <summary>Clicking away keeps what was typed, as in NetNewsWire.</summary>
	private void OnRenameLostFocus(object sender, RoutedEventArgs e)
	{
		if (DataContextOf((FrameworkElement)sender) is not SidebarItem { IsRenaming: true } item)
		{
			return;
		}
		if (Environment.TickCount64 - _renameStarted < 600)
		{
			var box = (TextBox)sender;
			DispatcherQueue.TryEnqueue(() => box.Focus(FocusState.Programmatic));
			return;
		}
		_ = CommitRenameAsync(item, ((TextBox)sender).Text);
	}

	/// <summary>Takes the name from the box itself: its binding to the row only updates as the box loses the focus, which may be after this.</summary>
	private async Task CommitRenameAsync(SidebarItem item, string name)
	{
		if (!item.IsRenaming)
		{
			return;
		}
		item.IsRenaming = false;
		if (item.Feed is not null)
		{
			await RunReportingErrorsAsync("Couldn’t Rename Feed", () => ViewModel.RenameFeedAsync(item, name));
		}
		else if (item.Folder is not null)
		{
			await RunReportingErrorsAsync("Couldn’t Rename Folder", () => ViewModel.RenameFolderAsync(item, name));
		}
		FocusPane(Pane.Sidebar);
	}

	/// <summary>
	/// NetNewsWire's Inspector (Window › Info, Cmd+I), as a dialog for the selected sidebar row: a feed's name, home
	/// page, feed URL and Reader View setting; a folder's name; a smart feed's name. Changes apply on Done.
	/// </summary>
	private async Task ShowInspectorAsync(SidebarItem? item)
	{
		item ??= ViewModel.SelectedSidebarItem;
		var panel = new StackPanel { Spacing = 12, Width = 420 };
		TextBox? nameBox = null;
		CheckBox? readerView = null;
		CheckBox? notifications = null;
		string title;
		if (item is null or { Kind: SidebarItemKind.SmartFeedsGroup or SidebarItemKind.AccountGroup })
		{
			title = "Inspector";
			panel.Children.Add(new TextBlock { Text = "Nothing to Inspect", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center });
		}
		else
		{
			var header = new Grid { ColumnSpacing = 12 };
			header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			UIElement icon = item.Icon is { } image
				? new Image { Source = image, Width = 32, Height = 32 }
				: new FontIcon { Glyph = item.Glyph, FontSize = 28 };
			header.Children.Add(icon);
			if (item.Kind is SidebarItemKind.Feed or SidebarItemKind.Folder)
			{
				nameBox = new TextBox { Text = item.Feed is { } feed ? NameSorting.DisplayName(feed) : item.Title, VerticalAlignment = VerticalAlignment.Center };
				AutomationProperties.SetName(nameBox, "Name");
				Grid.SetColumn(nameBox, 1);
				header.Children.Add(nameBox);
			}
			else
			{
				var name = new TextBlock { Text = item.Title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center };
				Grid.SetColumn(name, 1);
				header.Children.Add(name);
			}
			panel.Children.Add(header);
			if (item.Feed is { } inspected)
			{
				title = "Feed Inspector";
				panel.Children.Add(Field("Home Page", inspected.HomePageUrl));
				panel.Children.Add(Field("Feed", inspected.Url));
				notifications = new CheckBox { Content = "Show Notifications for New Articles", IsChecked = ViewModel.IsNotifyingNewArticles(item) };
				panel.Children.Add(notifications);
				readerView = new CheckBox { Content = "Always Use Reader View", IsChecked = ViewModel.IsReaderViewAlwaysOn(item) };
				panel.Children.Add(readerView);
			}
			else
			{
				title = item.Folder is not null ? "Folder Inspector" : "Smart Feed Inspector";
			}
		}
		var dialog = new ContentDialog
		{
			XamlRoot = Content.XamlRoot,
			Title = title,
			Content = panel,
			CloseButtonText = nameBox is null && readerView is null ? "Close" : "Cancel",
			DefaultButton = ContentDialogButton.Primary,
		};
		if (nameBox is not null || readerView is not null)
		{
			dialog.PrimaryButtonText = "Done";
		}
		if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || item is null)
		{
			return;
		}
		if (readerView is not null)
		{
			ViewModel.SetReaderViewAlwaysOn(item, readerView.IsChecked == true);
		}
		if (notifications is not null)
		{
			ViewModel.SetNotifiesNewArticles(item, notifications.IsChecked == true);
		}
		if (nameBox is { Text: var newName } && newName.Trim().Length > 0 && newName.Trim() != item.Title)
		{
			if (item.Feed is not null)
			{
				await RunReportingErrorsAsync("Couldn’t Rename Feed", () => ViewModel.RenameFeedAsync(item, newName));
			}
			else
			{
				await RunReportingErrorsAsync("Couldn’t Rename Folder", () => ViewModel.RenameFolderAsync(item, newName.Trim()));
			}
		}

		// A URL that can be selected and copied but not edited, as in NetNewsWire's inspector.
		static UIElement Field(string header, string? url) =>
			new TextBox { Header = header, Text = url ?? "", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, PlaceholderText = "None" };
	}


	private async Task ConfirmDeleteAsync(SidebarItem item)
	{
		var kind = item.Folder is not null ? "folder" : "feed";
		var text = $"Are you sure you want to delete the “{item.Title}” {kind}?";
		if (item.Account is { Kind: not AccountKind.Local } account)
		{
			text += $" It will be removed from {account.Name} too.";
		}
		var dialog = Dialog("Delete", "Delete", new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
		if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
		{
			await RunReportingErrorsAsync("Couldn’t Delete", () => ViewModel.DeleteAsync(item));
		}
	}

	private Task ShowAddAccountDialogAsync() => ShowSharedDialogAsync(() => AccountDialogs.ShowAddAsync(Content.XamlRoot, ViewModel));

	private Task ConfirmRemoveAccountAsync(IAccount account) => ShowSharedDialogAsync(() => AccountDialogs.ConfirmRemoveAsync(Content.XamlRoot, ViewModel, account));

	/// <summary>Only one dialog can be open at a time; one of the shared account dialogs, like the others, waits its turn.</summary>
	private async Task ShowSharedDialogAsync(Func<Task> show)
	{
		if (_dialogOpen)
		{
			return;
		}
		_dialogOpen = true;
		try
		{
			await show();
		}
		finally
		{
			_dialogOpen = false;
		}
	}

	/// <summary>Runs a change that may need the network, showing what went wrong instead of failing silently.</summary>
	private async Task RunReportingErrorsAsync(string title, Func<Task> action)
	{
		try
		{
			await action();
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			await ShowMessageAsync(title, ex.Message);
		}
	}

	/// <summary>Asks for a line of text. Returns null if cancelled or left blank.</summary>
	private async Task<string?> PromptAsync(string title, string primaryButtonText, string header, string text)
	{
		var box = new TextBox { Header = header, Text = text, Width = 360 };
		box.SelectAll();
		var dialog = Dialog(title, primaryButtonText, box);
		dialog.IsPrimaryButtonEnabled = text.Trim().Length > 0;
		box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
		return await ShowDialogAsync(dialog) == ContentDialogResult.Primary ? box.Text.Trim() : null;
	}

	private ContentDialog Dialog(string title, string primaryButtonText, object content) => new()
	{
		XamlRoot = Content.XamlRoot,
		Title = title,
		Content = content,
		PrimaryButtonText = primaryButtonText,
		CloseButtonText = "Cancel",
		DefaultButton = ContentDialogButton.Primary,
	};

	/// <summary>Only one dialog can be open at a time; a shortcut pressed while one is open does nothing.</summary>
	private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
	{
		if (_dialogOpen)
		{
			return ContentDialogResult.None;
		}
		_dialogOpen = true;
		try
		{
			// A dialog doesn't take the window's Light or Dark on its own; it follows Windows'.
			dialog.RequestedTheme = Root.ActualTheme;
			return await dialog.ShowAsync();
		}
		finally
		{
			_dialogOpen = false;
		}
	}

	private Task ShowMessageAsync(string title, string message) =>
		ShowDialogAsync(new ContentDialog { XamlRoot = Content.XamlRoot, Title = title, Content = message, CloseButtonText = "OK" });

	private static async Task<string?> ClipboardUrlAsync()
	{
		try
		{
			var content = Clipboard.GetContent();
			if (content.Contains(StandardDataFormats.Text) && (await content.GetTextAsync()).Trim() is var text && IsWebUrl(text) && Uri.IsWellFormedUriString(text, UriKind.Absolute))
			{
				return text;
			}
		}
		catch (Exception)
		{
			// The clipboard can be locked by another app; prefilling is only a convenience.
		}
		return null;
	}

	// Import and export

	/// <summary>With more than one account, asks which one an import or export is for; with one, that one. Null if cancelled.</summary>
	private async Task<IAccount?> ChooseAccountAsync(string title, string button)
	{
		var accounts = ViewModel.Accounts.ToList();
		if (accounts.Count == 1)
		{
			return accounts[0];
		}
		var box = new ComboBox
		{
			Header = "Account",
			ItemsSource = accounts.Select(account => account.Name).ToList(),
			SelectedIndex = Math.Max(0, accounts.IndexOf(ViewModel.DefaultAccountForNewFeed)),
			HorizontalAlignment = HorizontalAlignment.Stretch,
			Width = 360,
		};
		AutomationProperties.SetName(box, "Account");
		return await ShowDialogAsync(Dialog(title, button, box)) == ContentDialogResult.Primary ? accounts[Math.Max(0, box.SelectedIndex)] : null;
	}

	private async Task ImportAsync()
	{
		if (await ChooseAccountAsync("Import Subscriptions", "Choose File…") is not { } account)
		{
			return;
		}
		var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
		picker.FileTypeFilter.Add(".opml");
		picker.FileTypeFilter.Add(".xml");
		WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
		if (await picker.PickSingleFileAsync() is not { } file)
		{
			return;
		}
		try
		{
			var summary = await ViewModel.ImportOpmlAsync(file.Path, account);
			if (account.Kind != AccountKind.Local)
			{
				await ShowMessageAsync("Import Subscriptions", summary);
			}
		}
		catch (Exception ex) when (ex is OpmlException or IOException or UnauthorizedAccessException)
		{
			await ShowMessageAsync("Couldn’t Import Subscriptions", ex.Message);
		}
	}

	private async Task ExportAsync()
	{
		if (await ChooseAccountAsync("Export Subscriptions", "Choose File…") is not { } account)
		{
			return;
		}
		var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = $"Subscriptions-{account.Name}" };
		picker.FileTypeChoices.Add("OPML", [".opml"]);
		WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
		if (await picker.PickSaveFileAsync() is not { } file)
		{
			return;
		}
		try
		{
			ViewModel.ExportOpml(file.Path, account);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			await ShowMessageAsync("Couldn’t Export Subscriptions", ex.Message);
		}
	}

	// Keyboard

	private enum Pane
	{
		Other,
		Sidebar,
		Timeline,
		Article,
	}

	/// <summary>A key and its modifiers, and the pane focus must be in for it to apply; null for anywhere.</summary>
	private sealed record Shortcut(VirtualKey Key, VirtualKeyModifiers Modifiers, Pane? Pane, Action Action);

	private readonly List<Shortcut> _shortcuts = [];

	/// <summary>
	/// The page in the article pane forwards keys the app may want, since WebView2 keeps keyboard input to itself.
	/// It leaves the page the keys it needs: typing in fields, editing shortcuts such as copy, scrolling, following
	/// a focused link with Enter, and Space until the article's end.
	/// </summary>
	private const string ArticleKeysScript = """
		(() => {
		// Captured before any other script runs, so one can't replace postMessage to see the token.
		const webview = window.chrome && chrome.webview;
		if (!webview) return;
		const post = webview.postMessage.bind(webview);
		addEventListener('keydown', e => {
			// Only real key presses: a script can dispatch a keydown, but not a trusted one.
			if (!e.isTrusted) return;
			const t = e.target;
			if (t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName))) return;
			const key = e.key;
			if (e.ctrlKey && !e.altKey && !e.shiftKey && ['a', 'c', 'v', 'x', 'y', 'z', 'Insert'].includes(key.length === 1 ? key.toLowerCase() : key)) return;
			if (['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End', 'Tab'].includes(key) || (key === 'ArrowRight' && !e.ctrlKey)) return;
			if (key === ' ' && !e.ctrlKey && !e.altKey && (e.shiftKey || innerHeight + scrollY < document.documentElement.scrollHeight - 4)) return;
			if (key === 'Enter' && t && t.closest && t.closest('a, button')) return;
			if (!e.ctrlKey && !e.altKey && key.length !== 1 && !['Enter', 'ArrowLeft', 'F1', 'F3', 'F5', 'F11'].includes(key)) return;
			e.preventDefault();
			post({ token: '[[token]]', keyCode: e.keyCode, ctrl: e.ctrlKey, shift: e.shiftKey, alt: e.altKey });
		}, true);
		})();
		""";

	/// <summary>
	/// NetNewsWire's keyboard map from the parity matrix, for the commands built so far: menu shortcuts with Ctrl for
	/// Cmd, the single-key shortcuts, and the keys that move focus between panes.
	/// </summary>
	private void AddShortcuts()
	{
		const VirtualKeyModifiers None = VirtualKeyModifiers.None;
		const VirtualKeyModifiers Shift = VirtualKeyModifiers.Shift;
		const VirtualKeyModifiers Ctrl = VirtualKeyModifiers.Control;
		const VirtualKeyModifiers CtrlShift = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift;
		const VirtualKeyModifiers CtrlAlt = VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu;
		const VirtualKey Semicolon = (VirtualKey)186, Plus = (VirtualKey)187, Comma = (VirtualKey)188, Minus = (VirtualKey)189,
			Period = (VirtualKey)190, Slash = (VirtualKey)191, Quote = (VirtualKey)222;

		// Keys that only apply in one pane come first, so they win over the global ones.
		Add(VirtualKey.Right, None, Pane.Sidebar, () => FocusPane(Pane.Timeline));
		Add(VirtualKey.Tab, None, Pane.Sidebar, () => FocusPane(Pane.Timeline));
		Add(Comma, None, Pane.Sidebar, () => ViewModel.SetSelectedExpanded(false));
		Add(VirtualKey.Left, Ctrl, Pane.Sidebar, () => ViewModel.SetSelectedExpanded(false));
		Add(Period, None, Pane.Sidebar, () => ViewModel.SetSelectedExpanded(true));
		Add(VirtualKey.Right, Ctrl, Pane.Sidebar, () => ViewModel.SetSelectedExpanded(true));
		Add(Semicolon, None, Pane.Sidebar, () => ViewModel.SetAllExpanded(false));
		Add(VirtualKey.Left, CtrlAlt, Pane.Sidebar, () => ViewModel.SetAllExpanded(false));
		Add(Quote, None, Pane.Sidebar, () => ViewModel.SetAllExpanded(true));
		Add(VirtualKey.Right, CtrlAlt, Pane.Sidebar, () => ViewModel.SetAllExpanded(true));
		Add(VirtualKey.F2, None, Pane.Sidebar, () => BeginRename(ViewModel.SelectedSidebarItem));
		Add(VirtualKey.Delete, None, Pane.Sidebar, () => _ = DeleteSelectedAsync());
		Add(VirtualKey.Back, None, Pane.Sidebar, () => _ = DeleteSelectedAsync());
		Add(VirtualKey.Left, None, Pane.Timeline, () => FocusPane(Pane.Sidebar));
		Add(VirtualKey.Right, None, Pane.Timeline, () => FocusPane(Pane.Article));
		Add(VirtualKey.Left, None, Pane.Article, () => FocusPane(Pane.Timeline));

		// Menu shortcuts.
		Add(VirtualKey.R, Ctrl, null, () => _ = ViewModel.RefreshAsync());
		Add(VirtualKey.F5, None, null, () => _ = ViewModel.RefreshAsync());
		Add(VirtualKey.N, Ctrl, null, () => _ = ShowNewFeedDialogAsync());
		Add(VirtualKey.N, CtrlShift, null, () => _ = ShowNewFolderDialogAsync());
		Add(VirtualKey.E, CtrlAlt, null, () => _ = ExportAsync());
		Add(VirtualKey.K, Ctrl, null, () => ViewModel.MarkAllRead());
		Add(VirtualKey.U, CtrlShift, null, () => ViewModel.ToggleRead());
		Add(VirtualKey.L, CtrlShift, null, () => ViewModel.ToggleStarred());
		Add(VirtualKey.R, CtrlShift, null, () => ViewModel.ToggleReaderView());
		Add(VirtualKey.I, Ctrl, null, () => _ = ShowInspectorAsync(null));
		Add(VirtualKey.F, CtrlAlt, null, () => SearchBox.Focus(FocusState.Keyboard));
		Add(VirtualKey.S, CtrlAlt, null, ToggleSidebar);
		Add(VirtualKey.F1, None, null, () => MainViewModel.OpenInBrowser(HelpUrl));
		Add(Comma, Ctrl, null, OpenPreferences);
		Add(VirtualKey.K, CtrlAlt, null, ViewModel.MarkAboveRead);
		Add(VirtualKey.K, CtrlShift, null, ViewModel.MarkBelowRead);
		Add(VirtualKey.C, CtrlShift, null, () => CopyToClipboard(ViewModel.SelectedArticle?.LinkUrl));
		Add(VirtualKey.C, CtrlAlt, null, CopyExternalUrl);
		Add(VirtualKey.W, Ctrl, null, Close);
		Add(VirtualKey.F11, None, null, ToggleFullScreen);
		Add(VirtualKey.T, CtrlAlt, null, ToggleToolbar);
		Add(VirtualKey.N, CtrlAlt, null, () => NewWindowRequested?.Invoke(this, EventArgs.Empty));
		Add(VirtualKey.Number6, Ctrl, null, () => OpenUtilityWindow("Dinosaurs"));
		Add(VirtualKey.Number7, Ctrl, null, () => OpenUtilityWindow("CurrentActivity"));
		Add(VirtualKey.Number8, Ctrl, null, () => OpenUtilityWindow("ActivityLog"));
		Add(VirtualKey.Number9, Ctrl, null, () => OpenUtilityWindow("ErrorLog"));
		Add(VirtualKey.Z, Ctrl, null, () => _ = UndoAsync());
		Add(VirtualKey.Y, Ctrl, null, () => _ = RedoAsync());
		Add(VirtualKey.Z, CtrlShift, null, () => _ = RedoAsync());
		Add(VirtualKey.F, Ctrl, null, ShowFindBar);
		Add(VirtualKey.F3, None, null, FindNext);
		Add(VirtualKey.F3, Shift, null, FindPrevious);
		Add(VirtualKey.Number0, Ctrl, null, Activate);
		// Ctrl+Right opens the article only with the article focused, since elsewhere it expands rows and moves by words.
		Add(VirtualKey.Right, Ctrl, Pane.Article, () => MainViewModel.OpenInBrowser(ViewModel.SelectedArticle?.LinkUrl));
		Add(VirtualKey.Right, CtrlShift, Pane.Article, () => MainViewModel.OpenInBrowser(ViewModel.SelectedArticle?.LinkUrl));
		Add(Slash, Ctrl, null, ViewModel.SelectNextUnread);
		Add(VirtualKey.Number1, Ctrl, null, () => ViewModel.SelectSmartFeed(SidebarItemKind.Today));
		Add(VirtualKey.Number2, Ctrl, null, () => ViewModel.SelectSmartFeed(SidebarItemKind.AllUnread));
		Add(VirtualKey.Number3, Ctrl, null, () => ViewModel.SelectSmartFeed(SidebarItemKind.Starred));
		Add(VirtualKey.Q, Ctrl, null, () => ExitRequested?.Invoke(this, EventArgs.Empty));
		Add(VirtualKey.H, CtrlShift, null, ViewModel.ToggleHideReadArticles);
		Add(VirtualKey.F, CtrlShift, null, ViewModel.ToggleHideReadFeeds);
		Add(Quote, Ctrl, null, ViewModel.CleanUp);

		// Single-key shortcuts.
		Add(VirtualKey.Space, None, null, () => _ = SpaceAsync(up: false));
		Add(VirtualKey.Space, Shift, null, () => _ = SpaceAsync(up: true));
		Add(Minus, None, null, ViewModel.SelectPreviousUnread);
		Add(VirtualKey.Subtract, None, null, ViewModel.SelectPreviousUnread);
		Add(VirtualKey.N, None, null, ViewModel.SelectNextUnread);
		Add(Plus, Shift, null, ViewModel.SelectNextUnread);
		Add(VirtualKey.Add, None, null, ViewModel.SelectNextUnread);
		Add(VirtualKey.R, None, null, () => ViewModel.ToggleRead());
		Add(VirtualKey.U, None, null, () => ViewModel.ToggleRead());
		Add(VirtualKey.K, None, null, () => ViewModel.MarkAllRead());
		Add(VirtualKey.M, None, null, ViewModel.MarkUnreadAndSelectNextUnread);
		Add(VirtualKey.L, None, null, ViewModel.MarkAllReadAndSelectNextUnread);
		Add(VirtualKey.O, None, null, ViewModel.MarkOlderRead);
		Add(VirtualKey.S, None, null, () => ViewModel.ToggleStarred());
		// Shift opens in the background on the Mac; Windows can't open a browser behind the app, so both open normally.
		foreach (var modifiers in new[] { None, Shift })
		{
			Add(VirtualKey.B, modifiers, null, () => MainViewModel.OpenInBrowser(ViewModel.SelectedArticle?.LinkUrl));
			Add(VirtualKey.Enter, modifiers, null, () => MainViewModel.OpenInBrowser(ViewModel.SelectedArticle?.LinkUrl));
		}
		Add(VirtualKey.A, None, null, () => ViewModel.SelectAdjacentSidebarItem(-1));
		Add(VirtualKey.Z, None, null, () => ViewModel.SelectAdjacentSidebarItem(1));

		void Add(VirtualKey key, VirtualKeyModifiers modifiers, Pane? pane, Action action) => _shortcuts.Add(new Shortcut(key, modifiers, pane, action));
	}

	private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
	{
		var focused = FocusManager.GetFocusedElement(Content.XamlRoot) as DependencyObject;
		var pane = PaneOf(focused);
		// Keys typed into the article page arrive through OnArticleMessage; single keys never apply in text fields.
		if (pane == Pane.Article || focused is TextBox or PasswordBox or RichEditBox or AutoSuggestBox)
		{
			return;
		}
		// Space and Enter press a focused toolbar button.
		if (pane == Pane.Other && e.Key is VirtualKey.Space or VirtualKey.Enter)
		{
			return;
		}
		e.Handled = RunShortcut(e.Key, CurrentModifiers(), pane);
	}

	private void OnArticleMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
	{
		// Any script in the page can post, so anything that isn't a well-formed key message from the key script is dropped.
		JsonElement root;
		try
		{
			using var message = JsonDocument.Parse(args.WebMessageAsJson);
			root = message.RootElement.Clone();
		}
		catch (JsonException)
		{
			return;
		}
		if (root.ValueKind != JsonValueKind.Object
			|| !root.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String || token.GetString() != ArticleKeysToken
			|| !root.TryGetProperty("keyCode", out var keyCode) || !keyCode.TryGetInt32(out var code)
			|| !IsBoolean(root, "ctrl") || !IsBoolean(root, "shift") || !IsBoolean(root, "alt"))
		{
			return;
		}
		var modifiers = VirtualKeyModifiers.None;
		if (root.GetProperty("ctrl").GetBoolean())
		{
			modifiers |= VirtualKeyModifiers.Control;
		}
		if (root.GetProperty("shift").GetBoolean())
		{
			modifiers |= VirtualKeyModifiers.Shift;
		}
		if (root.GetProperty("alt").GetBoolean())
		{
			modifiers |= VirtualKeyModifiers.Menu;
		}
		// Chromium's keyCode is the Windows virtual-key code.
		RunShortcut((VirtualKey)code, modifiers, Pane.Article);

		static bool IsBoolean(JsonElement element, string name) =>
			element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False;
	}

	private bool RunShortcut(VirtualKey key, VirtualKeyModifiers modifiers, Pane pane)
	{
		if (_dialogOpen || _shortcuts.FirstOrDefault(s => s.Key == key && s.Modifiers == modifiers && (s.Pane is null || s.Pane == pane)) is not { } shortcut)
		{
			return false;
		}
		shortcut.Action();
		return true;
	}

	private static VirtualKeyModifiers CurrentModifiers()
	{
		var modifiers = VirtualKeyModifiers.None;
		foreach (var (key, modifier) in new[]
		{
			(VirtualKey.Control, VirtualKeyModifiers.Control),
			(VirtualKey.Shift, VirtualKeyModifiers.Shift),
			(VirtualKey.Menu, VirtualKeyModifiers.Menu),
		})
		{
			if (InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
			{
				modifiers |= modifier;
			}
		}
		return modifiers;
	}

	private Pane PaneOf(DependencyObject? element)
	{
		for (; element is not null; element = VisualTreeHelper.GetParent(element))
		{
			if (element == Sidebar)
			{
				return Pane.Sidebar;
			}
			if (element == Timeline)
			{
				return Pane.Timeline;
			}
			if (element == ArticleView)
			{
				return Pane.Article;
			}
		}
		return Pane.Other;
	}

	private void FocusPane(Pane pane)
	{
		switch (pane)
		{
			case Pane.Sidebar:
				Sidebar.Focus(FocusState.Keyboard);
				break;
			case Pane.Timeline:
				// Focus the selected row, or the first one, as the timeline does when tabbed into.
				if (ViewModel.SelectedArticle is null && ViewModel.Articles.Count > 0)
				{
					ViewModel.SelectedArticle = ViewModel.Articles[0];
				}
				Timeline.Focus(FocusState.Keyboard);
				break;
			case Pane.Article when ViewModel.SelectedArticle is not null:
				ArticleView.Focus(FocusState.Keyboard);
				break;
		}
	}

	/// <summary>Space pages down through the article and, at its end, goes to the next unread article. Shift+Space pages up.</summary>
	private async Task SpaceAsync(bool up)
	{
		if (!_articleViewReady || ViewModel.SelectedArticle is null)
		{
			if (!up)
			{
				ViewModel.SelectNextUnread();
			}
			return;
		}
		var result = await ArticleView.CoreWebView2.ExecuteScriptAsync(up
			? "scrollBy(0, -(innerHeight - 40)); 'scrolled'"
			: "innerHeight + scrollY >= document.documentElement.scrollHeight - 4 ? 'end' : (scrollBy(0, innerHeight - 40), 'scrolled')");
		if (result == "\"end\"")
		{
			ViewModel.SelectNextUnread();
		}
	}

	private async Task DeleteSelectedAsync()
	{
		if (ViewModel.MultiSelection.Count > 1)
		{
			await ConfirmDeleteMultipleAsync();
			return;
		}
		if (ViewModel.SelectedSidebarItem is { Feed: not null } or { Folder: not null })
		{
			await ConfirmDeleteAsync(ViewModel.SelectedSidebarItem);
		}
	}
}
