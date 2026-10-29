namespace WinNewsReader.Core;

public enum ActivityLevel
{
	Info,
	Error,
}

/// <summary>One line of NetNewsWire's Activity Log or Error Log.</summary>
public sealed record ActivityEntry(DateTimeOffset Time, ActivityLevel Kind, string Account, string? Feed, string Message);

/// <summary>A feed being downloaded right now, for the Current Activity window.</summary>
public sealed record ActivityInProgress(string Account, string Feed, DateTimeOffset Since);

/// <summary>
/// What the refresh has been doing, kept in memory for NetNewsWire's Current Activity, Activity Log and Error Log
/// windows. Refreshes run on background threads, so every member is safe to call from any thread, and
/// <see cref="Changed"/> may be raised on any of them.
/// </summary>
public sealed class ActivityLog
{
	private readonly object _lock = new();
	private readonly List<ActivityEntry> _entries = [];
	private readonly Dictionary<object, ActivityInProgress> _inProgress = [];
	private readonly int _limit;

	public ActivityLog(int limit = 2000) => _limit = limit;

	public event EventHandler? Changed;

	/// <summary>Every entry, oldest first.</summary>
	public IReadOnlyList<ActivityEntry> Entries()
	{
		lock (_lock)
		{
			return [.. _entries];
		}
	}

	public IReadOnlyList<ActivityEntry> Errors() => [.. Entries().Where(entry => entry.Kind == ActivityLevel.Error)];

	/// <summary>The downloads under way, longest-running first.</summary>
	public IReadOnlyList<ActivityInProgress> InProgress()
	{
		lock (_lock)
		{
			return [.. _inProgress.Values.OrderBy(item => item.Since)];
		}
	}

	public void Add(ActivityLevel kind, string account, string? feed, string message)
	{
		lock (_lock)
		{
			_entries.Add(new ActivityEntry(DateTimeOffset.Now, kind, account, feed, message));
			if (_entries.Count > _limit)
			{
				_entries.RemoveRange(0, _entries.Count - _limit);
			}
		}
		Changed?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Marks a feed as being downloaded. <paramref name="token"/> is handed back to <see cref="Finished"/>.</summary>
	public object Started(string account, string feed)
	{
		var token = new object();
		lock (_lock)
		{
			_inProgress[token] = new ActivityInProgress(account, feed, DateTimeOffset.Now);
		}
		Changed?.Invoke(this, EventArgs.Empty);
		return token;
	}

	public void Finished(object token)
	{
		lock (_lock)
		{
			_inProgress.Remove(token);
		}
		Changed?.Invoke(this, EventArgs.Empty);
	}

	public void Clear()
	{
		lock (_lock)
		{
			_entries.Clear();
		}
		Changed?.Invoke(this, EventArgs.Empty);
	}
}
