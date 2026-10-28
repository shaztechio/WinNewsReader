using System.Text.Json;
using System.Text.Json.Serialization;
using WinNewsReader.Store;

namespace WinNewsReader.Core;

public enum AccountKind
{
	Local,
	FreshRss,
}

/// <summary>What a refresh did, for the status bar.</summary>
public sealed record RefreshSummary(int NewArticles, int FailedFeeds, string? Error = null);

/// <summary>
/// An account: the local "On This PC" one, or a sync service. Each has its own store. Changes that a sync service
/// must hear about go through the account rather than straight to the store.
/// </summary>
public interface IAccount
{
	string AccountId { get; }

	AccountKind Kind { get; }

	string Name { get; }

	ArticlesStore Store { get; }

	/// <summary>Whether a feed can sit outside every folder. FreshRSS puts every feed in a category.</summary>
	bool FeedsCanBeAtTopLevel { get; }

	/// <summary>Whether a feed can be in several folders at once, so it can be copied as well as moved.</summary>
	bool FeedsCanBeInSeveralFolders { get; }

	/// <summary>
	/// Moves a feed between folders, null meaning the top level; copying leaves it where it was too. Only within
	/// this account.
	/// </summary>
	Task MoveFeedAsync(string feedId, long? fromFolderId, long? toFolderId, bool copy, CancellationToken cancellationToken = default);

	Task<RefreshSummary> RefreshAllAsync(CancellationToken cancellationToken = default);

	/// <summary>Finds the feed for <paramref name="url"/> and subscribes. Throws <see cref="SubscribeException"/> if it can't.</summary>
	Task<StoredFeed> SubscribeAsync(string url, string? folderName, CancellationToken cancellationToken = default);

	/// <summary>Takes the feed out of one folder, or the top level when <paramref name="folderId"/> is null.</summary>
	Task RemoveFeedAsync(string feedId, long? folderId, CancellationToken cancellationToken = default);

	Task RenameFolderAsync(long folderId, string name, CancellationToken cancellationToken = default);

	/// <summary>NetNewsWire's <c>renameFeed</c>: sets the feed's edited name, on the server too for a sync account.</summary>
	Task RenameFeedAsync(string feedId, string name, CancellationToken cancellationToken = default);

	/// <summary>Removes the folder; its feeds go too unless they're somewhere else as well.</summary>
	Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken = default);

	/// <summary>Marks articles read or unread, starred or not.</summary>
	void SetArticleStatus(IReadOnlyCollection<string> articleIds, StatusKey key, bool flag);
}

/// <summary>A sync account as listed in <c>accounts.json</c>. Its password is in the credential store, not here.</summary>
public sealed record AccountRecord(string AccountId, AccountKind Kind, string Name, string Endpoint, string Username);

/// <summary>The list of sync accounts. The local account is always there and isn't listed.</summary>
public static class AccountsFile
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

	public static List<AccountRecord> Load(string path)
	{
		try
		{
			return File.Exists(path) ? JsonSerializer.Deserialize<List<AccountRecord>>(File.ReadAllText(path), JsonOptions) ?? [] : [];
		}
		catch (JsonException)
		{
			return [];
		}
	}

	public static void Save(string path, IEnumerable<AccountRecord> accounts)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		var temporary = path + ".tmp";
		File.WriteAllText(temporary, JsonSerializer.Serialize(accounts.ToList(), JsonOptions));
		File.Move(temporary, path, overwrite: true);
	}
}
