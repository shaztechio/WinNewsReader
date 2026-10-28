using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using WinNewsReader.App.ViewModels;
using WinNewsReader.Core;
using WinNewsReader.Parser.Xml;
using WinNewsReader.Store;

namespace WinNewsReader.App;

/// <summary>What a click on a notification, or one of its buttons, asks for.</summary>
public enum NotificationAction
{
	Open,
	MarkRead,
	MarkStarred,
}

/// <summary>
/// NetNewsWire's <c>UserNotificationManager</c> as Windows toast notifications: one for each new unread article in a
/// feed with Show Notifications for New Articles on, with Mark as Read and Mark as Starred, taken away again once the
/// article is read. Windows' own toast API rather than the Windows App SDK's: unpackaged and self-contained, the app
/// lacks the SDK's notification service. Clicks reach the app while it runs, so it clears its notifications on exit.
/// </summary>
public sealed partial class NewArticleNotifications
{
	/// <summary>The app's identity in Windows' notification settings and Notification Center.</summary>
	private const string AppUserModelId = "ShazTech.WinNewsReader";

	/// <summary>A refresh can bring dozens of articles at once; past this many per feed, one notification says how many more.</summary>
	private const int MaxNotificationsPerFeed = 5;

	private readonly MainViewModel _viewModel;
	private readonly DispatcherQueue _dispatcher;
	private readonly HashSet<IAccount> _watched = [];
	private ToastNotifier? _notifier;

	public NewArticleNotifications(MainViewModel viewModel, DispatcherQueue dispatcher)
	{
		_viewModel = viewModel;
		_dispatcher = dispatcher;
	}

	/// <summary>Raised on the UI thread when the reader clicks a notification or one of its buttons.</summary>
	public event Action<NotificationAction, string, string>? Invoked;

	/// <summary>
	/// Gives Windows the app's name and icon for its notifications, then watches every account. An unpackaged app
	/// registers its identity under the user's own registry classes.
	/// </summary>
	public void Start()
	{
		using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}"))
		{
			key.SetValue("DisplayName", "WinNewsReader");
			key.SetValue("IconUri", Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png"));
		}
		_notifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
		WatchAccounts();
	}

	/// <summary>Takes away the app's notifications, whose buttons stop working once it's closed.</summary>
	public void Stop()
	{
		try
		{
			ToastNotificationManager.History.Clear(AppUserModelId);
		}
		catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException)
		{
			// Nothing to clear, or notifications are off.
		}
	}

	/// <summary>Starts watching any account added since; call after accounts change.</summary>
	public void WatchAccounts()
	{
		foreach (var account in _viewModel.Accounts.ToList())
		{
			if (_watched.Add(account))
			{
				// Settings and icons belong to the UI thread; refreshes save articles on others.
				account.Store.ArticlesAdded += (_, e) => _dispatcher.TryEnqueue(() => OnArticlesAdded(account, e));
				account.Store.StatusesChanged += (_, e) => OnStatusesChanged(account, e);
			}
		}
	}

	private void OnArticlesAdded(IAccount account, ArticlesAddedEventArgs e)
	{
		if (_notifier is null || !_viewModel.NotifiesNewArticles(account, e.FeedId) || account.Store.GetFeed(e.FeedId) is not { } feed)
		{
			return;
		}
		var unread = e.ArticleIds.Select(account.Store.GetArticle).OfType<StoredArticle>().Where(article => !article.Read).ToList();
		var feedName = NameSorting.DisplayName(feed);
		var icon = _viewModel.IconPath(account, e.FeedId);
		foreach (var article in unread.Take(MaxNotificationsPerFeed))
		{
			var title = Truncated(Plain(article.Title), 100);
			var summary = Truncated(Plain(article.Summary ?? article.ContentText ?? article.ContentHtml), 200);
			Show(account, article.ArticleId, icon, [feedName, title, summary == title ? "" : summary], withButtons: true, Tag(account, article.ArticleId));
		}
		if (unread.Count > MaxNotificationsPerFeed)
		{
			var more = unread.Count - MaxNotificationsPerFeed;
			Show(account, unread[MaxNotificationsPerFeed].ArticleId, icon,
				[feedName, more == 1 ? "1 more new article" : $"{more} more new articles"], withButtons: false, tag: null);
		}
	}

	private void Show(IAccount account, string articleId, string? icon, string[] lines, bool withButtons, string? tag)
	{
		var xml = new StringBuilder();
		xml.Append($"<toast launch=\"{Escaped(Arguments("open", account, articleId))}\"><visual><binding template=\"ToastGeneric\">");
		foreach (var line in lines.Where(line => line.Length > 0))
		{
			xml.Append($"<text>{Escaped(line)}</text>");
		}
		if (icon is not null)
		{
			xml.Append($"<image placement=\"appLogoOverride\" src=\"{Escaped(new Uri(icon).AbsoluteUri)}\" />");
		}
		xml.Append("</binding></visual>");
		if (withButtons)
		{
			xml.Append("<actions>");
			xml.Append($"<action content=\"Mark as Read\" activationType=\"foreground\" arguments=\"{Escaped(Arguments("read", account, articleId))}\" />");
			xml.Append($"<action content=\"Mark as Starred\" activationType=\"foreground\" arguments=\"{Escaped(Arguments("star", account, articleId))}\" />");
			xml.Append("</actions>");
		}
		xml.Append("</toast>");
		var document = new XmlDocument();
		document.LoadXml(xml.ToString());
		var toast = new ToastNotification(document) { Group = Group(account) };
		if (tag is not null)
		{
			toast.Tag = tag;
		}
		toast.Activated += (_, args) =>
		{
			if (args is ToastActivatedEventArgs activated)
			{
				_dispatcher.TryEnqueue(() => Handle(activated.Arguments));
			}
		};
		try
		{
			_notifier!.Show(toast);
		}
		catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException)
		{
			// Notifications turned off for the app, or for Windows.
		}
	}

	private void Handle(string arguments)
	{
		var values = arguments.Split('&').Select(pair => pair.Split('=', 2)).Where(pair => pair.Length == 2)
			.ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
		if (values.TryGetValue("account", out var accountId) && values.TryGetValue("article", out var articleId))
		{
			var action = (values.TryGetValue("action", out var name) ? name : null) switch
			{
				"read" => NotificationAction.MarkRead,
				"star" => NotificationAction.MarkStarred,
				_ => NotificationAction.Open,
			};
			Invoked?.Invoke(action, accountId, articleId);
		}
	}

	/// <summary>NetNewsWire takes an article's notification away once it's read, wherever it was read.</summary>
	private static void OnStatusesChanged(IAccount account, StatusesChangedEventArgs e)
	{
		if (e.Key != StatusKey.Read || !e.Flag)
		{
			return;
		}
		foreach (var articleId in e.ArticleIds)
		{
			try
			{
				ToastNotificationManager.History.Remove(Tag(account, articleId), Group(account), AppUserModelId);
			}
			catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException)
			{
				// Already gone.
			}
		}
	}

	private static string Arguments(string action, IAccount account, string articleId) =>
		$"action={action}&account={Uri.EscapeDataString(account.AccountId)}&article={Uri.EscapeDataString(articleId)}";

	/// <summary>Windows keeps tags and groups short; sync services' article IDs can be long, so they're hashed.</summary>
	private static string Tag(IAccount account, string articleId) =>
		Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(account.AccountId + "|" + articleId)))[..32];

	private static string Group(IAccount account) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(account.AccountId)))[..32];

	private static string Escaped(string text) => SecurityElement.Escape(text);

	/// <summary>Block tags become spaces and inline ones vanish, so "<a>Hacker News</a>," stays "Hacker News,".</summary>
	private static string Plain(string? html) =>
		Whitespace().Replace(Tags().Replace(BlockTags().Replace(html ?? "", " "), ""), " ").Trim().DecodingHtmlEntities();

	private static string Truncated(string text, int length) => text.Length <= length ? text : text[..length].TrimEnd() + "…";

	[GeneratedRegex(@"</?(p|br|div|li|ul|ol|h[1-6]|blockquote|tr|td|th|pre|figure|figcaption|hr)[^>]*>", RegexOptions.IgnoreCase)]
	private static partial Regex BlockTags();

	[GeneratedRegex("<[^>]*>")]
	private static partial Regex Tags();

	[GeneratedRegex(@"\s+")]
	private static partial Regex Whitespace();
}
