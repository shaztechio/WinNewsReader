using WinNewsReader.Core;

namespace WinNewsReader.Store.Tests;

public sealed class UndoStackTests
{
	[Fact]
	public async Task UndoAndRedoRunTheCommandsInOrder()
	{
		var log = new List<string>();
		var stack = new UndoStack();
		stack.Push("Mark Read", () => log.Add("undo 1"), () => log.Add("redo 1"));
		stack.Push("Mark Starred", () => log.Add("undo 2"), () => log.Add("redo 2"));

		Assert.Equal("Mark Starred", stack.UndoLabel);
		await stack.UndoAsync();
		await stack.UndoAsync();
		Assert.False(stack.CanUndo);
		Assert.Equal("Mark Read", stack.RedoLabel);
		await stack.RedoAsync();

		Assert.Equal(["undo 2", "undo 1", "redo 1"], log);
		Assert.Equal("Mark Read", stack.UndoLabel);
		Assert.Equal("Mark Starred", stack.RedoLabel);
	}

	[Fact]
	public async Task ANewCommandClearsWhatCouldBeRedone()
	{
		var stack = new UndoStack();
		stack.Push("Mark Read", () => { }, () => { });
		await stack.UndoAsync();
		Assert.True(stack.CanRedo);

		stack.Push("Mark Starred", () => { }, () => { });

		Assert.False(stack.CanRedo);
		Assert.Equal("Mark Starred", stack.UndoLabel);
	}

	[Fact]
	public async Task CommandsPushedWhileUndoingAreIgnored()
	{
		var stack = new UndoStack();
		stack.Push("Mark Read", () => stack.Push("Mark Unread", () => { }, () => { }), () => { });

		await stack.UndoAsync();

		Assert.False(stack.CanUndo);
		Assert.Equal("Mark Read", stack.RedoLabel);
	}

	[Fact]
	public async Task AFailedUndoStaysOnTheStack()
	{
		var stack = new UndoStack();
		stack.Push("Delete", () => Task.FromException(new InvalidOperationException("offline")), () => Task.CompletedTask);

		await Assert.ThrowsAsync<InvalidOperationException>(stack.UndoAsync);

		Assert.Equal("Delete", stack.UndoLabel);
		Assert.False(stack.CanRedo);
	}

	[Fact]
	public void OldestCommandsDropOffPastTheLimit()
	{
		var stack = new UndoStack(limit: 2);
		stack.Push("a", () => { }, () => { });
		stack.Push("b", () => { }, () => { });
		stack.Push("c", () => { }, () => { });

		Assert.Equal("c", stack.UndoLabel);
	}

	[Fact]
	public async Task ChangedFiresWhenTheLabelsMove()
	{
		var stack = new UndoStack();
		var count = 0;
		stack.Changed += (_, _) => count++;
		stack.Push("Mark Read", () => { }, () => { });
		await stack.UndoAsync();
		stack.Clear();

		Assert.Equal(3, count);
	}
}
