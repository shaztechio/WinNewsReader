using WinNewsReader.Parser.Feeds;
using WinNewsReader.Parser.Opml;

namespace WinNewsReader.Core;

/// <summary>A feed in an OPML file, with the folder it sits in, if any.</summary>
public sealed record OpmlFeed(string Url, string? Title, string? HomePageUrl, string? FolderName);

/// <summary>How an import into a sync account went: the feeds subscribed to, and the ones that couldn't be.</summary>
public sealed record OpmlSubscribeResult(int Subscribed, IReadOnlyList<string> Failed);

/// <summary>
/// Importing an OPML file into an account that has to subscribe on its server, as NetNewsWire's import does for its sync
/// accounts. The local account adds feeds straight to its store instead.
/// </summary>
public static class OpmlImporter
{
	/// <summary>
	/// The feeds in the file, in order, each with its folder. Folders are one level deep, so a nested folder becomes a
	/// folder of its own, and the items of a folder with no title go to the folder above, as in NetNewsWire.
	/// </summary>
	public static IReadOnlyList<OpmlFeed> Feeds(IEnumerable<OpmlItem> items)
	{
		var feeds = new List<OpmlFeed>();
		Collect(items, null, feeds);
		return feeds;
	}

	private static void Collect(IEnumerable<OpmlItem> items, string? folderName, List<OpmlFeed> feeds)
	{
		foreach (var item in items)
		{
			if (item.FeedSpecifier is { } specifier)
			{
				feeds.Add(new OpmlFeed(specifier.FeedUrl, specifier.Title, specifier.HomePageUrl, folderName));
			}
			else if (item.TitleFromAttributes is { } name)
			{
				Collect(item.Children ?? [], name, feeds);
			}
			else
			{
				Collect(item.Children ?? [], folderName, feeds);
			}
		}
	}

	public static IReadOnlyList<OpmlFeed> FeedsIn(string path) =>
		Feeds(OpmlParser.Parse(new ParserData(new Uri(Path.GetFullPath(path)).AbsoluteUri, File.ReadAllBytes(path))).Children ?? []);

	/// <summary>Subscribes <paramref name="account"/> to each feed, one after another, reporting how far it has got.</summary>
	public static async Task<OpmlSubscribeResult> SubscribeAsync(IAccount account, IReadOnlyList<OpmlFeed> feeds, Action<int, int>? progress = null, CancellationToken cancellationToken = default)
	{
		var subscribed = 0;
		var failed = new List<string>();
		for (var i = 0; i < feeds.Count; i++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			progress?.Invoke(i + 1, feeds.Count);
			try
			{
				await account.SubscribeAsync(feeds[i].Url, feeds[i].FolderName, cancellationToken);
				subscribed++;
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// One feed that can't be added shouldn't stop the rest.
				failed.Add(feeds[i].Title is { Length: > 0 } title ? title : feeds[i].Url);
			}
		}
		return new OpmlSubscribeResult(subscribed, failed);
	}
}
