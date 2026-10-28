using WinNewsReader.Core;
using WinNewsReader.Store;
using WinNewsReader.Sync.GoogleReader;

namespace WinNewsReader.App;

/// <summary>
/// The app's accounts: "On This PC" first, then sync accounts from <c>accounts.json</c>. Each account keeps its data in
/// its own folder; sync passwords are in Windows Credential Manager.
/// </summary>
public sealed class AccountManager(string dataRoot, FeedDownloader downloader, ICredentialStore credentials) : IDisposable
{
	private readonly List<IAccount> _accounts = [];
	private readonly List<ArticlesStore> _stores = [];

	public IReadOnlyList<IAccount> Accounts => _accounts;

	private string AccountsPath => Path.Combine(dataRoot, "accounts.json");

	public void LoadAll()
	{
		var localStore = new ArticlesStore(Path.Combine(dataRoot, LocalAccount.LocalAccountId, "articles.db"));
		_stores.Add(localStore);
		_accounts.Add(new LocalAccount(localStore, downloader));
		foreach (var record in AccountsFile.Load(AccountsPath))
		{
			// An account whose password has gone missing still opens, showing what it last synced; refreshing reports the login failure.
			_accounts.Add(OpenSyncAccount(record, credentials.GetSecret(CredentialTarget(record.AccountId)) ?? ""));
		}
	}

	public LocalAccount LocalAccount => (LocalAccount)_accounts[0];

	/// <summary>Checks the login, then saves the account. Throws <see cref="ReaderApiException"/> if the server says no.</summary>
	public async Task<IAccount> AddFreshRssAccountAsync(string serverUrl, string username, string password, CancellationToken cancellationToken = default)
	{
		var endpoint = ReaderApiCaller.NormalizedEndpoint(serverUrl);
		using (var check = new ReaderApiCaller(endpoint, username, password))
		{
			await check.LoginAsync(cancellationToken);
		}
		var record = new AccountRecord(Guid.NewGuid().ToString("N"), AccountKind.FreshRss, "FreshRSS", endpoint, username);
		credentials.SetSecret(CredentialTarget(record.AccountId), username, password);
		AccountsFile.Save(AccountsPath, [.. AccountsFile.Load(AccountsPath), record]);
		var account = OpenSyncAccount(record, password);
		_accounts.Add(account);
		return account;
	}

	/// <summary>Removes a sync account and its local copy of the data. Nothing changes on the server.</summary>
	public void RemoveAccount(IAccount account)
	{
		if (account is LocalAccount)
		{
			throw new InvalidOperationException("The local account can't be removed.");
		}
		_accounts.Remove(account);
		(account as IDisposable)?.Dispose();
		_stores.Remove(account.Store);
		account.Store.Dispose();
		AccountsFile.Save(AccountsPath, AccountsFile.Load(AccountsPath).Where(record => record.AccountId != account.AccountId));
		credentials.Delete(CredentialTarget(account.AccountId));
		Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
		try
		{
			Directory.Delete(AccountDirectory(account.AccountId), recursive: true);
		}
		catch (IOException)
		{
			// The data folder is only a cache of what's on the server; a leftover one is harmless.
		}
	}

	public void Dispose()
	{
		foreach (var account in _accounts.OfType<IDisposable>())
		{
			account.Dispose();
		}
		foreach (var store in _stores)
		{
			store.Dispose();
		}
	}

	private static string CredentialTarget(string accountId) => $"WinNewsReader:{accountId}";

	private string AccountDirectory(string accountId) => Path.Combine(dataRoot, "Accounts", accountId);

	private ReaderApiAccount OpenSyncAccount(AccountRecord record, string password)
	{
		var store = new ArticlesStore(Path.Combine(AccountDirectory(record.AccountId), "articles.db"));
		_stores.Add(store);
		return new ReaderApiAccount(record, store, new ReaderApiCaller(record.Endpoint, record.Username, password), downloader);
	}
}
