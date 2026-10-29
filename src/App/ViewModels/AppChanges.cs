namespace WinNewsReader.App.ViewModels;

public enum AppChangeKind
{
	/// <summary>Articles were read, starred or refreshed: counts and timelines may be out of date.</summary>
	Articles,

	/// <summary>Feeds or folders came or went, or were renamed or moved: the sidebar may be out of date.</summary>
	Structure,

	/// <summary>The reader changed the app's Light or Dark appearance.</summary>
	Appearance,
}

/// <summary>
/// Tells every main window that another one changed something they all show. Each window has its own view model, with its
/// own selection, over the same accounts, so what one does to the articles or the feed list has to reach the others.
/// </summary>
public static class AppChanges
{
	public static event Action<object, AppChangeKind>? Changed;

	public static void Raise(object sender, AppChangeKind kind) => Changed?.Invoke(sender, kind);
}
