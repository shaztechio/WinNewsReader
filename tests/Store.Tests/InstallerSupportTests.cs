using System.Runtime.Versioning;
using Microsoft.Win32;
using WinNewsReader.Core;

namespace WinNewsReader.Store.Tests;

/// <summary>What the installer relies on: registry entries for feed links and themes, and local crash reports.</summary>
[SupportedOSPlatform("windows")]
public sealed class InstallerSupportTests : IDisposable
{
	private const string Exe = @"C:\Apps\WinNewsReader\WinNewsReader.exe";

	private readonly string _keyPath = @"Software\WinNewsReaderTests\" + Guid.NewGuid().ToString("N");
	private readonly RegistryKey _software;
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "wnr-tests-" + Guid.NewGuid().ToString("N"));

	public InstallerSupportTests() => _software = Registry.CurrentUser.CreateSubKey(_keyPath);

	public void Dispose()
	{
		_software.Dispose();
		Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
		if (Directory.Exists(_directory))
		{
			Directory.Delete(_directory, recursive: true);
		}
	}

	private string? Value(string path, string? name = null)
	{
		using var key = _software.OpenSubKey(path);
		return key?.GetValue(name) as string;
	}

	[Theory]
	[InlineData("feed")]
	[InlineData("feeds")]
	[InlineData("winnewsreader")]
	public void RegisterClaimsEachLinkScheme(string scheme)
	{
		ShellRegistration.Register(_software, Exe);

		Assert.Equal("\"" + Exe + "\" \"%1\"", Value(@"Classes\" + scheme + @"\shell\open\command"));
		Assert.Equal("", Value(@"Classes\" + scheme, "URL Protocol"));
		Assert.Equal("WinNewsReader.Url", Value(@"WinNewsReader\Capabilities\URLAssociations", scheme));
	}

	[Fact]
	public void RegisterClaimsThemeFilesAndListsTheAppUnderDefaultApps()
	{
		ShellRegistration.Register(_software, Exe);

		Assert.Equal("WinNewsReader.Theme", Value(@"Classes\.nnwtheme"));
		Assert.Equal("WinNewsReader.Theme", Value(@"WinNewsReader\Capabilities\FileAssociations", ".nnwtheme"));
		Assert.Equal(@"Software\WinNewsReader\Capabilities", Value("RegisteredApplications", "WinNewsReader"));
		Assert.Equal("WinNewsReader", Value(@"WinNewsReader\Capabilities", "ApplicationName"));
	}

	[Fact]
	public void RegisteringAgainAfterAnUpdateMovesTheCommandToTheNewFolder()
	{
		ShellRegistration.Register(_software, @"C:\Old\WinNewsReader.exe");
		ShellRegistration.Register(_software, Exe);

		Assert.Equal("\"" + Exe + "\" \"%1\"", Value(@"Classes\feed\shell\open\command"));
	}

	[Fact]
	public void UnregisterRemovesEverythingItWrote()
	{
		ShellRegistration.Register(_software, Exe);

		ShellRegistration.Unregister(_software);

		Assert.Null(Value(@"Classes\feed\shell\open\command"));
		Assert.Null(Value(@"Classes\.nnwtheme"));
		Assert.Null(Value(@"WinNewsReader\Capabilities", "ApplicationName"));
		Assert.Null(Value("RegisteredApplications", "WinNewsReader"));
	}

	[Fact]
	public void UnregisterLeavesASchemeAnotherProgramHasTakenOver()
	{
		ShellRegistration.Register(_software, Exe);
		using (var key = _software.CreateSubKey(@"Classes\feed\shell\open\command"))
		{
			key.SetValue(null, "\"C:\\Other\\Reader.exe\" \"%1\"");
		}

		ShellRegistration.Unregister(_software);

		Assert.Equal("\"C:\\Other\\Reader.exe\" \"%1\"", Value(@"Classes\feed\shell\open\command"));
		Assert.Null(Value(@"Classes\feeds\shell\open\command"));
	}

	[Fact]
	public void ACrashReportHasTheVersionAndTheExceptionAndIsPendingUntilDismissed()
	{
		var path = CrashLog.Write(_directory, new InvalidOperationException("boom"), "0.1.0", new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));

		var text = File.ReadAllText(path);
		Assert.Contains("WinNewsReader 0.1.0", text);
		Assert.Contains("InvalidOperationException: boom", text);
		Assert.Equal([path], CrashLog.Pending(_directory));

		CrashLog.Dismiss(path);

		Assert.Empty(CrashLog.Pending(_directory));
		Assert.True(File.Exists(Path.Combine(_directory, "seen", Path.GetFileName(path))));
	}

	[Fact]
	public void NoCrashFolderMeansNothingPending() => Assert.Empty(CrashLog.Pending(_directory));
}
