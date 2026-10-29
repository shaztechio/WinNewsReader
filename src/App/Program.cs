using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;
using WinNewsReader.Core;

namespace WinNewsReader.App;

/// <summary>
/// The app's entry point, in place of the one WinUI generates, so two things can happen before any window: Velopack's
/// install, update and uninstall hooks, and handing a second launch (a <c>feed:</c> link, a theme file) to the app already running.
/// </summary>
public static class Program
{
	[STAThread]
	public static int Main(string[] args)
	{
		VelopackApp.Build()
			.OnAfterInstallFastCallback(_ => ShellRegistrationFor(register: true))
			.OnAfterUpdateFastCallback(_ => ShellRegistrationFor(register: true))
			.OnBeforeUninstallFastCallback(_ => ShellRegistrationFor(register: false))
			.Run();

		WinRT.ComWrappersSupport.InitializeComWrappers();
		// Only one copy runs for a data folder; a second launch passes its arguments to the first and ends.
		var launch = LaunchArguments.From(args);
		if (!SingleInstance.TryBecomeFirst(App.DataDirectory, launch.Targets))
		{
			return 0;
		}
		Application.Start(callback =>
		{
			SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
			new App(launch.Targets);
		});
		return 0;
	}

	/// <summary>Writes or removes the registry entries for feed links and theme files, as Velopack runs the app during install and uninstall.</summary>
	private static void ShellRegistrationFor(bool register)
	{
		if (Environment.ProcessPath is not { } exe)
		{
			return;
		}
		using var software = Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Software");
		if (register)
		{
			ShellRegistration.Register(software, exe);
		}
		else
		{
			ShellRegistration.Unregister(software);
		}
	}
}

/// <summary>What a launch asks for besides starting: the feed links and theme files in its arguments.</summary>
public sealed record LaunchArguments(IReadOnlyList<string> Targets)
{
	/// <summary>The arguments that aren't the app's own <c>--data</c> option and its folder.</summary>
	public static LaunchArguments From(IEnumerable<string> args)
	{
		var targets = new List<string>();
		var skipNext = false;
		foreach (var arg in args)
		{
			if (skipNext)
			{
				skipNext = false;
			}
			else if (arg == "--data")
			{
				skipNext = true;
			}
			else if (!arg.StartsWith("--", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(arg))
			{
				targets.Add(arg);
			}
		}
		return new LaunchArguments(targets);
	}
}
