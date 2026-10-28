using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinNewsReader.Parser.Feeds;
using WinNewsReader.Parser.Utilities;

namespace WinNewsReader.Store;

public sealed record StoredFeed(string FeedId, string Url, string? Name, string? HomePageUrl, string? IconUrl, string? ETag, string? LastModified);

/// <summary>A folder. As in NetNewsWire, folders are one level deep and hold only feeds.</summary>
public sealed record StoredFolder(long FolderId, string Name);

public sealed record StoredArticle(
	string ArticleId,
	string FeedId,
	string UniqueId,
	string? Title,
	string? Url,
	string? ExternalUrl,
	string? ContentHtml,
	string? ContentText,
	string? Summary,
	DateTimeOffset? DatePublished,
	DateTimeOffset? DateModified,
	DateTimeOffset DateArrived,
	bool Read,
	bool Starred);

/// <summary>
/// SQLite store for one account's feeds, folders, articles and statuses. The articles and statuses tables follow
/// NetNewsWire's ArticlesDatabase schema, and article IDs use its formula, so data maps across later.
/// As in NetNewsWire, a feed can be at the top level and in any number of folders at once, and stays
/// subscribed while it's in at least one of them.
/// Safe to use from several threads: each public call holds a lock for its whole transaction.
/// </summary>
public sealed class ArticlesStore : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly Lock _lock = new();

	public ArticlesStore(string databasePath)
	{
		var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}
		_connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
		_connection.Open();
		Execute("""
			PRAGMA journal_mode = WAL;
			PRAGMA foreign_keys = ON;
			CREATE TABLE IF NOT EXISTS feeds (feedID TEXT NOT NULL PRIMARY KEY, url TEXT NOT NULL, name TEXT, homePageURL TEXT, iconURL TEXT, etag TEXT, lastModified TEXT, dateAdded DATE NOT NULL, topLevel BOOL NOT NULL DEFAULT 1);
			CREATE TABLE IF NOT EXISTS folders (folderID INTEGER PRIMARY KEY, name TEXT NOT NULL);
			CREATE TABLE IF NOT EXISTS folderFeeds (folderID INTEGER NOT NULL REFERENCES folders ON DELETE CASCADE, feedID TEXT NOT NULL REFERENCES feeds ON DELETE CASCADE, PRIMARY KEY (folderID, feedID));
			CREATE TABLE IF NOT EXISTS articles (articleID TEXT NOT NULL PRIMARY KEY, feedID TEXT NOT NULL, uniqueID TEXT NOT NULL, title TEXT, contentHTML TEXT, contentText TEXT, markdown TEXT, url TEXT, externalURL TEXT, summary TEXT, imageURL TEXT, bannerImageURL TEXT, datePublished DATE, dateModified DATE, authors TEXT, attachments TEXT, thumbnailURL TEXT);
			CREATE TABLE IF NOT EXISTS statuses (articleID TEXT NOT NULL PRIMARY KEY, read BOOL NOT NULL DEFAULT 0, starred BOOL NOT NULL DEFAULT 0, dateArrived DATE NOT NULL DEFAULT 0);
			CREATE INDEX IF NOT EXISTS articles_feedID_datePublished_articleID ON articles (feedID, datePublished, articleID);
			CREATE INDEX IF NOT EXISTS statuses_starred_index ON statuses (starred);
			CREATE INDEX IF NOT EXISTS folderFeeds_feedID ON folderFeeds (feedID);
			""");
		// Databases from before folders: every feed was at the top level.
		using var hasTopLevel = Command("SELECT COUNT(*) FROM pragma_table_info('feeds') WHERE name = 'topLevel'");
		if (Convert.ToInt32(hasTopLevel.ExecuteScalar()) == 0)
		{
			Execute("ALTER TABLE feeds ADD COLUMN topLevel BOOL NOT NULL DEFAULT 1");
		}
	}

	/// <summary>NetNewsWire's <c>Article.calculatedArticleID</c>.</summary>
	public static string CalculatedArticleId(string feedId, string uniqueId) => SwiftCompat.Md5String($"{feedId} {uniqueId}");

	/// <summary>
	/// Adds the feed if it isn't there, and puts it at the top level or in <paramref name="folderId"/>. An existing
	/// feed keeps its name and stays wherever else it already is. For the local account the feed ID is its URL.
	/// </summary>
	public StoredFeed AddFeed(string url, string? name = null, string? homePageUrl = null, long? folderId = null)
	{
		lock (_lock)
		{
			using var transaction = _connection.BeginTransaction();
			Execute(
				"INSERT OR IGNORE INTO feeds (feedID, url, name, homePageURL, dateAdded, topLevel) VALUES ($id, $url, $name, $home, $now, $topLevel)",
				("$id", url), ("$url", url), ("$name", name), ("$home", homePageUrl), ("$now", Now()), ("$topLevel", folderId is null));
			if (folderId is null)
			{
				Execute("UPDATE feeds SET topLevel = 1 WHERE feedID = $id", ("$id", url));
			}
			else
			{
				Execute("INSERT OR IGNORE INTO folderFeeds (folderID, feedID) VALUES ($folder, $id)", ("$folder", folderId), ("$id", url));
			}
			transaction.Commit();
			return QueryFeeds("WHERE feedID = $id", ("$id", url))[0];
		}
	}

	/// <summary>Removes the feed from everywhere, with its articles and statuses. Returns false if it wasn't subscribed.</summary>
	public bool RemoveFeed(string feedId)
	{
		lock (_lock)
		{
			using var transaction = _connection.BeginTransaction();
			var removed = DeleteFeedsWhere("feedID = $id", ("$id", feedId)) > 0;
			transaction.Commit();
			return removed;
		}
	}

	/// <summary>
	/// Takes the feed out of the top level (null <paramref name="folderId"/>) or out of one folder. A feed left
	/// nowhere is removed with its articles. Returns false if the feed wasn't there.
	/// </summary>
	public bool RemoveFeed(string feedId, long? folderId)
	{
		lock (_lock)
		{
			using var transaction = _connection.BeginTransaction();
			var removed = folderId is null
				? Execute("UPDATE feeds SET topLevel = 0 WHERE feedID = $id AND topLevel = 1", ("$id", feedId)) > 0
				: Execute("DELETE FROM folderFeeds WHERE folderID = $folder AND feedID = $id", ("$folder", folderId), ("$id", feedId)) > 0;
			DeleteUnparentedFeeds();
			transaction.Commit();
			return removed;
		}
	}

	public StoredFeed? GetFeed(string feedId)
	{
		lock (_lock)
		{
			return QueryFeeds("WHERE feedID = $id", ("$id", feedId)).FirstOrDefault();
		}
	}

	public IReadOnlyList<StoredFeed> GetFeeds()
	{
		lock (_lock)
		{
			return QueryFeeds("ORDER BY COALESCE(name, url) COLLATE NOCASE");
		}
	}

	public IReadOnlyList<StoredFeed> GetTopLevelFeeds()
	{
		lock (_lock)
		{
			return QueryFeeds("WHERE topLevel = 1 ORDER BY COALESCE(name, url) COLLATE NOCASE");
		}
	}

	public IReadOnlyList<StoredFeed> GetFeeds(long folderId)
	{
		lock (_lock)
		{
			return QueryFeeds(
				"WHERE feedID IN (SELECT feedID FROM folderFeeds WHERE folderID = $folder) ORDER BY COALESCE(name, url) COLLATE NOCASE",
				("$folder", folderId));
		}
	}

	/// <summary>The folder named <paramref name="name"/>, created if needed: NetNewsWire's <c>ensureFolder(with:)</c>.</summary>
	public StoredFolder EnsureFolder(string name)
	{
		lock (_lock)
		{
			if (QueryFolders("WHERE name = $name", ("$name", name)).FirstOrDefault() is { } existing)
			{
				return existing;
			}
			using var command = Command("INSERT INTO folders (name) VALUES ($name) RETURNING folderID", ("$name", name));
			return new StoredFolder(Convert.ToInt64(command.ExecuteScalar()), name);
		}
	}

	public IReadOnlyList<StoredFolder> GetFolders()
	{
		lock (_lock)
		{
			return QueryFolders("ORDER BY name COLLATE NOCASE");
		}
	}

	public bool RenameFolder(long folderId, string name)
	{
		lock (_lock)
		{
			return Execute("UPDATE folders SET name = $name WHERE folderID = $folder", ("$folder", folderId), ("$name", name)) > 0;
		}
	}

	/// <summary>
	/// Removes the folder. Its feeds go too, with their articles, unless they're also at the top level or in another
	/// folder. Returns false if there was no such folder.
	/// </summary>
	public bool RemoveFolder(long folderId)
	{
		lock (_lock)
		{
			using var transaction = _connection.BeginTransaction();
			var removed = Execute("DELETE FROM folders WHERE folderID = $folder", ("$folder", folderId)) > 0;
			DeleteUnparentedFeeds();
			transaction.Commit();
			return removed;
		}
	}

	/// <summary>Saves what a successful fetch learned. Null name or home page keeps the stored value.</summary>
	public void UpdateFeedMetadata(string feedId, string? name, string? homePageUrl, string? iconUrl, string? etag, string? lastModified)
	{
		lock (_lock)
		{
			Execute(
				"""
				UPDATE feeds SET name = COALESCE($name, name), homePageURL = COALESCE($home, homePageURL), iconURL = COALESCE($icon, iconURL),
					etag = $etag, lastModified = $lastModified WHERE feedID = $id
				""",
				("$id", feedId), ("$name", name), ("$home", homePageUrl), ("$icon", iconUrl), ("$etag", etag), ("$lastModified", lastModified));
		}
	}

	/// <summary>
	/// Inserts new articles as unread and updates changed ones. Articles missing from the feed are kept.
	/// Returns the number of new articles.
	/// </summary>
	public int UpsertArticles(string feedId, IEnumerable<ParsedItem> items)
	{
		lock (_lock)
		{
			using var transaction = _connection.BeginTransaction();
			var now = Now();
			var newCount = 0;
			foreach (var item in items)
			{
				var articleId = CalculatedArticleId(feedId, item.UniqueId);
				Execute(
					"""
					INSERT INTO articles (articleID, feedID, uniqueID, title, contentHTML, contentText, markdown, url, externalURL, summary, imageURL, bannerImageURL, datePublished, dateModified, authors, attachments, thumbnailURL)
					VALUES ($articleID, $feedID, $uniqueID, $title, $contentHTML, $contentText, $markdown, $url, $externalURL, $summary, $imageURL, $bannerImageURL, $datePublished, $dateModified, $authors, $attachments, $thumbnailURL)
					ON CONFLICT(articleID) DO UPDATE SET title = excluded.title, contentHTML = excluded.contentHTML, contentText = excluded.contentText,
						markdown = excluded.markdown, url = excluded.url, externalURL = excluded.externalURL, summary = excluded.summary,
						imageURL = excluded.imageURL, bannerImageURL = excluded.bannerImageURL, datePublished = excluded.datePublished,
						dateModified = excluded.dateModified, authors = excluded.authors, attachments = excluded.attachments, thumbnailURL = excluded.thumbnailURL
					""",
					("$articleID", articleId), ("$feedID", feedId), ("$uniqueID", item.UniqueId), ("$title", item.Title),
					("$contentHTML", item.ContentHtml), ("$contentText", item.ContentText), ("$markdown", item.Markdown),
					("$url", item.Url), ("$externalURL", item.ExternalUrl), ("$summary", item.Summary), ("$imageURL", item.ImageUrl),
					("$bannerImageURL", item.BannerImageUrl), ("$datePublished", ToDb(item.DatePublished)), ("$dateModified", ToDb(item.DateModified)),
					("$authors", item.Authors is null ? null : JsonSerializer.Serialize(item.Authors)),
					("$attachments", item.Attachments is null ? null : JsonSerializer.Serialize(item.Attachments)),
					("$thumbnailURL", item.ThumbnailUrl));
				newCount += Execute("INSERT OR IGNORE INTO statuses (articleID, read, starred, dateArrived) VALUES ($id, 0, 0, $now)", ("$id", articleId), ("$now", now));
			}
			transaction.Commit();
			return newCount;
		}
	}

	/// <summary>Unread articles, newest first, optionally for one feed.</summary>
	public IReadOnlyList<StoredArticle> GetUnreadArticles(string? feedId = null)
	{
		lock (_lock)
		{
			return QueryArticles("WHERE s.read = 0" + (feedId is null ? "" : " AND a.feedID = $feedID"), ("$feedID", feedId));
		}
	}

	public IReadOnlyList<StoredArticle> GetArticles(string feedId)
	{
		lock (_lock)
		{
			return QueryArticles("WHERE a.feedID = $feedID", ("$feedID", feedId));
		}
	}

	/// <summary>Articles in any of <paramref name="feedIds"/>, newest first.</summary>
	public IReadOnlyList<StoredArticle> GetArticles(IEnumerable<string> feedIds)
	{
		lock (_lock)
		{
			return QueryArticles("WHERE a.feedID IN (SELECT value FROM json_each($feedIDs))", ("$feedIDs", JsonSerializer.Serialize(feedIds)));
		}
	}

	public IReadOnlyList<StoredArticle> GetStarredArticles()
	{
		lock (_lock)
		{
			return QueryArticles("WHERE s.starred = 1");
		}
	}

	/// <summary>
	/// The Today smart feed, as NetNewsWire's <c>fetchArticlesSince</c>: published after <paramref name="cutoff"/>,
	/// or arrived after it when there's no publish date.
	/// </summary>
	public IReadOnlyList<StoredArticle> GetArticlesSince(DateTimeOffset cutoff)
	{
		lock (_lock)
		{
			return QueryArticles($"WHERE {SinceCondition}", ("$cutoff", ToDb(cutoff)));
		}
	}

	/// <summary>Unread counts by feed ID. Feeds with no unread articles are left out.</summary>
	public IReadOnlyDictionary<string, int> UnreadCounts()
	{
		lock (_lock)
		{
			using var command = Command("SELECT a.feedID, COUNT(*) FROM articles a JOIN statuses s ON s.articleID = a.articleID WHERE s.read = 0 GROUP BY a.feedID");
			using var reader = command.ExecuteReader();
			var counts = new Dictionary<string, int>();
			while (reader.Read())
			{
				counts[reader.GetString(0)] = reader.GetInt32(1);
			}
			return counts;
		}
	}

	public int UnreadCountSince(DateTimeOffset cutoff)
	{
		lock (_lock)
		{
			return CountUnread($" AND {SinceCondition}", ("$cutoff", ToDb(cutoff)));
		}
	}

	public int StarredUnreadCount()
	{
		lock (_lock)
		{
			return CountUnread(" AND s.starred = 1");
		}
	}

	public StoredArticle? GetArticle(string articleId)
	{
		lock (_lock)
		{
			return QueryArticles("WHERE a.articleID = $id", ("$id", articleId)).FirstOrDefault();
		}
	}

	/// <summary>Article IDs starting with <paramref name="prefix"/>, so the CLI can accept short IDs.</summary>
	public IReadOnlyList<string> FindArticleIds(string prefix)
	{
		lock (_lock)
		{
			using var command = Command("SELECT articleID FROM articles WHERE articleID LIKE $prefix || '%' LIMIT 2", ("$prefix", prefix));
			using var reader = command.ExecuteReader();
			var ids = new List<string>();
			while (reader.Read())
			{
				ids.Add(reader.GetString(0));
			}
			return ids;
		}
	}

	public int UnreadCount(string? feedId = null)
	{
		lock (_lock)
		{
			return CountUnread(feedId is null ? "" : " AND a.feedID = $feedID", ("$feedID", feedId));
		}
	}

	public int MarkRead(IEnumerable<string> articleIds, bool read = true) => SetStatus("read", articleIds, read);

	public int MarkStarred(IEnumerable<string> articleIds, bool starred = true) => SetStatus("starred", articleIds, starred);

	/// <summary>Marks every unread article read, optionally for one feed. Returns how many changed.</summary>
	public int MarkAllRead(string? feedId = null)
	{
		lock (_lock)
		{
			return Execute(
				"UPDATE statuses SET read = 1 WHERE read = 0" + (feedId is null ? "" : " AND articleID IN (SELECT articleID FROM articles WHERE feedID = $feedID)"),
				("$feedID", feedId));
		}
	}

	public void Dispose()
	{
		lock (_lock)
		{
			_connection.Dispose();
		}
	}

	private int SetStatus(string column, IEnumerable<string> articleIds, bool value)
	{
		lock (_lock)
		{
			using var transaction = _connection.BeginTransaction();
			var changed = articleIds.Sum(id => Execute($"UPDATE statuses SET {column} = $value WHERE articleID = $id AND {column} != $value", ("$id", id), ("$value", value)));
			transaction.Commit();
			return changed;
		}
	}

	/// <summary>Deletes matching feeds with their articles and statuses. Folder memberships cascade.</summary>
	private int DeleteFeedsWhere(string condition, params (string Name, object? Value)[] parameters)
	{
		var feedIds = $"SELECT feedID FROM feeds WHERE {condition}";
		Execute($"DELETE FROM statuses WHERE articleID IN (SELECT articleID FROM articles WHERE feedID IN ({feedIds}))", parameters);
		Execute($"DELETE FROM articles WHERE feedID IN ({feedIds})", parameters);
		return Execute($"DELETE FROM feeds WHERE {condition}", parameters);
	}

	private const string SinceCondition = "(a.datePublished > $cutoff OR (a.datePublished IS NULL AND s.dateArrived > $cutoff))";

	private int CountUnread(string andCondition, params (string Name, object? Value)[] parameters)
	{
		using var command = Command("SELECT COUNT(*) FROM articles a JOIN statuses s ON s.articleID = a.articleID WHERE s.read = 0" + andCondition, parameters);
		return Convert.ToInt32(command.ExecuteScalar());
	}

	private void DeleteUnparentedFeeds() => DeleteFeedsWhere("topLevel = 0 AND feedID NOT IN (SELECT feedID FROM folderFeeds)");

	private List<StoredFolder> QueryFolders(string where, params (string Name, object? Value)[] parameters)
	{
		using var command = Command($"SELECT folderID, name FROM folders {where}", parameters);
		using var reader = command.ExecuteReader();
		var folders = new List<StoredFolder>();
		while (reader.Read())
		{
			folders.Add(new StoredFolder(reader.GetInt64(0), reader.GetString(1)));
		}
		return folders;
	}

	private List<StoredFeed> QueryFeeds(string where, params (string Name, object? Value)[] parameters)
	{
		using var command = Command($"SELECT feedID, url, name, homePageURL, iconURL, etag, lastModified FROM feeds {where}", parameters);
		using var reader = command.ExecuteReader();
		var feeds = new List<StoredFeed>();
		while (reader.Read())
		{
			feeds.Add(new StoredFeed(reader.GetString(0), reader.GetString(1), Text(reader, 2), Text(reader, 3), Text(reader, 4), Text(reader, 5), Text(reader, 6)));
		}
		return feeds;
	}

	private List<StoredArticle> QueryArticles(string where, params (string Name, object? Value)[] parameters)
	{
		using var command = Command(
			$"""
			SELECT a.articleID, a.feedID, a.uniqueID, a.title, a.url, a.externalURL, a.contentHTML, a.contentText, a.summary,
				a.datePublished, a.dateModified, s.dateArrived, s.read, s.starred
			FROM articles a JOIN statuses s ON s.articleID = a.articleID
			{where}
			ORDER BY COALESCE(a.datePublished, s.dateArrived) DESC, a.articleID
			""",
			parameters);
		using var reader = command.ExecuteReader();
		var articles = new List<StoredArticle>();
		while (reader.Read())
		{
			articles.Add(new StoredArticle(
				reader.GetString(0), reader.GetString(1), reader.GetString(2), Text(reader, 3), Text(reader, 4), Text(reader, 5),
				Text(reader, 6), Text(reader, 7), Text(reader, 8), FromDb(reader, 9), FromDb(reader, 10), FromDb(reader, 11) ?? DateTimeOffset.UnixEpoch,
				reader.GetBoolean(12), reader.GetBoolean(13)));
		}
		return articles;
	}

	private int Execute(string sql, params (string Name, object? Value)[] parameters)
	{
		using var command = Command(sql, parameters);
		return command.ExecuteNonQuery();
	}

	private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
	{
		var command = _connection.CreateCommand();
		command.CommandText = sql;
		foreach (var (name, value) in parameters)
		{
			command.Parameters.AddWithValue(name, value ?? DBNull.Value);
		}
		return command;
	}

	private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

	/// <summary>Dates are stored as seconds since 1970, as NetNewsWire's database does.</summary>
	private static double? ToDb(DateTimeOffset? date) => date is { } d ? SwiftCompat.TimeIntervalSince1970(d) : null;

	private static DateTimeOffset? FromDb(SqliteDataReader reader, int ordinal) =>
		reader.IsDBNull(ordinal) ? null : DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(reader.GetDouble(ordinal) * 1000));

	private static double Now() => SwiftCompat.TimeIntervalSince1970(DateTimeOffset.UtcNow);
}
