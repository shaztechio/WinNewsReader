using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinNewsReader.Core;
using WinRT.Interop;

namespace WinNewsReader.App;

/// <summary>
/// NetNewsWire's Dock badge (<c>updateDockBadge</c>) as a taskbar overlay: the unread total in a red circle on the app's
/// taskbar button, or a dot, or nothing, following the Unread Counts preference. Windows' own badge API needs a packaged
/// app, so this draws the overlay and hands it to the taskbar.
/// </summary>
public sealed class TaskbarBadge(Window window)
{
	private ITaskbarList3? _taskbar;
	private string? _shownText;
	private bool _applied;

	/// <summary>Shows the badge for <paramref name="unread"/> as <paramref name="display"/> says, or takes it away.</summary>
	public void Update(int unread, UnreadCountDisplay display)
	{
		var text = UnreadBadge.Text(unread, display);
		// An overlay set before the taskbar button exists is lost, so one that didn't take is tried again.
		if (_applied && text == _shownText)
		{
			return;
		}
		_applied = Apply(text, UnreadBadge.Description(unread, display));
		_shownText = text;
	}

	/// <summary>Tries again if the last attempt didn't take, such as one made before the taskbar button existed.</summary>
	public void Reapply(int unread, UnreadCountDisplay display)
	{
		if (!_applied)
		{
			Update(unread, display);
		}
	}

	private bool Apply(string? text, string description)
	{
		try
		{
			_taskbar ??= CreateTaskbar();
			var icon = text is null ? IntPtr.Zero : Render(text);
			try
			{
				return _taskbar.SetOverlayIcon(WindowNative.GetWindowHandle(window), icon, text is null ? null : description) == 0;
			}
			finally
			{
				if (icon != IntPtr.Zero)
				{
					DestroyIcon(icon);
				}
			}
		}
		catch (Exception ex) when (ex is COMException or InvalidCastException)
		{
			return false;
		}
	}

	private static ITaskbarList3 CreateTaskbar()
	{
		var taskbar = (ITaskbarList3)new TaskbarList();
		_ = taskbar.HrInit();
		return taskbar;
	}

	/// <summary>The overlay: Windows shows it at 16 px, so it's drawn at 32 for sharpness on high-DPI screens.</summary>
	internal static IntPtr Render(string text)
	{
		const int Size = 32;
		using var bitmap = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
		using (var graphics = Graphics.FromImage(bitmap))
		{
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
			var red = Color.FromArgb(0xD1, 0x34, 0x38);
			using var fill = new SolidBrush(red);
			if (text == UnreadBadge.DotText)
			{
				// A dot is smaller than the circle that holds a number, and has a white edge to stand out from the icon.
				using var edge = new SolidBrush(Color.White);
				graphics.FillEllipse(edge, 8, 8, 17, 17);
				graphics.FillEllipse(fill, 10, 10, 13, 13);
			}
			else
			{
				graphics.FillEllipse(fill, 0, 0, Size - 1, Size - 1);
				var fontSize = text.Length switch { 1 => 20f, 2 => 17f, _ => 13f };
				using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
				using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
				// Segoe UI sits a little high in its line box; nudge it down so the digits are centered in the circle.
				graphics.DrawString(text, font, Brushes.White, new RectangleF(0, 1.5f, Size, Size), format);
			}
		}
		return bitmap.GetHicon();
	}

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DestroyIcon(IntPtr icon);

	[ComImport, Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
	private class TaskbarList;

	/// <summary>The taskbar's COM interface, with every method in its vtable order, since COM calls by position.</summary>
	[ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface ITaskbarList3
	{
		// ITaskbarList
		[PreserveSig] int HrInit();
		[PreserveSig] int AddTab(IntPtr window);
		[PreserveSig] int DeleteTab(IntPtr window);
		[PreserveSig] int ActivateTab(IntPtr window);
		[PreserveSig] int SetActiveAlt(IntPtr window);

		// ITaskbarList2
		[PreserveSig] int MarkFullscreenWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

		// ITaskbarList3
		[PreserveSig] int SetProgressValue(IntPtr window, ulong completed, ulong total);
		[PreserveSig] int SetProgressState(IntPtr window, int flags);
		[PreserveSig] int RegisterTab(IntPtr tab, IntPtr mdi);
		[PreserveSig] int UnregisterTab(IntPtr tab);
		[PreserveSig] int SetTabOrder(IntPtr tab, IntPtr insertBefore);
		[PreserveSig] int SetTabActive(IntPtr tab, IntPtr mdi, uint reserved);
		[PreserveSig] int ThumbBarAddButtons(IntPtr window, uint count, IntPtr buttons);
		[PreserveSig] int ThumbBarUpdateButtons(IntPtr window, uint count, IntPtr buttons);
		[PreserveSig] int ThumbBarSetImageList(IntPtr window, IntPtr imageList);
		[PreserveSig] int SetOverlayIcon(IntPtr window, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
		[PreserveSig] int SetThumbnailTooltip(IntPtr window, [MarshalAs(UnmanagedType.LPWStr)] string tip);
		[PreserveSig] int SetThumbnailClip(IntPtr window, IntPtr clip);
	}
}
