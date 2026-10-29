using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using WinNewsReader.Parser.Xml;
using WinNewsReader.Store;

namespace WinNewsReader.App.ViewModels;

/// <summary>A row in the timeline.</summary>
public sealed partial class ArticleItem : ObservableObject
{
	private const int MaxSummaryLength = 300;

	/// <summary>The line under each timeline row: <c>CorreiaSeparators</c>. Set at launch, before rows are made.</summary>
	public static Microsoft.UI.Xaml.Thickness SeparatorThickness { get; set; }

	private string? _summary;

	public ArticleItem(Core.IAccount account, StoredArticle article, string feedName, Microsoft.UI.Xaml.Media.ImageSource? icon)
	{
		Account = account;
		Article = article;
		FeedName = feedName;
		IsRead = article.Read;
		IsStarred = article.Starred;
		Icon = icon;
	}

	/// <summary>The feed's favicon, or null to show a placeholder.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasIcon), nameof(LacksIcon))]
	public partial Microsoft.UI.Xaml.Media.ImageSource? Icon { get; set; }

	public bool HasIcon => Icon is not null;

	public bool LacksIcon => Icon is null;

	/// <summary>The account the article came from; status changes go through it.</summary>
	public Core.IAccount Account { get; }

	public StoredArticle Article { get; }

	public string ArticleId => Article.ArticleId;

	public string FeedName { get; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsUnread))]
	public partial bool IsRead { get; set; }

	public bool IsUnread => !IsRead;

	[ObservableProperty]
	public partial bool IsStarred { get; set; }

	/// <summary>Articles without a title show their summary in its place, as in NetNewsWire.</summary>
	public string Title => Article.Title is { Length: > 0 } title ? Collapse(title.DecodingHtmlEntities()) : Summary;

	public string Summary => _summary ??= MakeSummary();

	/// <summary>Empty when the summary is already standing in for the title.</summary>
	public string SummaryBelowTitle => Article.Title is { Length: > 0 } ? Summary : "";

	public bool HasSummaryBelowTitle => SummaryBelowTitle.Length > 0;

	public string DateText
	{
		get
		{
			var date = Article.LogicalDatePublished.ToLocalTime();
			return date.Date == DateTime.Today ? date.ToString("t") : date.ToString("d");
		}
	}

	public string? LinkUrl => Article.Url ?? Article.ExternalUrl;

	private string MakeSummary()
	{
		var text = Article.Summary ?? Article.ContentText ?? Article.ContentHtml ?? "";
		text = Collapse(Tags().Replace(ScriptsAndStyles().Replace(text, " "), " ").DecodingHtmlEntities());
		return text.Length <= MaxSummaryLength ? text : text[..MaxSummaryLength].TrimEnd() + "…";
	}

	private static string Collapse(string text) => Whitespace().Replace(text, " ").Trim();

	[GeneratedRegex(@"<(script|style)\b.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
	private static partial Regex ScriptsAndStyles();

	[GeneratedRegex("<[^>]*>")]
	private static partial Regex Tags();

	[GeneratedRegex(@"\s+")]
	private static partial Regex Whitespace();
}
