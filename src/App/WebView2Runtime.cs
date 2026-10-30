using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace WinNewsReader.App;

/// <summary>
/// The WebView2 runtime that shows articles. The Velopack installer sets it up, but a Microsoft Store package can't, so that
/// package carries Microsoft's small bootstrapper beside the exe and this runs it on a PC that lacks the runtime.
/// </summary>
public static class WebView2Runtime
{
	private const string BootstrapperName = "MicrosoftEdgeWebview2Setup.exe";
	private const string DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

	public static bool IsInstalled()
	{
		try
		{
			return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>
	/// If the runtime is missing and this copy carries the bootstrapper, asks to install it and waits for it. Does nothing
	/// otherwise. If the runtime still isn't there, the article pane shows the WebView2 control's own message.
	/// </summary>
	public static void EnsureInstalled()
	{
		var bootstrapper = Path.Combine(AppContext.BaseDirectory, BootstrapperName);
		if (!File.Exists(bootstrapper) || IsInstalled())
		{
			return;
		}
		const uint OkCancel = 0x1, Information = 0x40, Error = 0x10, Ok = 1;
		if (MessageBox(IntPtr.Zero, "WinNewsReader shows articles with Microsoft Edge WebView2, which this PC doesn’t have yet. Install it now? Windows may ask for permission.", "WinNewsReader", OkCancel | Information) != Ok)
		{
			return;
		}
		string detail;
		try
		{
			// Run a copy from the temp folder: a program inside the package folder is read-only and runs with the package's identity.
			var copy = Path.Combine(Path.GetTempPath(), "WinNewsReader-" + BootstrapperName);
			File.Copy(bootstrapper, copy, overwrite: true);
			using var setup = Process.Start(new ProcessStartInfo(copy, "/silent /install") { UseShellExecute = true });
			setup?.WaitForExit();
			detail = $"The installer ended with code {setup?.ExitCode}.";
		}
		catch (Exception ex)
		{
			// The user declined Windows' permission prompt, or it couldn't start.
			detail = $"The installer couldn’t run: {ex.Message}";
		}
		if (!IsInstalled())
		{
			MessageBox(IntPtr.Zero, $"WebView2 wasn’t installed, so articles can’t be shown. {detail} You can install it from {DownloadUrl} and start WinNewsReader again.", "WinNewsReader", Error);
		}
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
