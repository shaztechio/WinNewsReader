using WinNewsReader.Core;

namespace WinNewsReader.Store.Tests;

public sealed class AccountRulesTests
{
	private static AccountRecord Record(string id, string name, string endpoint, string username) =>
		new(id, AccountKind.FreshRss, name, endpoint, username);

	[Fact]
	public void UniqueNameCountsUpFromTwo()
	{
		Assert.Equal("FreshRSS", AccountRules.UniqueName("FreshRSS", ["On This PC"]));
		Assert.Equal("FreshRSS 2", AccountRules.UniqueName("FreshRSS", ["On This PC", "FreshRSS"]));
		Assert.Equal("FreshRSS 3", AccountRules.UniqueName("FreshRSS", ["FreshRSS", "FreshRSS 2"]));
		// A gap is filled, and case and stray spaces count as the same name.
		Assert.Equal("FreshRSS 2", AccountRules.UniqueName("FreshRSS", ["  freshrss ", "FreshRSS 3"]));
	}

	[Theory]
	[InlineData("FreshRSS", true)]
	[InlineData("freshrss", true)]
	[InlineData("  FreshRSS  ", true)]
	[InlineData("On This PC", true)]
	[InlineData("FreshRSS 2", false)]
	[InlineData("", false)]
	public void NamesDifferingOnlyByCaseOrSpacesAreTheSame(string name, bool taken) =>
		Assert.Equal(taken, AccountRules.NameIsTaken(name, ["FreshRSS", "On This PC"]));

	[Fact]
	public void SameServerAndUsernameIsTheSameLogin()
	{
		var accounts = new[]
		{
			Record("a", "Home", "https://rss.example/api/greader.php", "alice"),
			Record("b", "Work", "https://other.example/api/greader.php", "alice"),
		};

		Assert.Equal("a", AccountRules.FindDuplicateLogin(accounts, "https://rss.example/api/greader.php", "alice")?.AccountId);
		// Case in the host or the username, and a trailing slash, don't make a different login.
		Assert.Equal("a", AccountRules.FindDuplicateLogin(accounts, "https://RSS.example/api/greader.php/", " Alice ")?.AccountId);
		// A different username on the same server, or the same username on another server, is a different account.
		Assert.Null(AccountRules.FindDuplicateLogin(accounts, "https://rss.example/api/greader.php", "bob"));
		Assert.Null(AccountRules.FindDuplicateLogin(accounts, "https://third.example/api/greader.php", "alice"));
	}

	[Fact]
	public void AnAccountIsNotADuplicateOfItself()
	{
		var accounts = new[] { Record("a", "Home", "https://rss.example/api/greader.php", "alice") };
		Assert.Null(AccountRules.FindDuplicateLogin(accounts, "https://rss.example/api/greader.php", "alice", exceptAccountId: "a"));
		Assert.NotNull(AccountRules.FindDuplicateLogin(accounts, "https://rss.example/api/greader.php", "alice", exceptAccountId: "other"));
	}
}
