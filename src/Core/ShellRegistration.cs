using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WinNewsReader.Core;

/// <summary>
/// What the installer writes to the current user's registry so Windows hands feed links and theme files to the app, and
/// lists it under Default apps as a feed reader (NetNewsWire's <c>feed:</c> handling and <c>.nnwtheme</c> document type).
/// Everything is under HKCU, so no administrator rights are needed, and <see cref="Unregister"/> takes it all away again.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShellRegistration
{
	public const string AppName = "WinNewsReader";

	public const string ThemeExtension = ".nnwtheme";

	/// <summary>The link schemes the app opens: the two a page offers its feed with, and the app's own.</summary>
	public static readonly string[] UrlSchemes = ["feed", "feeds", "winnewsreader"];

	private const string UrlProgId = "WinNewsReader.Url";
	private const string ThemeProgId = "WinNewsReader.Theme";
	private const string CapabilitiesPath = @"WinNewsReader\Capabilities";

	/// <summary>
	/// Registers <paramref name="exePath"/> under <paramref name="software"/>, which stands for <c>HKCU\Software</c> (tests give
	/// it a key of their own). A scheme or extension that another program has claimed is taken over, as an installer does.
	/// </summary>
	public static void Register(RegistryKey software, string exePath)
	{
		var command = $"\"{exePath}\" \"%1\"";
		foreach (var scheme in UrlSchemes)
		{
			WriteProgId(software, @"Classes\" + scheme, $"URL:{scheme} Protocol", exePath, command, urlProtocol: true);
		}
		WriteProgId(software, @"Classes\" + UrlProgId, "WinNewsReader feed link", exePath, command, urlProtocol: false);
		WriteProgId(software, @"Classes\" + ThemeProgId, "NetNewsWire article theme", exePath, command, urlProtocol: false);
		Set(software, @"Classes\" + ThemeExtension, null, ThemeProgId);

		// The Default apps list reads these.
		Set(software, CapabilitiesPath, "ApplicationName", AppName);
		Set(software, CapabilitiesPath, "ApplicationDescription", "A feed reader for Windows, in the spirit of NetNewsWire.");
		foreach (var scheme in UrlSchemes)
		{
			Set(software, CapabilitiesPath + @"\URLAssociations", scheme, UrlProgId);
		}
		Set(software, CapabilitiesPath + @"\FileAssociations", ThemeExtension, ThemeProgId);
		Set(software, "RegisteredApplications", AppName, @"Software\" + CapabilitiesPath);
	}

	/// <summary>Removes what <see cref="Register"/> wrote, leaving a scheme or extension alone if something else owns it now.</summary>
	public static void Unregister(RegistryKey software)
	{
		foreach (var scheme in UrlSchemes)
		{
			DeleteIfOurs(software, @"Classes\" + scheme);
		}
		software.DeleteSubKeyTree(@"Classes\" + UrlProgId, throwOnMissingSubKey: false);
		software.DeleteSubKeyTree(@"Classes\" + ThemeProgId, throwOnMissingSubKey: false);
		using (var extension = software.OpenSubKey(@"Classes\" + ThemeExtension, writable: true))
		{
			if (extension?.GetValue(null) as string == ThemeProgId)
			{
				software.DeleteSubKeyTree(@"Classes\" + ThemeExtension, throwOnMissingSubKey: false);
			}
		}
		software.DeleteSubKeyTree("WinNewsReader", throwOnMissingSubKey: false);
		using var registered = software.OpenSubKey("RegisteredApplications", writable: true);
		registered?.DeleteValue(AppName, throwOnMissingValue: false);
	}

	private static void WriteProgId(RegistryKey software, string path, string description, string exePath, string command, bool urlProtocol)
	{
		Set(software, path, null, description);
		if (urlProtocol)
		{
			Set(software, path, "URL Protocol", "");
		}
		Set(software, path + @"\DefaultIcon", null, $"\"{exePath}\",0");
		Set(software, path + @"\shell\open\command", null, command);
	}

	private static void DeleteIfOurs(RegistryKey software, string path)
	{
		using var command = software.OpenSubKey(path + @"\shell\open\command");
		if (command?.GetValue(null) is string value && value.Contains("WinNewsReader.exe", StringComparison.OrdinalIgnoreCase))
		{
			software.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
		}
	}

	private static void Set(RegistryKey software, string path, string? name, string value)
	{
		using var key = software.CreateSubKey(path);
		key.SetValue(name, value);
	}
}
