using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinNewsReader.App.ViewModels;
using WinNewsReader.Core;
using WinRT.Interop;

namespace WinNewsReader.App;

/// <summary>
/// The base of NetNewsWire's small windows (Current Activity, Activity Log, Error Log, Account Stats, Dinosaurs): a titled
/// window with the app's icon, backdrop and appearance, built in code, and refreshed on a short timer while it's open.
/// </summary>
public abstract class UtilityWindow : Window
{
	[DllImport("user32.dll")]
	private static extern uint GetDpiForWindow(IntPtr window);

	private readonly DispatcherQueueTimer _timer;

	protected UtilityWindow(MainViewModel viewModel, string title, int width, int height)
	{
		ViewModel = viewModel;
		Title = title;
		Root = new Grid { Padding = new Thickness(16, 12, 16, 16), RowSpacing = 10 };
		Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		Content = Root;
		AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
		SystemBackdrop = MicaController.IsSupported() ? new MicaBackdrop() : new DesktopAcrylicBackdrop();
		var scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
		var size = new SizeInt32((int)(width * scale), (int)(height * scale));
		AppWindow.Resize(size);
		var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
		AppWindow.Move(new PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
		WindowAppearance.Track(this, Root);
		WindowAppearance.Apply(this, Root, viewModel.Settings.Appearance);
		viewModel.AppearanceChanged += OnAppearanceChanged;

		_timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
		_timer.Interval = TimeSpan.FromMilliseconds(500);
		_timer.Tick += (_, _) => Refresh();
		Closed += (_, _) =>
		{
			_timer.Stop();
			viewModel.AppearanceChanged -= OnAppearanceChanged;
		};
	}

	protected MainViewModel ViewModel { get; }

	/// <summary>Row 0 is the heading or buttons, row 1 the content.</summary>
	protected Grid Root { get; }

	/// <summary>Fills the window and starts refreshing it every half second. Called once, by the subclass's constructor.</summary>
	protected void Start()
	{
		Refresh();
		_timer.Start();
	}

	protected abstract void Refresh();

	private void OnAppearanceChanged(object? sender, EventArgs e) => WindowAppearance.Apply(this, Root, ViewModel.Settings.Appearance);

	protected static TextBlock Heading(string text) => new()
	{
		Text = text,
		Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
		TextWrapping = TextWrapping.Wrap,
	};

	protected static Button ButtonNamed(string text, Action click)
	{
		var button = new Button { Content = text };
		button.Click += (_, _) => click();
		return button;
	}

	protected static void CopyText(string text)
	{
		var package = new DataPackage();
		package.SetText(text);
		Clipboard.SetContent(package);
	}
}

/// <summary>NetNewsWire's Current Activity window: the feeds being downloaded right now.</summary>
public sealed class CurrentActivityWindow : UtilityWindow
{
	private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.None };
	private readonly TextBlock _heading = Heading("");

	public CurrentActivityWindow(MainViewModel viewModel) : base(viewModel, "Current Activity", 520, 460)
	{
		AutomationProperties.SetName(_list, "Feeds refreshing");
		Root.Children.Add(_heading);
		Grid.SetRow(_list, 1);
		Root.Children.Add(_list);
		Start();
	}

	protected override void Refresh()
	{
		var busy = ViewModel.Activity.InProgress();
		_heading.Text = busy.Count == 0 ? "Nothing is refreshing." : busy.Count == 1 ? "Refreshing 1 feed:" : $"Refreshing {busy.Count} feeds:";
		_list.ItemsSource = busy.Select(item => $"{item.Feed}  ·  {item.Account}  ·  {(int)(DateTimeOffset.Now - item.Since).TotalSeconds}s").ToList();
	}
}

/// <summary>NetNewsWire's Activity Log and Error Log windows: what refreshes did, or only what went wrong.</summary>
public sealed class ActivityLogWindow : UtilityWindow
{
	private readonly bool _errorsOnly;
	private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Extended };
	private readonly TextBlock _heading = Heading("");
	private int _shown = -1;

	public ActivityLogWindow(MainViewModel viewModel, bool errorsOnly)
		: base(viewModel, errorsOnly ? "Error Log" : "Activity Log", 760, 520)
	{
		_errorsOnly = errorsOnly;
		AutomationProperties.SetName(_list, Title);
		var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
		bar.Children.Add(_heading);
		bar.Children.Add(ButtonNamed("Copy", Copy));
		bar.Children.Add(ButtonNamed("Clear", () => ViewModel.Activity.Clear()));
		Root.Children.Add(bar);
		Grid.SetRow(_list, 1);
		Root.Children.Add(_list);
		Start();
	}

	private IReadOnlyList<ActivityEntry> Entries() => _errorsOnly ? ViewModel.Activity.Errors() : ViewModel.Activity.Entries();

	/// <summary>The newest entry first, as a line of text.</summary>
	private static string Line(ActivityEntry entry) =>
		$"{entry.Time:T}  {entry.Account}{(entry.Feed is null ? "" : "  ·  " + entry.Feed)}  ·  {entry.Message}";

	private void Copy()
	{
		var lines = _list.SelectedItems.Count > 0 ? _list.SelectedItems.Cast<string>() : _list.Items.Cast<string>();
		CopyText(string.Join(Environment.NewLine, lines));
	}

	protected override void Refresh()
	{
		var entries = Entries();
		// Rebuilding the list would drop the reader's selection, so it's left alone until something new arrives.
		if (entries.Count == _shown)
		{
			return;
		}
		_shown = entries.Count;
		_heading.Text = entries.Count == 0 ? (_errorsOnly ? "No errors." : "No activity yet.") : $"{entries.Count:N0} " + (_errorsOnly ? (entries.Count == 1 ? "error" : "errors") : (entries.Count == 1 ? "entry" : "entries"));
		_list.ItemsSource = entries.Reverse().Select(Line).ToList();
	}
}

/// <summary>NetNewsWire's Account Stats window: what each account holds.</summary>
public sealed class AccountStatsWindow : UtilityWindow
{
	private readonly StackPanel _panel = new() { Spacing = 18 };

	public AccountStatsWindow(MainViewModel viewModel) : base(viewModel, "Account Stats", 420, 520)
	{
		Root.Children.Add(Heading("What each account holds."));
		var scroller = new ScrollViewer { Content = _panel };
		Grid.SetRow(scroller, 1);
		Root.Children.Add(scroller);
		Start();
	}

	protected override void Refresh()
	{
		_panel.Children.Clear();
		foreach (var account in ViewModel.Accounts)
		{
			var stats = account.Store.Statistics();
			var block = new StackPanel { Spacing = 4 };
			block.Children.Add(new TextBlock { Text = account.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
			var grid = new Grid { ColumnSpacing = 24 };
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			(string Label, int Value)[] rows = [("Feeds", stats.Feeds), ("Folders", stats.Folders), ("Articles", stats.Articles), ("Unread", stats.Unread), ("Starred", stats.Starred)];
			for (var i = 0; i < rows.Length; i++)
			{
				grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
				var label = Heading(rows[i].Label);
				Grid.SetRow(label, i);
				var value = new TextBlock { Text = rows[i].Value.ToString("N0") };
				Grid.SetRow(value, i);
				Grid.SetColumn(value, 1);
				grid.Children.Add(label);
				grid.Children.Add(value);
			}
			block.Children.Add(grid);
			_panel.Children.Add(block);
		}
	}
}

/// <summary>
/// NetNewsWire's Dinosaurs window: feeds that haven't had a new article in six months. Select some to open their home
/// pages, copy their addresses, or delete them; deleting can be undone.
/// </summary>
public sealed class DinosaursWindow : UtilityWindow
{
	private sealed record Row(IAccount Account, Dinosaur Dinosaur)
	{
		public override string ToString() =>
			$"{NameSorting.DisplayName(Dinosaur.Feed)}  ·  last article {Dinosaur.LastUpdate:d}  ·  {Account.Name}";
	}

	private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Extended };
	private readonly TextBlock _heading = Heading("");
	private readonly Button _undo;
	private string? _shown;

	public DinosaursWindow(MainViewModel viewModel) : base(viewModel, "Dinosaurs", 640, 480)
	{
		AutomationProperties.SetName(_list, "Feeds not updated in six months");
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
		buttons.Children.Add(ButtonNamed("Open Home Page", OpenHomePages));
		buttons.Children.Add(ButtonNamed("Copy URL", CopyUrls));
		buttons.Children.Add(ButtonNamed("Delete", () => _ = DeleteAsync()));
		_undo = ButtonNamed("Undo Delete", () => _ = UndoAsync());
		buttons.Children.Add(_undo);
		var bar = new StackPanel { Spacing = 10 };
		bar.Children.Add(_heading);
		bar.Children.Add(buttons);
		Root.Children.Add(bar);
		Grid.SetRow(_list, 1);
		Root.Children.Add(_list);
		Start();
	}

	private IEnumerable<Row> Selected => _list.SelectedItems.Cast<Row>();

	private void OpenHomePages()
	{
		foreach (var row in Selected.Where(row => row.Dinosaur.Feed.HomePageUrl is not null))
		{
			MainViewModel.OpenInBrowser(row.Dinosaur.Feed.HomePageUrl);
		}
	}

	private void CopyUrls()
	{
		if (Selected.Select(row => row.Dinosaur.Feed.Url).ToList() is { Count: > 0 } urls)
		{
			CopyText(string.Join(Environment.NewLine, urls));
		}
	}

	private async Task DeleteAsync()
	{
		var rows = Selected.ToList();
		if (rows.Count == 0)
		{
			return;
		}
		var dialog = new ContentDialog
		{
			XamlRoot = Root.XamlRoot,
			Title = "Delete",
			Content = new TextBlock { Text = rows.Count == 1 ? $"Delete the “{NameSorting.DisplayName(rows[0].Dinosaur.Feed)}” feed?" : $"Delete these {rows.Count} feeds?", TextWrapping = TextWrapping.Wrap },
			PrimaryButtonText = "Delete",
			CloseButtonText = "Cancel",
			DefaultButton = ContentDialogButton.Primary,
			RequestedTheme = Root.ActualTheme,
		};
		if (await dialog.ShowAsync() != ContentDialogResult.Primary)
		{
			return;
		}
		foreach (var row in rows)
		{
			foreach (var item in ViewModel.RowsForFeed(row.Account, row.Dinosaur.Feed.FeedId))
			{
				await ViewModel.DeleteAsync(item);
			}
		}
		_shown = null;
		Refresh();
	}

	private async Task UndoAsync()
	{
		if (ViewModel.Undo.UndoLabel is { } label && label.StartsWith("Delete", StringComparison.Ordinal))
		{
			await ViewModel.Undo.UndoAsync();
			_shown = null;
			Refresh();
		}
	}

	protected override void Refresh()
	{
		var now = DateTimeOffset.UtcNow;
		var rows = ViewModel.Accounts
			.SelectMany(account => Dinosaurs.Stale(account.Store.GetFeeds(), account.Store.NewestArticleDates(), now).Select(dinosaur => new Row(account, dinosaur)))
			.OrderBy(row => row.Dinosaur.LastUpdate)
			.ToList();
		_undo.IsEnabled = ViewModel.Undo.UndoLabel?.StartsWith("Delete", StringComparison.Ordinal) == true;
		// The list is left alone, keeping the reader's selection, unless the feeds in it have changed.
		var key = string.Join('|', rows.Select(row => row.Account.AccountId + row.Dinosaur.Feed.FeedId));
		if (key == _shown)
		{
			return;
		}
		_shown = key;
		_heading.Text = rows.Count == 0 ? "No feeds have gone quiet." : rows.Count == 1 ? "1 feed hasn't updated in six months:" : $"{rows.Count} feeds haven't updated in six months:";
		_list.ItemsSource = rows;
	}
}

/// <summary>NetNewsWire's Keyboard Shortcuts window, with this app's keys, grouped as the help page groups them.</summary>
public sealed class KeyboardShortcutsWindow : UtilityWindow
{
	public KeyboardShortcutsWindow(MainViewModel viewModel) : base(viewModel, "Keyboard Shortcuts", 620, 640)
	{
		Root.Children.Add(Heading("Single keys work from any pane, but not while you're typing in a box. Ctrl stands where NetNewsWire uses Cmd."));
		var panel = new StackPanel { Spacing = 4 };
		foreach (var section in KeyboardShortcuts.All.GroupBy(entry => entry.Section))
		{
			panel.Children.Add(new TextBlock { Text = section.Key, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
			foreach (var entry in section)
			{
				var row = new Grid { ColumnSpacing = 16, Padding = new Thickness(0, 2, 0, 2) };
				row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
				row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				var keys = new TextBlock { Text = entry.Keys, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap };
				var action = new TextBlock { Text = entry.Action, TextWrapping = TextWrapping.Wrap };
				Grid.SetColumn(action, 1);
				row.Children.Add(keys);
				row.Children.Add(action);
				AutomationProperties.SetName(row, $"{entry.Keys}: {entry.Action}");
				panel.Children.Add(row);
			}
		}
		var scroller = new ScrollViewer { Content = panel };
		Grid.SetRow(scroller, 1);
		Root.Children.Add(scroller);
		Start();
	}

	// The list does not change while the window is open.
	protected override void Refresh()
	{
	}
}
