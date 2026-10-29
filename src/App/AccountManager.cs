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

	public Task<IAccount> AddFreshRssAccountAsync(string serverUrl, string username, string password, CancellationToken cancellationToken = default) =>
		AddAccountAsync(AccountKind.FreshRss, serverUrl, username, password, cancellationToken);

	/// <summary>The API address: FreshRSS's is the server the reader typed, the hosted services' are fixed.</summary>
	private static string EndpointFor(AccountKind kind, string serverUrl) => kind switch
	{
		_ when ReaderApiVariants.IsReaderApi(kind) => ReaderApiVariants.Host(ReaderApiVariants.For(kind)) ?? ReaderApiCaller.NormalizedEndpoint(serverUrl),
		_ => throw new NotSupportedException($"{AccountKinds.DisplayName(kind)} accounts aren't supported yet."),
	};

	/// <summary>Checks that the service accepts the login. Throws if it doesn't.</summary>
	private static async Task CheckLoginAsync(AccountKind kind, string endpoint, string username, string password, CancellationToken cancellationToken)
	{
		using var check = new ReaderApiCaller(endpoint, username, password, variant: ReaderApiVariants.For(kind));
		await check.LoginAsync(cancellationToken);
	}

	/// <summary>Checks the login, then saves the account. Throws if the service says no.</summary>
	public async Task<IAccount> AddAccountAsync(AccountKind kind, string serverUrl, string username, string password, CancellationToken cancellationToken = default)
	{
		var endpoint = EndpointFor(kind, serverUrl);
		RejectDuplicateLogin(endpoint, username, exceptAccountId: null);
		await CheckLoginAsync(kind, endpoint, username, password, cancellationToken);
		// A second FreshRSS account is "FreshRSS 2", so the two can be told apart.
		var name = AccountRules.UniqueName(AccountKinds.DisplayName(kind), _accounts.Select(account => account.Name));
		var record = new AccountRecord(Guid.NewGuid().ToString("N"), kind, name, endpoint, username);
		credentials.SetSecret(CredentialTarget(record.AccountId), username, password);
		AccountsFile.Save(AccountsPath, [.. AccountsFile.Load(AccountsPath), record]);
		var account = OpenSyncAccount(record, password);
		_accounts.Add(account);
		return account;
	}

	/// <summary>
	/// NetNewsWire's credentials sheet: checks the login if the server, username or password changed (a blank password
	/// keeps the saved one), then saves the new name, server, username and password. The account keeps its data.
	/// </summary>
	public async Task UpdateAccountAsync(
		ISyncAccount account, string name, string serverUrl, string username, string password, CancellationToken cancellationToken = default)
	{
		var current = account.Record;
		var endpoint = EndpointFor(current.Kind, serverUrl);
		var target = CredentialTarget(account.AccountId);
		if (AccountRules.NameIsTaken(name, _accounts.Where(other => other.AccountId != account.AccountId).Select(other => other.Name)))
		{
			throw new InvalidOperationException($"There’s already an account named “{name}”. Choose a different name.");
		}
		RejectDuplicateLogin(endpoint, username, account.AccountId);
		var secret = password.Length > 0 ? password : credentials.GetSecret(target) ?? "";
		var loginChanged = password.Length > 0 || endpoint != current.Endpoint || username != current.Username;
		if (loginChanged)
		{
			await CheckLoginAsync(current.Kind, endpoint, username, secret, cancellationToken);
		}
		var record = current with { Name = name, Endpoint = endpoint, Username = username };
		if (loginChanged)
		{
			credentials.SetSecret(target, username, secret);
		}
		AccountsFile.Save(AccountsPath, AccountsFile.Load(AccountsPath).Select(saved => saved.AccountId == record.AccountId ? record : saved));
		await account.ReconfigureAsync(record, secret, cancellationToken);
	}

	/// <summary>NetNewsWire's <c>duplicateServiceAccount</c>: the same server and username can't be two accounts.</summary>
	private void RejectDuplicateLogin(string endpoint, string username, string? exceptAccountId)
	{
		if (AccountRules.FindDuplicateLogin(_accounts.OfType<ISyncAccount>().Select(account => account.Record), endpoint, username, exceptAccountId) is { } existing)
		{
			throw new InvalidOperationException($"You already have an account for “{username}” on {new Uri(endpoint).Host}: “{existing.Name}”.");
		}
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

	private IAccount OpenSyncAccount(AccountRecord record, string password)
	{
		var store = new ArticlesStore(Path.Combine(AccountDirectory(record.AccountId), "articles.db"));
		_stores.Add(store);
		return new ReaderApiAccount(record, store, new ReaderApiCaller(record.Endpoint, record.Username, password, variant: ReaderApiVariants.For(record.Kind)), downloader);
	}
}
