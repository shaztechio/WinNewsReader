using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using WinNewsReader.Core;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace WinNewsReader.App;

/// <summary>One native notification-area icon, owned by a main window and recreated after Explorer restarts.</summary>
internal sealed class SystemTray : IDisposable
{
	private const uint CallbackMessage = 0x8001;
	private readonly MainWindow _window;
	private readonly Action _exit;
	private readonly Action _open;
	private readonly SubclassProc _callback;
	private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
	private NotifyIconData _data;
	private readonly nint _baseIcon;
	private string? _badgeText;
	private string? _badgeDescription;
	private readonly uint _taskbarButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");
	private bool _disposed;
	public bool IsAvailable { get; private set; }

	public SystemTray(MainWindow window, Action open, Action exit)
	{
		_window = window;
		_exit = exit;
		_open = open;
		_callback = WindowProc;
		_data = new NotifyIconData
		{
			Size = (uint)Marshal.SizeOf<NotifyIconData>(),
			Window = WindowNative.GetWindowHandle(window),
			Id = 1, Flags = 7, Callback = CallbackMessage,
			Icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), 1, 0, 0, 0x10),
			Tip = "WinNewsReader", Info = "", InfoTitle = ""
		};
		_baseIcon = _data.Icon;
		if (_data.Icon == 0 || !SetWindowSubclass(_data.Window, _callback, 1, 0))
		{
			if (_data.Icon != 0) DestroyIcon(_data.Icon);
			throw new Win32Exception();
		}
		if (!(IsAvailable = Shell_NotifyIcon(0, ref _data)))
		{
			Dispose();
			throw new Win32Exception();
		}
		_window.ViewModel.UnreadBadgeChanged += OnUnreadBadgeChanged;
		UpdateBadge();
	}

	private void OnUnreadBadgeChanged(object? sender, EventArgs args) => UpdateBadge();

	private void UpdateBadge()
	{
		var viewModel = _window.ViewModel;
		var text = UnreadBadge.Text(viewModel.TotalUnread, viewModel.Settings.UnreadCounts);
		var description = UnreadBadge.Description(viewModel.TotalUnread, viewModel.Settings.UnreadCounts);
		if (text == _badgeText && description == _badgeDescription) return;
		var icon = _baseIcon;
		if (text is not null)
		{
			using var bitmap = new Bitmap(32, 32);
			using (var graphics = Graphics.FromImage(bitmap))
			{
				graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
				using var appIcon = Icon.FromHandle(_baseIcon);
				graphics.DrawIcon(appIcon, new Rectangle(0, 0, 32, 32));
				var badgeHandle = TaskbarBadge.Render(text);
				try
				{
					using var badge = Icon.FromHandle(badgeHandle);
					graphics.DrawIcon(badge, new Rectangle(10, 10, 22, 22));
				}
				finally { DestroyIcon(badgeHandle); }
			}
			icon = bitmap.GetHicon();
		}
		var previous = _data.Icon;
		_data.Icon = icon;
		_data.Tip = text is null ? "WinNewsReader" : $"WinNewsReader — {UnreadBadge.Description(viewModel.TotalUnread, viewModel.Settings.UnreadCounts)}";
		var applied = Shell_NotifyIcon(1, ref _data);
		_badgeText = applied ? text : null;
		_badgeDescription = applied ? description : null;
		if (previous != _baseIcon) DestroyIcon(previous);
	}

	private nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
	{
		if (message == _taskbarCreated)
		{
			// If Explorer cannot recreate the icon, keep the window reachable.
			if (!(IsAvailable = Shell_NotifyIcon(0, ref _data))) _open();
		}
		else if (message == _taskbarButtonCreated)
		{
			_window.RefreshTaskbarBadge();
		}
		else if (message == CallbackMessage)
		{
			switch ((uint)lParam)
			{
				case 0x202: // WM_LBUTTONUP
				case 0x403: // NIN_SELECT (keyboard activation)
					_open();
					break;
				case 0x205: // WM_RBUTTONUP
				case 0x7B: // WM_CONTEXTMENU
					ShowMenu();
					break;
			}
			return 0;
		}
		return DefSubclassProc(hwnd, message, wParam, lParam);
	}

	private void ShowMenu()
	{
		var menu = CreatePopupMenu();
		if (menu == 0) return;
		uint command;
		try
		{
			AppendMenu(menu, 0, 1, "Open WinNewsReader");
			AppendMenu(menu, 0, 2, "Exit");
			GetCursorPos(out var point);
			SetForegroundWindow(_data.Window);
			command = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, _data.Window, 0);
			PostMessage(_data.Window, 0, 0, 0);
		}
		finally { DestroyMenu(menu); }
		// Defer closing until this native callback has returned.
		if (command == 1) _window.DispatcherQueue.TryEnqueue(() => _open());
		if (command == 2) _window.DispatcherQueue.TryEnqueue(() => _exit());
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_window.ViewModel.UnreadBadgeChanged -= OnUnreadBadgeChanged;
		IsAvailable = false;
		Shell_NotifyIcon(2, ref _data);
		RemoveWindowSubclass(_data.Window, _callback, 1);
		if (_data.Icon != _baseIcon) DestroyIcon(_data.Icon);
		DestroyIcon(_baseIcon);
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct NotifyIconData
	{
		public uint Size;
		public nint Window;
		public uint Id, Flags, Callback;
		public nint Icon;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
		public uint State, StateMask;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
		public uint TimeoutOrVersion;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
		public uint InfoFlags;
		public Guid Guid;
		public nint BalloonIcon;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Point { public int X, Y; }
	private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
	[DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
	[DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
	[DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
	[DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
	[DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
	[DllImport("user32.dll")] private static extern nint CreatePopupMenu();
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
	[DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
	[DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
	[DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
	[DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
	[DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
}
