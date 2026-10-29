using WinNewsReader.Core;
using WinNewsReader.Parser.Feeds;

namespace WinNewsReader.Store.Tests;

public sealed class AuthorAvatarTests
{
	private static ParsedAuthor Author(string? avatar) => new("Name", null, avatar, null);

	[Fact]
	public void OneAuthorWithAnAvatarGivesItsAddress() =>
		Assert.Equal("https://example.com/me.png", AuthorAvatar.UrlFor([Author("https://example.com/me.png")]));

	[Fact]
	public void SeveralAuthorsGiveNone() =>
		Assert.Null(AuthorAvatar.UrlFor([Author("https://example.com/a.png"), Author("https://example.com/b.png")]));

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("not a url")]
	[InlineData("file:///C:/me.png")]
	[InlineData("javascript:alert(1)")]
	public void AnythingButAWebAddressGivesNone(string? avatar) => Assert.Null(AuthorAvatar.UrlFor([Author(avatar)]));

	[Fact]
	public void NoAuthorsGiveNone()
	{
		Assert.Null(AuthorAvatar.UrlFor(null));
		Assert.Null(AuthorAvatar.UrlFor([]));
	}
}
