using WinNewsReader.Core;
using WinNewsReader.Parser.Xml;
using WinNewsReader.Store;

// Headless driver for the core engine (Phase 1 exit test): subscribe, refresh, list unread.

const string Usage = """
	Usage: wnr [--db <path>] <command> [arguments]

	Commands:
	  subscribe <url> [--folder <name>]
	                         Subscribe to a feed, or find the feed for a web page
	  unsubscribe <url>      Remove a feed and its articles
	  import <file.opml>     Add every feed and folder in an OPML file (run refresh afterwards)
	  export [<file.opml>]   Write subscriptions as OPML, to standard output by default
	  feeds                  List subscriptions by folder, with unread counts
	  refresh                Fetch every feed
	  unread [<feed url>]    List unread articles, newest first
	  show <article id>      Print an article
	  read <article id>...   Mark articles read
	  read --all [<feed>]    Mark all articles read

	Article IDs can be shortened to any unique prefix.
	The default database is %LOCALAPPDATA%\WinNewsReader\OnThisPC\articles.db.
	""";

var arguments = args.ToList();
var databasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinNewsReader", "OnThisPC", "articles.db");
var dbIndex = arguments.IndexOf("--db");
if (dbIndex >= 0)
{
	if (dbIndex + 1 >= arguments.Count)
	{
		return Fail("--db needs a path.");
	}
	databasePath = arguments[dbIndex + 1];
	arguments.RemoveRange(dbIndex, 2);
}
if (arguments.Count == 0 || arguments[0] is "-h" or "--help" or "help")
{
	Console.WriteLine(Usage);
	return arguments.Count == 0 ? 1 : 0;
}

using var store = new ArticlesStore(databasePath);
using var downloader = new FeedDownloader();
var account = new LocalAccount(store, downloader);
var command = arguments[0];
var rest = arguments.Skip(1).ToList();

try
{
	switch (command)
	{
		case "subscribe" when rest.Count == 1 || (rest.Count == 3 && rest[1] == "--folder"):
			var (feed, newArticles) = await account.SubscribeAsync(rest[0], rest.ElementAtOrDefault(2));
			Console.WriteLine($"Subscribed to {feed.Name ?? feed.Url} ({newArticles} articles).");
			return 0;

		case "unsubscribe" when rest.Count == 1:
			return store.RemoveFeed(rest[0]) ? 0 : Fail($"Not subscribed to {rest[0]}.");

		case "import" when rest.Count == 1:
			var imported = account.ImportOpml(rest[0]);
			Console.WriteLine($"Added {imported.FeedsAdded} feeds and {imported.FoldersAdded} folders.");
			return 0;

		case "export" when rest.Count <= 1:
			if (rest.Count == 0)
			{
				Console.OutputEncoding = System.Text.Encoding.UTF8;
				Console.WriteLine(OpmlExporter.OpmlString(store, "Subscriptions-On This PC"));
			}
			else
			{
				File.WriteAllText(rest[0], OpmlExporter.OpmlString(store, Path.GetFileNameWithoutExtension(rest[0])));
			}
			return 0;

		case "feeds" when rest.Count == 0:
			foreach (var f in store.GetTopLevelFeeds())
			{
				PrintFeed(f, "");
			}
			foreach (var folder in store.GetFolders())
			{
				Console.WriteLine($"       {folder.Name}/");
				foreach (var f in store.GetFeeds(folder.FolderId))
				{
					PrintFeed(f, "  ");
				}
			}
			return 0;

			void PrintFeed(StoredFeed f, string indent) =>
				Console.WriteLine($"{store.UnreadCount(f.FeedId),5}  {indent}{f.Name ?? "(untitled)"}  {f.Url}");

		case "refresh" when rest.Count == 0:
			var results = await account.RefreshAllAsync();
			foreach (var failure in results.Where(r => r.Status == DownloadStatus.Failed))
			{
				Console.Error.WriteLine($"Failed: {failure.FeedId}: {failure.Error}");
			}
			Console.WriteLine(
				$"Refreshed {results.Count} feeds: {results.Sum(r => r.NewArticles)} new articles, " +
				$"{results.Count(r => r.Status == DownloadStatus.NotModified)} not modified, " +
				$"{results.Count(r => r.Status == DownloadStatus.Failed)} failed.");
			return results.Any(r => r.Status == DownloadStatus.Failed) ? 2 : 0;

		case "unread" when rest.Count <= 1:
			var feedNames = store.GetFeeds().ToDictionary(f => f.FeedId, f => f.Name ?? f.Url);
			foreach (var article in store.GetUnreadArticles(rest.FirstOrDefault()))
			{
				var date = article.LogicalDatePublished.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
				Console.WriteLine($"{article.ArticleId[..8]}  {date}  {feedNames.GetValueOrDefault(article.FeedId)}  {article.Title?.DecodingHtmlEntities() ?? "(untitled)"}");
			}
			return 0;

		case "show" when rest.Count == 1:
			if (ResolveArticleId(store, rest[0]) is not { } showId)
			{
				return 1;
			}
			var shown = store.GetArticle(showId)!;
			Console.WriteLine(shown.Title?.DecodingHtmlEntities() ?? "(untitled)");
			Console.WriteLine(shown.Url ?? shown.ExternalUrl);
			Console.WriteLine();
			Console.WriteLine(shown.ContentText ?? shown.ContentHtml ?? shown.Summary);
			return 0;

		case "read" when rest.Count >= 1 && rest[0] == "--all":
			Console.WriteLine($"Marked {store.MarkAllRead(rest.ElementAtOrDefault(1))} articles read.");
			return 0;

		case "read" when rest.Count >= 1:
			var ids = rest.Select(prefix => ResolveArticleId(store, prefix)).ToList();
			if (ids.Contains(null))
			{
				return 1;
			}
			Console.WriteLine($"Marked {store.MarkRead(ids!)} articles read.");
			return 0;

		default:
			Console.Error.WriteLine(Usage);
			return 1;
	}
}
catch (Exception ex) when (ex is SubscribeException or WinNewsReader.Parser.Opml.OpmlException or IOException)
{
	return Fail(ex.Message);
}

static string? ResolveArticleId(ArticlesStore store, string prefix)
{
	var matches = store.FindArticleIds(prefix);
	if (matches.Count == 1)
	{
		return matches[0];
	}
	Console.Error.WriteLine(matches.Count == 0 ? $"No article {prefix}." : $"Article ID {prefix} is ambiguous.");
	return null;
}

static int Fail(string message)
{
	Console.Error.WriteLine(message);
	return 1;
}
