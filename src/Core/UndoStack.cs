namespace WinNewsReader.Core;

/// <summary>
/// NetNewsWire's undo manager for the commands it supports: marking read or starred, and deleting feeds and folders.
/// Doing something new clears what could be redone, as an undo manager does.
/// </summary>
public sealed class UndoStack
{
	private readonly record struct Entry(string Label, Func<Task> Undo, Func<Task> Redo);

	private readonly List<Entry> _undo = [];
	private readonly List<Entry> _redo = [];
	private readonly int _limit;
	private bool _running;

	public UndoStack(int limit = 100) => _limit = limit;

	/// <summary>Raised when what can be undone or redone changes, so menus can update their labels.</summary>
	public event EventHandler? Changed;

	public bool CanUndo => _undo.Count > 0;

	public bool CanRedo => _redo.Count > 0;

	/// <summary>The command that Undo would reverse, such as "Mark Read"; null when there is none.</summary>
	public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;

	public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

	/// <summary>Records a command that has just been done. Does nothing while an undo or redo is running its own changes.</summary>
	public void Push(string label, Func<Task> undo, Func<Task> redo)
	{
		if (_running)
		{
			return;
		}
		_undo.Add(new Entry(label, undo, redo));
		if (_undo.Count > _limit)
		{
			_undo.RemoveAt(0);
		}
		_redo.Clear();
		Changed?.Invoke(this, EventArgs.Empty);
	}

	public void Push(string label, Action undo, Action redo) =>
		Push(label, () => { undo(); return Task.CompletedTask; }, () => { redo(); return Task.CompletedTask; });

	/// <summary>Reverses the latest command. If that fails, it stays where it was so it can be tried again.</summary>
	public async Task UndoAsync()
	{
		if (_running || _undo.Count == 0)
		{
			return;
		}
		var entry = _undo[^1];
		_running = true;
		try
		{
			await entry.Undo();
			_undo.RemoveAt(_undo.Count - 1);
			_redo.Add(entry);
		}
		finally
		{
			_running = false;
			Changed?.Invoke(this, EventArgs.Empty);
		}
	}

	public async Task RedoAsync()
	{
		if (_running || _redo.Count == 0)
		{
			return;
		}
		var entry = _redo[^1];
		_running = true;
		try
		{
			await entry.Redo();
			_redo.RemoveAt(_redo.Count - 1);
			_undo.Add(entry);
		}
		finally
		{
			_running = false;
			Changed?.Invoke(this, EventArgs.Empty);
		}
	}

	public void Clear()
	{
		if (_undo.Count == 0 && _redo.Count == 0)
		{
			return;
		}
		_undo.Clear();
		_redo.Clear();
		Changed?.Invoke(this, EventArgs.Empty);
	}
}
