using VaultManager.Core.Changes;
using VaultManager.Core.Foundation;
using VaultManager.Core.Obsidian;

namespace VaultManager.App.Logic;

internal enum LineState { Queued, Done, Failed, Waiting, NeedsReview }

internal sealed class QueueLine(ChangeItem item)
{
	public ChangeItem Item { get; set; } = item;
	public LineState State { get; set; } = LineState.Queued;
	public ChangeFailure Failure { get; set; } = ChangeFailure.None;
	public ChangeNote Note { get; set; } = ChangeNote.None;

	/// <summary>Asked for and not over yet. A Done line counts until the next scan has seen its result, or a second click would run on stale data.</summary>
	public bool IsInFlight => State is LineState.Queued or LineState.Waiting or LineState.Done;

	/// <summary>What a person has to see: it still waits, or it needs them.</summary>
	public bool NeedsShowing => State is LineState.Waiting or LineState.Failed or LineState.NeedsReview;
}

/// <summary>What the runner has to do and what happened to each request. No disk and no words in here.</summary>
internal sealed class ChangeQueue
{
	readonly List<QueueLine> _lines = [];

	public IReadOnlyList<QueueLine> Lines => _lines;

	public int WaitingCount => _lines.Count(l => l.State == LineState.Waiting);

	/// <returns>False when the same request is already on its way: a double click must not run twice.</returns>
	public bool Add(ChangeItem item)
	{
		string key = QueueRules.DedupeKey(item);
		int same = _lines.FindIndex(l => QueueRules.DedupeKey(l.Item) == key);
		if (same < 0)
		{
			_lines.Add(new QueueLine(item));
			return true;
		}

		if (_lines[same].IsInFlight)
		{
			return false;
		}

		_lines[same] = new QueueLine(item);
		return true;
	}

	/// <summary>A line read back from the database after a restart.</summary>
	public void Restore(ChangeItem item) => _lines.Add(new QueueLine(item) { State = LineState.Waiting });

	public void Remove(Guid id) => _lines.RemoveAll(l => l.Item.Id == id);

	public QueueLine? Find(Guid id) => _lines.FirstOrDefault(l => l.Item.Id == id);

	public IReadOnlyList<ChangeItem> NextBatch() => _lines.Where(l => l.State == LineState.Queued).Select(l => l.Item).ToList();

	public IReadOnlyList<QueueLine> Waiting() => _lines.Where(l => l.State == LineState.Waiting).ToList();

	public void Release(Guid id)
	{
		if (Find(id) is { } line)
		{
			line.State = LineState.Queued;
			line.Failure = ChangeFailure.None;
		}
	}

	/// <summary>Finished lines leave once a scan has seen what they did.</summary>
	public void DropFinished() => _lines.RemoveAll(l => l.State == LineState.Done);

	public void Record(ChangeResult result)
	{
		QueueLine? line = Find(result.ItemId);
		if (line is null)
		{
			return;
		}

		line.State = result.Status switch
		{
			ChangeStatus.Done => LineState.Done,
			ChangeStatus.Failed => LineState.Failed,
			ChangeStatus.Waiting => LineState.Waiting,
			ChangeStatus.NeedsReview => LineState.NeedsReview,
			_ => throw new ArgumentOutOfRangeException(nameof(result)),
		};
		line.Failure = result.Failure;
		line.Note = result.Note;
	}

	public void Renew(Guid id, ChangeItem fresh)
	{
		int index = _lines.FindIndex(l => l.Item.Id == id);
		if (index >= 0)
		{
			_lines[index] = new QueueLine(fresh);
		}
	}

	public bool IsPluginPending(string vaultPath, string pluginId) => _lines.Any(l => l.IsInFlight && l.Item switch
	{
		CopyPlugin c => Same(c.TargetVault, vaultPath) && c.PluginId == pluginId,
		DownloadPlugin d => Same(d.TargetVault, vaultPath) && d.PluginId == pluginId,
		_ => false,
	});

	public bool IsTogglePending(string vaultPath, string pluginId) => _lines.Any(l => l.IsInFlight && l.Item is SetPluginEnabled s && Same(s.TargetVault, vaultPath) && s.PluginId == pluginId);

	public bool ArePartsPending(string vaultPath) => _lines.Any(l => l.IsInFlight && l.Item is AddProfileParts p && Same(p.TargetVault, vaultPath));

	public bool IsCreatePending(string path) => _lines.Any(l => l.IsInFlight && l.Item is CreateVault c && Same(c.NewVaultPath, path));

	public bool IsSettingsPending(string target, string relative) => _lines.Any(l => l.IsInFlight && l.Item is CopySettingsFile s && Same(s.TargetVault, target) && s.RelativePath == relative);

	static bool Same(string a, string b) => VaultManager.Core.Foundation.PathRules.Same(a, b);
}

internal static class QueueRules
{
	public static string DedupeKey(ChangeItem item)
	{
		string vault = VaultManager.Core.Foundation.PathRules.Key(item.VaultPath);
		return item switch
		{
			CopyPlugin c => $"plugin|{vault}|{c.PluginId}",
			DownloadPlugin d => $"plugin|{vault}|{d.PluginId}",
			SetPluginEnabled s => $"toggle|{vault}|{s.PluginId}",
			CopySettingsFile f => $"settings|{vault}|{f.RelativePath}",
			AddProfileParts => $"parts|{vault}",
			CreateVault => $"create|{vault}",
			RegisterVault => $"register|{vault}",
			_ => item.Id.ToString(),
		};
	}

	/// <summary>A line whose vault moved on can be made again from what the last scan saw. Settings lines go back through Compare.</summary>
	public static ChangeItem? Renew(ChangeItem item, WorkspaceSnapshot? snapshot)
	{
		switch (item)
		{
			case CopyPlugin copy:
				PluginRow? row = snapshot?.Plugins.FirstOrDefault(p => p.Id == copy.PluginId);
				if (row?.NewestVersion is null || row.SourceVaultPath is null)
				{
					return null;
				}

				PluginInstall? target = row.Installs.FirstOrDefault(i => VaultManager.Core.Foundation.PathRules.Same(i.VaultPath, copy.TargetVault));

				// The vault may have moved past this line's version: renewing must never turn it into a downgrade.
				if (target?.Version is not null && PluginVersion.Compare(target.Version, row.NewestVersion) >= 0)
				{
					return null;
				}

				return copy with { FromVersion = target?.Version, ToVersion = row.NewestVersion, SourceVault = row.SourceVaultPath, ExpectedTargetHash = target?.MainHash };
			case DownloadPlugin download:
				PluginInstall? current = snapshot?.Plugins.FirstOrDefault(p => p.Id == download.PluginId)?.Installs
					.FirstOrDefault(i => VaultManager.Core.Foundation.PathRules.Same(i.VaultPath, download.TargetVault));
				if (current?.Version is not null && PluginVersion.Compare(current.Version, download.ToVersion) >= 0)
				{
					return null;
				}

				return download with { FromVersion = current?.Version, ExpectedTargetHash = current?.MainHash };
			default:
				return null;
		}
	}

	/// <summary>No panel when nothing waits, nothing is wrong and nothing can be undone; the vault list takes the room.</summary>
	public static bool ShowPanel(IEnumerable<QueueLine> lines, bool running, bool canUndo) =>
		running || canUndo || lines.Any(l => l.NeedsShowing);
}
