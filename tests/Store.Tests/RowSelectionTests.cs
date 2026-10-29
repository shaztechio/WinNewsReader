using WinNewsReader.Core;

namespace WinNewsReader.Store.Tests;

/// <summary>Ctrl+click and Shift+click selection of sidebar rows.</summary>
public sealed class RowSelectionTests
{
	private static readonly string[] Rows = ["a", "b", "c", "d", "e"];

	[Fact]
	public void CtrlClickAddsARowAndRemovesItAgain()
	{
		var selected = RowSelection.Toggle(["a"], "c");
		Assert.Equal(["a", "c"], selected);
		Assert.Equal(["a"], RowSelection.Toggle(selected, "c"));
	}

	[Theory]
	[InlineData("b", "d", new[] { "b", "c", "d" })]
	[InlineData("d", "b", new[] { "b", "c", "d" })]
	[InlineData("c", "c", new[] { "c" })]
	public void ShiftClickSelectsTheRangeInListOrder(string anchor, string clicked, string[] expected) =>
		Assert.Equal(expected, RowSelection.Range(Rows, anchor, clicked));

	[Fact]
	public void ShiftClickWithAnAnchorNotInTheListSelectsJustTheClickedRow() =>
		Assert.Equal(["d"], RowSelection.Range(Rows, "gone", "d"));
}
