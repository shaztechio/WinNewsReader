using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
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
		AddKeyboardAccelerators();
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
				SyncSelection(() => Timeline.ItemsSource = ViewModel.Articles);
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

	private async void OnSidebarKeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key is VirtualKey.Delete or VirtualKey.Back && ViewModel.SelectedSidebarItem is { Feed: not null } or { Folder: not null })
		{
			e.Handled = true;
			await ConfirmDeleteAsync(ViewModel.SelectedSidebarItem!);
		}
	}

	// Article view

	private async Task InitializeArticleViewAsync()
	{
		ArticleView.DefaultBackgroundColor = Colors.Transparent;
		await ArticleView.EnsureCoreWebView2Async();
		var core = ArticleView.CoreWebView2;
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

	/// <summary>The Windows mappings of NetNewsWire's menu shortcuts from the parity matrix, for the commands built so far.</summary>
	private void AddKeyboardAccelerators()
	{
		const VirtualKeyModifiers Ctrl = VirtualKeyModifiers.Control;
		const VirtualKeyModifiers CtrlShift = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift;
		const VirtualKeyModifiers CtrlAlt = VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu;
		const VirtualKey Slash = (VirtualKey)191;

		Add(VirtualKey.R, Ctrl, () => _ = ViewModel.RefreshAsync());
		Add(VirtualKey.F5, VirtualKeyModifiers.None, () => _ = ViewModel.RefreshAsync());
		Add(VirtualKey.N, Ctrl, () => _ = ShowNewFeedDialogAsync());
		Add(VirtualKey.N, CtrlShift, () => _ = ShowNewFolderDialogAsync());
		Add(VirtualKey.E, CtrlAlt, () => _ = ExportAsync());
		Add(VirtualKey.K, Ctrl, () => ViewModel.MarkAllRead());
		Add(VirtualKey.U, CtrlShift, () => ViewModel.ToggleRead());
		Add(VirtualKey.L, CtrlShift, () => ViewModel.ToggleStarred());
		Add(Slash, Ctrl, ViewModel.SelectNextUnread);
		Add(VirtualKey.Number1, Ctrl, () => ViewModel.SelectSmartFeed(SidebarItemKind.Today));
		Add(VirtualKey.Number2, Ctrl, () => ViewModel.SelectSmartFeed(SidebarItemKind.AllUnread));
		Add(VirtualKey.Number3, Ctrl, () => ViewModel.SelectSmartFeed(SidebarItemKind.Starred));
		Add(VirtualKey.Q, Ctrl, Close);

		void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
		{
			var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
			accelerator.Invoked += (_, args) =>
			{
				args.Handled = true;
				if (!_dialogOpen)
				{
					action();
				}
			};
			Root.KeyboardAccelerators.Add(accelerator);
		}
	}
}
