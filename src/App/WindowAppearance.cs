using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using WinNewsReader.Core;
using WinRT.Interop;

namespace WinNewsReader.App;

/// <summary>Light, Dark or System for a window: its content, its title bar, and the web content in it.</summary>
public static class WindowAppearance
{
	private const int DwmUseImmersiveDarkMode = 20;

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

	/// <summary>Follows the window's theme with its title bar, whichever of the app's setting or Windows' own changes it.</summary>
	public static void Track(Window window, FrameworkElement root)
	{
		root.ActualThemeChanged += (_, _) => UpdateTitleBar(window, root);
		UpdateTitleBar(window, root);
	}

	public static void Apply(Window window, FrameworkElement root, AppAppearance appearance)
	{
		root.RequestedTheme = ElementThemeFor(appearance);
		UpdateTitleBar(window, root);
	}

	public static ElementTheme ElementThemeFor(AppAppearance appearance) => appearance switch
	{
		AppAppearance.Light => ElementTheme.Light,
		AppAppearance.Dark => ElementTheme.Dark,
		_ => ElementTheme.Default,
	};

	/// <summary>
	/// What pages in a web view see as <c>prefers-color-scheme</c>: the app's choice, or Windows' own for System. An
	/// article's theme CSS follows it, so a dark article goes with a dark app.
	/// </summary>
	public static CoreWebView2PreferredColorScheme WebColorSchemeFor(AppAppearance appearance) => appearance switch
	{
		AppAppearance.Light => CoreWebView2PreferredColorScheme.Light,
		AppAppearance.Dark => CoreWebView2PreferredColorScheme.Dark,
		_ => CoreWebView2PreferredColorScheme.Auto,
	};

	private static void UpdateTitleBar(Window window, FrameworkElement root)
	{
		var dark = root.ActualTheme == ElementTheme.Dark ? 1 : 0;
		_ = DwmSetWindowAttribute(WindowNative.GetWindowHandle(window), DwmUseImmersiveDarkMode, ref dark, sizeof(int));
	}
}
