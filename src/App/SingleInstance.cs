using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinNewsReader.App;

/// <summary>
/// One running copy per data folder. The first to start listens on a named pipe; a later launch connects, sends what it was
/// asked to open (a <c>feed:</c> link or a theme file), and exits, so double-clicking a feed link in a browser reaches the
/// window already open instead of starting a second copy.
/// </summary>
public static class SingleInstance
{
	private static Mutex? _mutex;

	/// <summary>Raised on a background thread with the arguments of a later launch.</summary>
	public static event Action<IReadOnlyList<string>>? Launched;

	private static string NameFor(string dataDirectory)
	{
		var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory).ToLowerInvariant())))[..16];
		return "WinNewsReader-" + hash;
	}

	/// <summary>True if this is the first copy and should carry on; false if another has it, and has been told what to open.</summary>
	public static bool TryBecomeFirst(string dataDirectory, IReadOnlyList<string> targets)
	{
		var name = NameFor(dataDirectory);
		_mutex = new Mutex(initiallyOwned: true, @"Local\" + name, out var first);
		if (first)
		{
			_ = Task.Run(() => ListenAsync(name));
			return true;
		}
		Send(name, targets);
		return false;
	}

	private static void Send(string name, IReadOnlyList<string> targets)
	{
		try
		{
			using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
			client.Connect(3000);
			using var writer = new StreamWriter(client, Encoding.UTF8);
			writer.Write(JsonSerializer.Serialize(targets));
		}
		catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
		{
			// The first copy may be shutting down; there's nothing more to do.
		}
	}

	private static async Task ListenAsync(string name)
	{
		while (true)
		{
			try
			{
				await using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
				await server.WaitForConnectionAsync();
				using var reader = new StreamReader(server, Encoding.UTF8);
				var targets = JsonSerializer.Deserialize<List<string>>(await reader.ReadToEndAsync()) ?? [];
				Launched?.Invoke(targets);
			}
			catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
			{
				// A connection that broke off or sent nonsense is ignored; the next launch can still get through.
			}
		}
	}
}
