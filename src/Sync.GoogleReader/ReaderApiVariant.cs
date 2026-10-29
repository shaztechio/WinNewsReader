using WinNewsReader.Core;

namespace WinNewsReader.Sync.GoogleReader;

/// <summary>
/// The services that speak the Google Reader API, as in NetNewsWire's <c>ReaderAPIVariant</c>. FreshRSS is at an address you
/// give; The Old Reader and BazQux each have one fixed address.
/// </summary>
public enum ReaderApiVariant
{
	FreshRss,
	TheOldReader,
	BazQux,
}

public static class ReaderApiVariants
{
	/// <summary>The fixed address of a hosted service, or null for FreshRSS, which is wherever you put it.</summary>
	public static string? Host(ReaderApiVariant variant) => variant switch
	{
		ReaderApiVariant.TheOldReader => "https://theoldreader.com",
		ReaderApiVariant.BazQux => "https://bazqux.com",
		_ => null,
	};

	public static ReaderApiVariant For(AccountKind kind) => kind switch
	{
		AccountKind.TheOldReader => ReaderApiVariant.TheOldReader,
		AccountKind.BazQux => ReaderApiVariant.BazQux,
		_ => ReaderApiVariant.FreshRss,
	};

	public static bool IsReaderApi(AccountKind kind) => kind is AccountKind.FreshRss or AccountKind.TheOldReader or AccountKind.BazQux;
}
