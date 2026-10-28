using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace WinNewsReader.Core;

/// <summary>Where account passwords live, so they never sit in plain files: Windows Credential Manager in the app.</summary>
public interface ICredentialStore
{
	string? GetSecret(string target);

	void SetSecret(string target, string userName, string secret);

	void Delete(string target);
}

/// <summary>Windows Credential Manager, NetNewsWire's Keychain on Windows. Entries are generic credentials for this user.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ICredentialStore
{
	private const uint CredTypeGeneric = 1;
	private const uint CredPersistLocalMachine = 2;
	private const int ErrorNotFound = 1168;

	public string? GetSecret(string target)
	{
		if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
		{
			return Marshal.GetLastWin32Error() == ErrorNotFound ? null : throw new IOException($"Couldn’t read credential {target}.", Marshal.GetHRForLastWin32Error());
		}
		try
		{
			var credential = Marshal.PtrToStructure<Credential>(pointer);
			if (credential.CredentialBlobSize == 0)
			{
				return "";
			}
			var bytes = new byte[credential.CredentialBlobSize];
			Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
			return Encoding.Unicode.GetString(bytes);
		}
		finally
		{
			CredFree(pointer);
		}
	}

	public void SetSecret(string target, string userName, string secret)
	{
		var bytes = Encoding.Unicode.GetBytes(secret);
		var blob = Marshal.AllocHGlobal(bytes.Length);
		try
		{
			Marshal.Copy(bytes, 0, blob, bytes.Length);
			var credential = new Credential
			{
				Type = CredTypeGeneric,
				TargetName = target,
				UserName = userName,
				CredentialBlob = blob,
				CredentialBlobSize = (uint)bytes.Length,
				Persist = CredPersistLocalMachine,
			};
			if (!CredWrite(ref credential, 0))
			{
				throw new IOException($"Couldn’t save credential {target}.", Marshal.GetHRForLastWin32Error());
			}
		}
		finally
		{
			Marshal.FreeHGlobal(blob);
		}
	}

	public void Delete(string target)
	{
		if (!CredDelete(target, CredTypeGeneric, 0) && Marshal.GetLastWin32Error() != ErrorNotFound)
		{
			throw new IOException($"Couldn’t delete credential {target}.", Marshal.GetHRForLastWin32Error());
		}
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct Credential
	{
		public uint Flags;
		public uint Type;
		public string TargetName;
		public string? Comment;
		public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
		public uint CredentialBlobSize;
		public IntPtr CredentialBlob;
		public uint Persist;
		public uint AttributeCount;
		public IntPtr Attributes;
		public string? TargetAlias;
		public string? UserName;
	}

	[DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

	[DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CredWrite(ref Credential credential, uint flags);

	[DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CredDelete(string target, uint type, uint flags);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern void CredFree(IntPtr buffer);
}
