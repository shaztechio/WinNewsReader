using System.Text.Json.Serialization;

namespace WinNewsReader.Sync.GoogleReader;

// JSON shapes of the Google Reader API, as NetNewsWire's ReaderAPI models read them.

public sealed record ReaderApiTag([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("type")] string? Type)
{
	/// <summary>The folder name in a <c>user/-/label/Name</c> tag; null for other tags, such as states.</summary>
	public string? FolderName => Id.IndexOf("/label/", StringComparison.Ordinal) is var index and >= 0 ? Id[(index + "/label/".Length)..] : null;
}

public sealed record ReaderApiCategory([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("label")] string? Label);

public sealed record ReaderApiSubscription(
	[property: JsonPropertyName("id")] string FeedId,
	[property: JsonPropertyName("title")] string? Title,
	[property: JsonPropertyName("categories")] List<ReaderApiCategory>? Categories,
	[property: JsonPropertyName("url")] string? FeedUrl,
	[property: JsonPropertyName("htmlUrl")] string? HomePageUrl,
	[property: JsonPropertyName("iconUrl")] string? IconUrl)
{
	/// <summary>The feed's URL, or its ID without <c>feed/</c> when the server leaves the URL out.</summary>
	public string Url => FeedUrl ?? (FeedId.StartsWith("feed/", StringComparison.Ordinal) ? FeedId["feed/".Length..] : FeedId);
}

public sealed record ReaderApiEntry(
	[property: JsonPropertyName("id")] string Id,
	[property: JsonPropertyName("title")] string? Title,
	[property: JsonPropertyName("author")] string? Author,
	[property: JsonPropertyName("published")] double? Published,
	[property: JsonPropertyName("summary")] ReaderApiSummary? Summary,
	[property: JsonPropertyName("alternate")] List<ReaderApiLink>? Alternates,
	[property: JsonPropertyName("categories")] List<string>? Categories,
	[property: JsonPropertyName("origin")] ReaderApiOrigin? Origin,
	[property: JsonPropertyName("enclosure")] List<ReaderApiEnclosure>? Enclosures);

public sealed record ReaderApiSummary([property: JsonPropertyName("content")] string? Content);

public sealed record ReaderApiLink([property: JsonPropertyName("href")] string? Href);

public sealed record ReaderApiOrigin([property: JsonPropertyName("streamId")] string? StreamId, [property: JsonPropertyName("title")] string? Title);

public sealed record ReaderApiEnclosure([property: JsonPropertyName("href")] string? Href, [property: JsonPropertyName("type")] string? Type);

internal sealed record ReaderApiTagList([property: JsonPropertyName("tags")] List<ReaderApiTag>? Tags);

internal sealed record ReaderApiSubscriptionList([property: JsonPropertyName("subscriptions")] List<ReaderApiSubscription>? Subscriptions);

internal sealed record ReaderApiItemRefs(
	[property: JsonPropertyName("itemRefs")] List<ReaderApiItemRef>? ItemRefs,
	[property: JsonPropertyName("continuation")] string? Continuation);

internal sealed record ReaderApiItemRef([property: JsonPropertyName("id")] string? Id);

internal sealed record ReaderApiEntryList([property: JsonPropertyName("items")] List<ReaderApiEntry>? Items);

internal sealed record ReaderApiQuickAddResult(
	[property: JsonPropertyName("numResults")] int NumResults,
	[property: JsonPropertyName("error")] string? Error,
	[property: JsonPropertyName("streamId")] string? StreamId);
