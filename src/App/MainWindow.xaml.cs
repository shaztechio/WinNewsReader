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
	/// <summary>NetNewsWire's default refresh interval.</summary>
	private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

	private readonly DispatcherQueueTimer _refreshTimer;
	private bool _syncingSelection;
	private bool _articleViewReady;

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

	public MainWindow(MainViewModel viewModel)
	{
		ViewModel = viewModel;
		InitializeComponent();

		RestorePlacement(ViewModel.Settings.Window);
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
		SidebarSplitter.Column = SidebarColumn;
		TimelineSplitter.Column = TimelineColumn;
		Timeline.ItemsSource = ViewModel.Articles;
		ViewModel.PropertyChanged += OnViewModelPropertyChanged;
		ViewModel.SidebarExpansionChanged += (_, _) => ScheduleExpansionSync();
		ScheduleExpansionSync();
		// After the first layout, when the sidebar has rows to show the restored selection.
		Root.Loaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ViewModel.RestoreSelection);
		Closed += (_, _) => ViewModel.SaveState(new WindowPlacement(
			_normalBounds.X, _normalBounds.Y, _normalBounds.Width, _normalBounds.Height,
			AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized },
			SidebarColumn.ActualWidth, TimelineColumn.ActualWidth));
		AddShortcuts();
		_ = InitializeArticleViewAsync();

		// Refresh at launch and then every hour, as NetNewsWire does by default.
		_refreshTimer = DispatcherQueue.CreateTimer();
		_refreshTimer.Interval = RefreshInterval;
		_refreshTimer.Tick += (_, _) => _ = ViewModel.RefreshAsync();
		_refreshTimer.Start();
		_ = ViewModel.RefreshAsync();
		Closed += (_, _) => _refreshTimer.Stop();
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
		TimelineColumn.Width = new GridLength(Math.Clamp(placement.TimelineWidth, TimelineSplitter.MinimumWidth, TimelineSplitter.MaximumWidth));
	}

	private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)
		{
			case nameof(MainViewModel.SelectedSidebarItem):
				// After layout: when the sidebar was just rebuilt, the new row has no control to highlight yet.
				DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
				{
					if (Sidebar.SelectedItem != ViewModel.SelectedSidebarItem)
					{
						SyncSelection(() => Sidebar.SelectedItem = ViewModel.SelectedSidebarItem);
					}
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
						Timeline.SelectedItem = ViewModel.SelectedArticle;
					}));
				}
				break;
			case nameof(MainViewModel.SelectedArticle):
				SyncSelection(() => Timeline.SelectedItem = ViewModel.SelectedArticle);
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

	private void OnSidebarDragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs args)
	{
		if (args.Items.FirstOrDefault() is SidebarItem { Kind: SidebarItemKind.Feed, Feed: { } feed } item)
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

	private void OnSidebarDragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs args) => _draggedFeed = null;

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
		else
		{
			e.AcceptedOperation = DataPackageOperation.None;
		}
		e.Handled = true;
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
			ViewModel.SelectedArticle = Timeline.SelectedItem as ArticleItem;
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
		if (!_articleViewReady || item?.ArticleId == _shownArticleId)
		{
			return;
		}
		_shownArticleId = item?.ArticleId;
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

	private async void OnAddFreshRssAccount(object sender, RoutedEventArgs e) => await ShowAddFreshRssAccountDialogAsync();

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

	private void OnCleanUp(object sender, RoutedEventArgs e) => ViewModel.CleanUp();

	private void OnToggleArticleJavaScript(object sender, RoutedEventArgs e) => ViewModel.SetArticleJavaScriptEnabled(!ViewModel.IsArticleJavaScriptEnabled);

	/// <summary>The themes, with the current one checked, then the themes folder, where the reader adds .nnwtheme folders.</summary>
	private void OnThemeMenuOpening(object? sender, object e)
	{
		var menu = (MenuFlyout)sender!;
		menu.Items.Clear();
		ViewModel.Themes.Reload();
		var current = ViewModel.CurrentTheme.Name;
		foreach (var theme in ViewModel.Themes.All)
		{
			var item = new RadioMenuFlyoutItem { Text = theme.Name, GroupName = "ArticleTheme", IsChecked = theme.Name == current };
			item.Click += (_, _) => ViewModel.SetTheme(theme.Name);
			menu.Items.Add(item);
			if (theme == ViewModel.Themes.Default)
			{
				menu.Items.Add(new MenuFlyoutSeparator());
			}
		}
		menu.Items.Add(new MenuFlyoutSeparator());
		menu.Items.Add(MenuItem("Install Theme…", async () => await InstallThemeFromFileAsync()));
		menu.Items.Add(MenuItem("Install Theme from URL…", async () => await InstallThemeFromUrlAsync()));
		menu.Items.Add(MenuItem("Open Themes Folder", () =>
		{
			Directory.CreateDirectory(ViewModel.Themes.UserThemesDirectory);
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ViewModel.Themes.UserThemesDirectory) { UseShellExecute = true });
		}));
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
					Foreground = ThemeBrush("SystemFillColorCautionBrush"),
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

	private void OnTextSizeMenuOpening(object? sender, object e)
	{
		var menu = (MenuFlyout)sender!;
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

	private void OnToggleStarred(object sender, RoutedEventArgs e) => ViewModel.ToggleStarred();

	private void OnNextUnread(object sender, RoutedEventArgs e) => ViewModel.SelectNextUnread();

	private void OnToggleReaderView(object sender, RoutedEventArgs e) => ViewModel.ToggleReaderView();

	private void OnOpenInBrowser(object sender, RoutedEventArgs e) => MainViewModel.OpenInBrowser(ViewModel.SelectedArticle?.LinkUrl);

	// Context menus

	/// <summary>NetNewsWire's sidebar menus (parity rows SB-11 to SB-13), minus items whose features aren't built yet.</summary>
	private void OnSidebarMenuOpening(object? sender, object e)
	{
		var menu = (MenuFlyout)sender!;
		menu.Items.Clear();
		if (DataContextOf(menu.Target) is not SidebarItem item)
		{
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
			menu.Items.Add(MenuItem("Rename…", async () => await ShowRenameFeedDialogAsync(item)));
			menu.Items.Add(MenuItem("Delete", async () => await ConfirmDeleteAsync(item)));
			menu.Items.Add(new MenuFlyoutSeparator());
			menu.Items.Add(MenuItem("Get Info", async () => await ShowInspectorAsync(item)));
		}
		else if (item.Folder is not null)
		{
			menu.Items.Add(new MenuFlyoutSeparator());
			menu.Items.Add(MenuItem("Rename…", async () => await ShowRenameFolderDialogAsync(item)));
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
	private void OnArticleMenuOpening(object? sender, object e)
	{
		var menu = (MenuFlyout)sender!;
		menu.Items.Clear();
		if (DataContextOf(menu.Target) is not ArticleItem article)
		{
			return;
		}
		menu.Items.Add(MenuItem(article.IsRead ? "Mark as Unread" : "Mark as Read", () => ViewModel.ToggleRead(article)));
		menu.Items.Add(MenuItem(article.IsStarred ? "Mark as Unstarred" : "Mark as Starred", () => ViewModel.ToggleStarred(article)));
		menu.Items.Add(new MenuFlyoutSeparator());
		menu.Items.Add(MenuItem("Open in Browser", () => MainViewModel.OpenInBrowser(article.LinkUrl), article.LinkUrl is not null));
		menu.Items.Add(MenuItem("Copy Article URL", () => CopyToClipboard(article.Article.Url), article.Article.Url is not null));
		menu.Items.Add(MenuItem("Copy External URL", () => CopyToClipboard(article.Article.ExternalUrl), article.Article.ExternalUrl is not null));
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
	private async Task ShowNewFeedDialogAsync()
	{
		var url = new TextBox { Header = "URL", PlaceholderText = "Feed or website address" };
		if (await ClipboardUrlAsync() is { } clipboardUrl)
		{
			url.Text = clipboardUrl;
			url.SelectAll();
		}
		var accounts = ViewModel.Accounts.ToList();
		var accountBox = new ComboBox
		{
			Header = "Account",
			ItemsSource = accounts.Select(account => account.Name).ToList(),
			SelectedIndex = Math.Max(0, accounts.IndexOf(ViewModel.DefaultAccountForNewFeed)),
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
		ShowFolders(ViewModel.DefaultFolderForNewFeed);
		accountBox.SelectionChanged += (_, _) => ShowFolders(null);
		var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
		var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Foreground = ThemeBrush("SystemFillColorCriticalBrush") };
		var dialog = Dialog("New Feed", "Add", new StackPanel { Spacing = 12, Width = 420, Children = { url, accountBox, folderBox, progress, error } });
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
				await ViewModel.SubscribeAsync(url.Text.Trim(), accounts[Math.Max(0, accountBox.SelectedIndex)], folders[Math.Max(0, folderBox.SelectedIndex)].Folder);
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

	private async Task ShowRenameFeedDialogAsync(SidebarItem item)
	{
		if (await PromptAsync("Rename Feed", "Rename", "Name", item.Title) is { } name)
		{
			await RunReportingErrorsAsync("Couldn’t Rename Feed", () => ViewModel.RenameFeedAsync(item, name));
		}
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

	private async Task ShowRenameFolderDialogAsync(SidebarItem item)
	{
		if (await PromptAsync("Rename Folder", "Rename", "Name", item.Title) is { } name)
		{
			await RunReportingErrorsAsync("Couldn’t Rename Folder", () => ViewModel.RenameFolderAsync(item, name));
		}
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

	/// <summary>NetNewsWire's FreshRSS account sheet: server address, username and API password, checked before saving.</summary>
	private async Task ShowAddFreshRssAccountDialogAsync()
	{
		var server = new TextBox { Header = "Server", PlaceholderText = "https://freshrss.example.com" };
		var username = new TextBox { Header = "Username" };
		var password = new PasswordBox { Header = "API password" };
		var help = new TextBlock
		{
			Text = "Use the API password from FreshRSS’s Settings → Profile, with “Allow API access” turned on in its authentication settings.",
			TextWrapping = TextWrapping.Wrap,
			Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
		};
		var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
		var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Foreground = ThemeBrush("SystemFillColorCriticalBrush") };
		var dialog = Dialog("Add FreshRSS Account", "Add", new StackPanel { Spacing = 12, Width = 420, Children = { server, username, password, help, progress, error } });
		void UpdateEnabled() => dialog.IsPrimaryButtonEnabled = server.Text.Trim().Length > 0 && username.Text.Trim().Length > 0 && password.Password.Length > 0;
		UpdateEnabled();
		server.TextChanged += (_, _) => UpdateEnabled();
		username.TextChanged += (_, _) => UpdateEnabled();
		password.PasswordChanged += (_, _) => UpdateEnabled();
		dialog.PrimaryButtonClick += async (_, args) =>
		{
			var deferral = args.GetDeferral();
			progress.Visibility = Visibility.Visible;
			error.Visibility = Visibility.Collapsed;
			dialog.IsPrimaryButtonEnabled = false;
			try
			{
				await ViewModel.AddFreshRssAccountAsync(server.Text.Trim(), username.Text.Trim(), password.Password);
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
				UpdateEnabled();
				deferral.Complete();
			}
		};
		await ShowDialogAsync(dialog);
	}

	private async Task ConfirmRemoveAccountAsync(IAccount account)
	{
		var dialog = Dialog("Remove Account", "Remove", new TextBlock
		{
			Text = $"Remove the “{account.Name}” account? Its feeds and articles stay on the server; only this PC’s copy goes.",
			TextWrapping = TextWrapping.Wrap,
		});
		if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
		{
			ViewModel.RemoveAccount(account);
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

	private static Brush ThemeBrush(string key) => (Brush)Application.Current.Resources[key];

	// Import and export

	private async Task ImportAsync()
	{
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
			await ViewModel.ImportOpmlAsync(file.Path);
		}
		catch (Exception ex) when (ex is OpmlException or IOException or UnauthorizedAccessException)
		{
			await ShowMessageAsync("Couldn’t Import Subscriptions", ex.Message);
		}
	}

	private async Task ExportAsync()
	{
		var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "Subscriptions-On This PC" };
		picker.FileTypeChoices.Add("OPML", [".opml"]);
		WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
		if (await picker.PickSaveFileAsync() is not { } file)
		{
			return;
		}
		try
		{
			ViewModel.ExportOpml(file.Path);
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
			if (e.ctrlKey && !e.altKey && ['a', 'c', 'f', 'v', 'x', 'y', 'z', 'Insert'].includes(key.length === 1 ? key.toLowerCase() : key)) return;
			if (['ArrowUp', 'ArrowDown', 'ArrowRight', 'PageUp', 'PageDown', 'Home', 'End', 'Tab'].includes(key)) return;
			if (key === ' ' && !e.ctrlKey && !e.altKey && (e.shiftKey || innerHeight + scrollY < document.documentElement.scrollHeight - 4)) return;
			if (key === 'Enter' && t && t.closest && t.closest('a, button')) return;
			if (!e.ctrlKey && !e.altKey && key.length !== 1 && !['Enter', 'ArrowLeft', 'F5'].includes(key)) return;
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
		Add(Slash, Ctrl, null, ViewModel.SelectNextUnread);
		Add(VirtualKey.Number1, Ctrl, null, () => ViewModel.SelectSmartFeed(SidebarItemKind.Today));
		Add(VirtualKey.Number2, Ctrl, null, () => ViewModel.SelectSmartFeed(SidebarItemKind.AllUnread));
		Add(VirtualKey.Number3, Ctrl, null, () => ViewModel.SelectSmartFeed(SidebarItemKind.Starred));
		Add(VirtualKey.Q, Ctrl, null, Close);
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
		if (ViewModel.SelectedSidebarItem is { Feed: not null } or { Folder: not null })
		{
			await ConfirmDeleteAsync(ViewModel.SelectedSidebarItem);
		}
	}
}
