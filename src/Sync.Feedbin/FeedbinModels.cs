using System.Globalization;
using System.Text.Json.Serialization;

namespace WinNewsReader.Sync.Feedbin;

public sealed record FeedbinTag([property: JsonPropertyName("id")] long Id, [property: JsonPropertyName("name")] string Name);

public sealed record FeedbinTagging(
	[property: JsonPropertyName("id")] long Id,
	[property: JsonPropertyName("feed_id")] long FeedId,
	[property: JsonPropertyName("name")] string Name);

public sealed class FeedbinSubscription
{
	[JsonPropertyName("id")]
	public long SubscriptionId { get; init; }

	[JsonPropertyName("feed_id")]
	public long FeedId { get; init; }

	[JsonPropertyName("title")]
	public string? Title { get; init; }

	[JsonPropertyName("feed_url")]
	public string Url { get; init; } = "";

	[JsonPropertyName("site_url")]
	public string? HomePageUrl { get; init; }
}

/// <summary>One of the feeds Feedbin offers when the address given has several (a 300 answer).</summary>
public sealed record FeedbinSubscriptionChoice(
	[property: JsonPropertyName("title")] string? Title,
	[property: JsonPropertyName("feed_url")] string Url);

public sealed class FeedbinEntry
{
	[JsonPropertyName("id")]
	public long Id { get; init; }

	[JsonPropertyName("feed_id")]
	public long FeedId { get; init; }

	[JsonPropertyName("title")]
	public string? Title { get; init; }

	[JsonPropertyName("url")]
	public string? Url { get; init; }

	[JsonPropertyName("author")]
	public string? Author { get; init; }

	[JsonPropertyName("content")]
	public string? Content { get; init; }

	[JsonPropertyName("summary")]
	public string? Summary { get; init; }

	/// <summary>Kept as text: Feedbin's dates occasionally fail to parse, and that should lose one date, not the whole page.</summary>
	[JsonPropertyName("published")]
	public string? Published { get; init; }

	[JsonPropertyName("json_feed")]
	public FeedbinJsonFeed? JsonFeed { get; init; }

	[JsonPropertyName("images")]
	public FeedbinImages? Images { get; init; }

	public DateTimeOffset? PublishedDate =>
		DateTimeOffset.TryParse(Published, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
}

public sealed class FeedbinJsonFeed
{
	[JsonPropertyName("external_url")]
	public string? ExternalUrl { get; init; }

	[JsonPropertyName("author")]
	public FeedbinJsonFeedAuthor? Author { get; init; }
}

public sealed class FeedbinJsonFeedAuthor
{
	[JsonPropertyName("url")]
	public string? Url { get; init; }

	[JsonPropertyName("avatar")]
	public string? Avatar { get; init; }
}

public sealed class FeedbinImages
{
	[JsonPropertyName("original_url")]
	public string? OriginalUrl { get; init; }
}

/// <summary>What Feedbin said to a request to subscribe.</summary>
public abstract record FeedbinCreateResult
{
	public sealed record Created(FeedbinSubscription Subscription) : FeedbinCreateResult;

	public sealed record Choices(IReadOnlyList<FeedbinSubscriptionChoice> Feeds) : FeedbinCreateResult;

	public sealed record AlreadySubscribed : FeedbinCreateResult;

	public sealed record NotFound : FeedbinCreateResult;
}
