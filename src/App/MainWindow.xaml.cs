using System.ComponentModel;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using WinNewsReader.App.ViewModels;
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
	private bool _dialogOpen;
	private string? _shownArticleId;

	public MainWindow(MainViewModel viewModel)
	{
		ViewModel = viewModel;
		InitializeComponent();

		AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 860));
		AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
		SystemBackdrop = MicaController.IsSupported() ? new MicaBackdrop() : new DesktopAcrylicBackdrop();
		SidebarSplitter.Column = SidebarColumn;
		TimelineSplitter.Column = TimelineColumn;
		Timeline.ItemsSource = ViewModel.Articles;
		ViewModel.PropertyChanged += OnViewModelPropertyChanged;
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

	private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)
		{
			case nameof(MainViewModel.SelectedSidebarItem):
				SyncSelection(() => Sidebar.SelectedItem = ViewModel.SelectedSidebarItem);
				break;
			case nameof(MainViewModel.Articles):
				// Swapping the list removes the focused row, so keep keyboard focus in the timeline.
				var timelineFocused = PaneOf(FocusManager.GetFocusedElement(Content.XamlRoot) as DependencyObject) == Pane.Timeline;
				SyncSelection(() => Timeline.ItemsSource = ViewModel.Articles);
				if (timelineFocused)
				{
					// After layout, once the new rows exist to take focus.
					DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Timeline.Focus(FocusState.Keyboard));
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
		await core.AddScriptToExecuteOnDocumentCreatedAsync(ArticleKeysScript);
		core.WebMessageReceived += OnArticleMessage;
		// Links open in the default browser; the pane only ever shows the article.
		core.NavigationStarting += (_, args) =>
		{
			if (IsWebUrl(args.Uri))
			{
				args.Cancel = true;
				MainViewModel.OpenInBrowser(args.Uri);
			}
		};
		core.NewWindowRequested += (_, args) =>
		{
			args.Handled = true;
			MainViewModel.OpenInBrowser(args.Uri);
		};
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
		ArticleView.NavigateToString(item is null ? ArticleHtml.Empty : ArticleHtml.Render(item.Article, ViewModel.FeedFor(item.Article)));
	}

	// Toolbar

	private void OnRefresh(object sender, RoutedEventArgs e) => _ = ViewModel.RefreshAsync();

	private async void OnNewFeed(object sender, RoutedEventArgs e) => await ShowNewFeedDialogAsync();

	private async void OnNewFolder(object sender, RoutedEventArgs e) => await ShowNewFolderDialogAsync();

	private async void OnImport(object sender, RoutedEventArgs e) => await ImportAsync();

	private async void OnExport(object sender, RoutedEventArgs e) => await ExportAsync();

	private void OnMarkAllRead(object sender, RoutedEventArgs e) => ViewModel.MarkAllRead();

	private void OnToggleRead(object sender, RoutedEventArgs e) => ViewModel.ToggleRead();

	private void OnToggleStarred(object sender, RoutedEventArgs e) => ViewModel.ToggleStarred();

	private void OnNextUnread(object sender, RoutedEventArgs e) => ViewModel.SelectNextUnread();

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
			menu.Items.Add(MenuItem("Delete", async () => await ConfirmDeleteAsync(item)));
		}
		else if (item.Folder is not null)
		{
			menu.Items.Add(new MenuFlyoutSeparator());
			menu.Items.Add(MenuItem("Rename…", async () => await ShowRenameFolderDialogAsync(item)));
			menu.Items.Add(MenuItem("Delete", async () => await ConfirmDeleteAsync(item)));
		}
		else if (item.Kind == SidebarItemKind.AccountGroup)
		{
			menu.Items.Add(new MenuFlyoutSeparator());
			menu.Items.Add(MenuItem("New Feed…", async () => await ShowNewFeedDialogAsync()));
			menu.Items.Add(MenuItem("New Folder…", async () => await ShowNewFolderDialogAsync()));
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

	/// <summary>NetNewsWire's Add Feed sheet: URL, prefilled from the clipboard, and a folder.</summary>
	private async Task ShowNewFeedDialogAsync()
	{
		var url = new TextBox { Header = "URL", PlaceholderText = "Feed or website address" };
		if (await ClipboardUrlAsync() is { } clipboardUrl)
		{
			url.Text = clipboardUrl;
			url.SelectAll();
		}
		var folders = new List<(string Name, StoredFolder? Folder)> { ("On This PC", null) };
		folders.AddRange(ViewModel.Folders.Select(folder => (folder.Name, (StoredFolder?)folder)));
		var folderBox = new ComboBox
		{
			Header = "Folder",
			ItemsSource = folders.Select(folder => folder.Name).ToList(),
			SelectedIndex = Math.Max(0, folders.FindIndex(folder => folder.Folder?.FolderId == ViewModel.DefaultFolderForNewFeed?.FolderId)),
			HorizontalAlignment = HorizontalAlignment.Stretch,
		};
		var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
		var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Foreground = ThemeBrush("SystemFillColorCriticalBrush") };
		var dialog = Dialog("New Feed", "Add", new StackPanel { Spacing = 12, Width = 420, Children = { url, folderBox, progress, error } });
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
				await ViewModel.SubscribeAsync(url.Text.Trim(), folders[folderBox.SelectedIndex].Folder);
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

	private async Task ShowRenameFolderDialogAsync(SidebarItem item)
	{
		if (await PromptAsync("Rename Folder", "Rename", "Name", item.Title) is { } name)
		{
			ViewModel.RenameFolder(item, name);
		}
	}

	private async Task ConfirmDeleteAsync(SidebarItem item)
	{
		var kind = item.Folder is not null ? "folder" : "feed";
		var dialog = Dialog("Delete", "Delete", new TextBlock { Text = $"Are you sure you want to delete the “{item.Title}” {kind}?", TextWrapping = TextWrapping.Wrap });
		if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
		{
			ViewModel.Delete(item);
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
		addEventListener('keydown', e => {
			const t = e.target;
			if (t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName))) return;
			const key = e.key;
			if (e.ctrlKey && !e.altKey && ['a', 'c', 'f', 'v', 'x', 'y', 'z', 'Insert'].includes(key.length === 1 ? key.toLowerCase() : key)) return;
			if (['ArrowUp', 'ArrowDown', 'ArrowRight', 'PageUp', 'PageDown', 'Home', 'End', 'Tab'].includes(key)) return;
			if (key === ' ' && !e.ctrlKey && !e.altKey && (e.shiftKey || innerHeight + scrollY < document.documentElement.scrollHeight - 4)) return;
			if (key === 'Enter' && t && t.closest && t.closest('a, button')) return;
			if (!e.ctrlKey && !e.altKey && key.length !== 1 && !['Enter', 'ArrowLeft', 'F5'].includes(key)) return;
			e.preventDefault();
			chrome.webview.postMessage({ keyCode: e.keyCode, ctrl: e.ctrlKey, shift: e.shiftKey, alt: e.altKey });
		}, true);
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
		Add(Slash, Ctrl, null, ViewModel.SelectNextUnread);
		Add(VirtualKey.Number1, Ctrl, null, () => ViewModel.SelectSmartFeed(SidebarItemKind.Today));
		Add(VirtualKey.Number2, Ctrl, null, () => ViewModel.SelectSmartFeed(SidebarItemKind.AllUnread));
		Add(VirtualKey.Number3, Ctrl, null, () => ViewModel.SelectSmartFeed(SidebarItemKind.Starred));
		Add(VirtualKey.Q, Ctrl, null, Close);

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
		using var message = JsonDocument.Parse(args.WebMessageAsJson);
		var root = message.RootElement;
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
		RunShortcut((VirtualKey)root.GetProperty("keyCode").GetInt32(), modifiers, Pane.Article);
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
