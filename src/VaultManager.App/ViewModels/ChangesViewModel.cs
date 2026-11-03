using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VaultManager.App.Controls;
using VaultManager.App.Localization;
using VaultManager.App.Logic;
using VaultManager.Core.Changes;
using VaultManager.Core.Foundation;
using VaultManager.Core.Obsidian;
using VaultManager.Core.Store;

namespace VaultManager.App.ViewModels;

internal sealed partial class ChangeLineVm(Guid id, ChangesViewModel owner) : ObservableObject
{
	public Guid Id { get; } = id;
	public string Key => Id.ToString("N");

	[ObservableProperty] string _title = string.Empty;
	[ObservableProperty] string _reason = string.Empty;
	[ObservableProperty] StateKind _glyph;
	[ObservableProperty] bool _canRetry;
	[ObservableProperty] bool _canRemove = true;

	[RelayCommand]
	void Remove() => owner.Remove(Id);

	[RelayCommand]
	Task Retry() => owner.Retry(Id);
}

/// <summary>
/// Runs what a person asked for, right away and one run at a time. The panel in the corner shows only
/// what still waits, what went wrong, and the way back; everything that worked goes to the log.
/// </summary>
internal sealed partial class ChangesViewModel(ShellViewModel shell) : ObservableObject
{
	sealed class SyncProgress(Action<ChangeResult> report) : IProgress<ChangeResult>
	{
		public void Report(ChangeResult value) => report(value);
	}

	public ChangeQueue Queue { get; } = new();

	public ObservableCollection<ChangeLineVm> Lines { get; } = [];

	[ObservableProperty] bool _showPanel;
	[ObservableProperty] bool _isRunning;
	[ObservableProperty] string _runText = string.Empty;
	[ObservableProperty] bool _showUndo;
	[ObservableProperty] string _undoText = string.Empty;
	[ObservableProperty] string _undoProblem = string.Empty;
	[ObservableProperty] bool _hasUndoProblem;

	readonly HashSet<Guid> _handled = [];
	Task _pump = Task.CompletedTask;
	bool _pumping;
	bool _undoing;
	bool _rechecking;
	int _runDone;
	int _runTotal;
	int _batch;
	OperationRow? _lastUndoable;
	readonly DateTimeOffset _started = DateTimeOffset.UtcNow;

	WorkspaceData Data => shell.Data;

	AppDatabase Database => shell.Host.Database;

	/// <summary>Finishes when nothing is running or queued. Lines that wait for a gate do not count.</summary>
	public Task WhenIdle => _pumping ? _pump : Task.CompletedTask;

	public Task Run(ChangeItem item) => Run([item]);

	/// <summary>Asks for changes to run now. During a run they go after it; a request already on its way is not asked twice.</summary>
	public Task Run(IEnumerable<ChangeItem> items)
	{
		foreach (ChangeItem item in items)
		{
			Queue.Add(item);
		}

		shell.Rebuild();
		return Pump();
	}

	Task Pump()
	{
		if (!_pumping && !_undoing)
		{
			_pumping = true;
			_pump = PumpLoop();
		}

		return _pump;
	}

	async Task PumpLoop()
	{
		try
		{
			// Each pass is one operation for Undo. Requests made meanwhile are the next pass.
			while (Queue.NextBatch() is { Count: > 0 } batch)
			{
				await RunBatch(batch);
			}
		}
		finally
		{
			_pumping = false;
			IsRunning = false;
			shell.Rebuild();
		}
	}

	async Task RunBatch(IReadOnlyList<ChangeItem> items)
	{
		IsRunning = true;
		_runDone = 0;
		_runTotal = items.Count;
		_handled.Clear();
		int batch = ++_batch;
		shell.Rebuild();
		bool createdVault = false;
		try
		{
			// A post can land after its run has ended; it must not touch the lines of a later one.
			var progress = new SyncProgress(result => shell.Services.Ui.Post(() =>
			{
				if (batch == _batch)
				{
					OnResult(result);
				}
			}));
			ApplyReport report = await Task.Run(() => shell.Host.Executor.ApplyAsync(items, progress));
			foreach (ChangeResult result in report.Results)
			{
				OnResult(result);
			}

			createdVault = report.Results.Any(r => r.Status == ChangeStatus.Done && items.Any(i => i.Id == r.ItemId && i is CreateVault));
			int done = report.Results.Count(r => r.Status == ChangeStatus.Done);
			int waiting = report.Results.Count(r => r.Status == ChangeStatus.Waiting);
			string summary = Texts.Count(K.Plural.ChangesApplied, done);
			if (waiting > 0)
			{
				summary += ", " + Texts.Count(K.Plural.ChangesWaitingTail, waiting);
			}

			// With nothing done the lines above have said it all, and "0 changes applied" would only hide them in the strip.
			if (done > 0)
			{
				shell.Log.Add(done == report.Results.Count ? LogKind.Ok : LogKind.Plain, summary);
			}
		}
		catch (Exception ex)
		{
			// Whatever is left must not sit on Queued forever, or the loop would run it again at once.
			foreach (ChangeItem item in items.Where(i => Queue.Find(i.Id)?.State == LineState.Queued))
			{
				OnResult(new ChangeResult(item.Id, ChangeStatus.Failed, ChangeFailure.Unexpected));
			}

			shell.Log.Add(LogKind.Problem, Texts.Format(K.Log_ApplyStopped, ex.GetType().Name));
		}

		if (createdVault)
		{
			shell.Log.Add(LogKind.Plain, Texts.Get(K.Log_NewVaultNoUndo));
		}

		RefreshUndo();
		await shell.RescanAsync(log: false);
		Queue.DropFinished();
		shell.Rebuild();
	}

	void OnResult(ChangeResult result)
	{
		// The progress post and the end of the run may arrive in either order; each result counts once.
		if (!_handled.Add(result.ItemId))
		{
			return;
		}

		Queue.Record(result);
		_runDone++;
		QueueLine? line = Queue.Find(result.ItemId);
		if (line is not null)
		{
			LogResult(line.Item, result);
			Persist(line);
		}

		shell.Rebuild();
	}

	// Only a line that still waits outlives the app. One that ran, for good or bad, has had its turn.
	void Persist(QueueLine line)
	{
		string id = line.Item.Id.ToString("N");
		if (line.State == LineState.Waiting)
		{
			Database.SavePending(id, ChangeItemJson.Serialize(line.Item));
		}
		else
		{
			Database.RemovePending(id);
		}
	}

	void LogResult(ChangeItem item, ChangeResult result)
	{
		LineDescription described = Describe(item, result.Note);
		string subject = $"{described.Title} [{TargetName(item)}]";
		switch (result.Status)
		{
			case ChangeStatus.Done:
				string line = $"{subject}: {described.Detail}";
				shell.Log.Add(LogKind.Plain, result.Note == ChangeNote.None ? line : line + ". " + EnumTexts.Of(result.Note));
				break;
			case ChangeStatus.Waiting:
				shell.Log.Add(LogKind.Plain, Texts.Format(K.Log_LineWaiting, subject, EnumTexts.Of(result.Failure)));
				break;
			case ChangeStatus.NeedsReview:
				shell.Log.Add(LogKind.Problem, Texts.Format(K.Log_LineReview, subject, EnumTexts.Of(result.Failure)));
				break;
			default:
				shell.Log.Add(LogKind.Problem, Texts.Format(K.Log_LineFailed, subject, EnumTexts.Of(result.Failure)));
				break;
		}
	}

	public void Remove(Guid id)
	{
		Queue.Remove(id);
		Database.RemovePending(id.ToString("N"));
		shell.Rebuild();
	}

	/// <summary>A failed line runs as it was; one whose vault moved on is made again from the last scan.</summary>
	public Task Retry(Guid id)
	{
		QueueLine? line = Queue.Find(id);
		if (line is null)
		{
			return Task.CompletedTask;
		}

		if (line.State == LineState.NeedsReview)
		{
			if (QueueRules.Renew(line.Item, Data.Snapshot) is not { } fresh)
			{
				return Task.CompletedTask;
			}

			Queue.Renew(id, fresh);
		}
		else
		{
			Queue.Release(id);
		}

		shell.Rebuild();
		return Pump();
	}

	/// <summary>Reads lines that waited through the last run of the app. Their reasons come with the first look at the gates.</summary>
	public void LoadPersisted()
	{
		foreach (PendingRow row in Database.PendingChanges())
		{
			if (ChangeItemJson.TryParse(row.ItemJson) is { } item)
			{
				Queue.Restore(item);
			}
			else
			{
				// Damaged text cannot be run safely, and left in the table it would be read again at every start.
				Database.RemovePending(row.Id);
				shell.Log.Add(LogKind.Problem, Texts.Get(K.Log_PendingDamaged));
			}
		}
	}

	/// <summary>
	/// Asks the executor's own gates about waiting lines and lets go of those that may run. Called from the slow timer,
	/// so it returns before touching anything when nothing waits. The executor asks again before it writes.
	/// </summary>
	public async Task RecheckWaitingAsync()
	{
		IReadOnlyList<QueueLine> waiting = Queue.Waiting();
		if (waiting.Count == 0 || _pumping || _undoing || _rechecking)
		{
			return;
		}

		_rechecking = true;
		try
		{
			List<ChangeItem> items = waiting.Select(l => l.Item).ToList();
			List<ChangeFailure> gates = await Task.Run(() => items.Select(shell.Host.Executor.Gate).ToList());
			bool released = false;
			bool changed = false;
			for (int i = 0; i < items.Count; i++)
			{
				if (Queue.Find(items[i].Id) is not { State: LineState.Waiting } line)
				{
					continue;
				}

				if (gates[i] == ChangeFailure.None)
				{
					Queue.Release(items[i].Id);
					released = true;
				}
				else if (line.Failure != gates[i])
				{
					line.Failure = gates[i];
					changed = true;
				}
			}

			if (released)
			{
				await Pump();
			}
			else if (changed)
			{
				shell.Rebuild();
			}
		}
		finally
		{
			_rechecking = false;
		}
	}

	[RelayCommand]
	public async Task Undo()
	{
		if (_pumping || _undoing)
		{
			return;
		}

		_undoing = true;
		shell.Rebuild();
		IReadOnlyList<(UndoRecord Record, ChangeFailure Failure)> report;
		try
		{
			report = await Task.Run(() => shell.Host.Executor.UndoLast());
		}
		catch (Exception ex)
		{
			report = [];
			shell.Log.Add(LogKind.Problem, Texts.Format(K.Log_Unexpected, ex.GetType().Name));
		}
		finally
		{
			_undoing = false;
		}

		int blocked = 0;
		bool refused = false;
		foreach ((UndoRecord record, ChangeFailure failure) in report)
		{
			string subject = EnumTexts.UndoSubject(record);
			if (failure == ChangeFailure.None)
			{
				shell.Log.Add(LogKind.Plain, Texts.Format(K.Log_Undone, subject));
			}
			else
			{
				// Only a closed door gets retried. A line whose target moved on is gone, and promising another try would be false.
				if (failure is ChangeFailure.VaultOffline or ChangeFailure.VaultOpen or ChangeFailure.ListUnreadable or ChangeFailure.WriteFailed or ChangeFailure.RecoveryNeeded)
				{
					blocked++;
				}

				refused = true;
				shell.Log.Add(LogKind.Problem, Texts.Format(K.Log_UndoBlocked, subject, EnumTexts.Of(failure)));
			}
		}

		UndoProblem = blocked > 0 ? Texts.Format(K.Undo_Blocked, Texts.Count(K.Plural.Lines, blocked)) : string.Empty;
		HasUndoProblem = blocked > 0;
		if (report.Count > 0 && !refused)
		{
			shell.Log.Add(LogKind.Ok, Texts.Get(K.Log_UndoDone));
		}

		RefreshUndo();
		await shell.RescanAsync(log: false);

		// Requests made during the Undo were held for it.
		await Pump();
	}

	public void RefreshUndo() => _lastUndoable = Database.LastUndoable();

	public OperationRow? OlderUndo => _lastUndoable is { } last && last.At < _started ? last : null;

	public void Rebuild()
	{
		List<QueueLine> shown = Queue.Lines.Where(l => l.NeedsShowing).ToList();
		CollectionSync.Sync(Lines, shown, l => l.Key, l => l.Item.Id.ToString("N"), l => new ChangeLineVm(l.Item.Id, this), (_, _) => { });
		foreach (QueueLine line in shown)
		{
			UpdateLine(Lines.First(l => l.Id == line.Item.Id), line);
		}

		RunText = Texts.Format(K.Changes_Updating, Math.Min(_runDone + 1, Math.Max(_runTotal, 1)), Math.Max(_runTotal, 1));
		// Undo records live for weeks; a panel kept up by them would never leave the screen. Older ones wait in the menu.
		ShowUndo = _lastUndoable is { } own && own.At >= _started;
		UndoText = _lastUndoable is { } last
			? Texts.Format(K.Undo_Last, Texts.Count(K.Plural.Updated, last.ItemCount), Formats.Time(last.At))
			: string.Empty;
		ShowPanel = QueueRules.ShowPanel(Queue.Lines, IsRunning, ShowUndo) || HasUndoProblem;
		OnPropertyChanged(nameof(CanUndo));
	}

	public bool CanUndo => !_pumping && !_undoing;

	string TargetName(ChangeItem item) => PluginFacts.VaultName(Data.Snapshot, item.VaultPath);

	void UpdateLine(ChangeLineVm vm, QueueLine line)
	{
		string title = Describe(line.Item, line.Note).Title;
		vm.Title = line.Item is CreateVault or RegisterVault ? title : $"{title} · {TargetName(line.Item)}";
		(vm.Reason, vm.Glyph) = ReasonOf(line);
		vm.CanRemove = !IsRunning;
		vm.CanRetry = !IsRunning && line.State switch
		{
			LineState.Failed => true,
			LineState.NeedsReview => QueueRules.Renew(line.Item, Data.Snapshot) is not null,
			_ => false,
		};
	}

	internal static (string, StateKind) ReasonOf(QueueLine line) => line.State switch
	{
		LineState.Failed => (Texts.Format(K.Result_Failed, EnumTexts.Of(line.Failure)), StateKind.Blocked),
		LineState.Waiting when line.Failure == ChangeFailure.None => (Texts.Get(K.Status_Waiting), StateKind.Waiting),
		LineState.Waiting => (Texts.Format(K.Result_Waiting, EnumTexts.Of(line.Failure)), StateKind.Waiting),
		LineState.NeedsReview => (Texts.Format(K.Result_Review, EnumTexts.Of(line.Failure)), StateKind.Review),
		_ => (string.Empty, StateKind.None),
	};

	internal sealed record LineDescription(string Title, string Detail);

	LineDescription Describe(ChangeItem item, ChangeNote note)
	{
		WorkspaceSnapshot? snapshot = Data.Snapshot;
		PluginInstall? Target(string id, string vault) => snapshot?.Plugins.FirstOrDefault(p => p.Id == id)?.Installs
			.FirstOrDefault(i => VaultManager.Core.Foundation.PathRules.Same(i.VaultPath, vault));

		string From(string vault) => Texts.Format(K.Line_From, PluginFacts.VaultName(snapshot, vault));

		switch (item)
		{
			case CopyPlugin copy:
				string versions = copy.FromVersion is null ? Texts.Format(K.Line_Add, copy.ToVersion) : $"{copy.FromVersion} → {copy.ToVersion}";
				SettingsFate fate = note == ChangeNote.SettingsSkippedSecret ? SettingsFate.Default : PluginFacts.FateOf(copy, Target(copy.PluginId, copy.TargetVault));
				return new LineDescription(copy.PluginName, $"{versions} · {EnumTexts.Of(fate)} · {From(copy.SourceVault)}");
			case DownloadPlugin download:
				string to = download.FromVersion is null ? Texts.Format(K.Line_Add, download.ToVersion) : $"{download.FromVersion} → {download.ToVersion}";
				return new LineDescription(download.PluginName, $"{to} · {EnumTexts.Of(PluginFacts.FateOf(download, Target(download.PluginId, download.TargetVault)))} · {Texts.Format(K.Line_Download, Formats.Size(download.SizeBytes))}");
			case SetPluginEnabled toggle:
				return new LineDescription(toggle.PluginName, $"{Texts.Get(toggle.Enabled ? K.Line_TurnOn : K.Line_TurnOff)} · {Texts.Get(K.Line_FilesStay)}");
			case CopySettingsFile settings:
				return new LineDescription(settings.RelativePath, $"{Texts.Get(settings.ExpectedTargetHash is null ? K.Line_SettingsNew : K.Line_SettingsReplace)} · {From(settings.SourceVault)}");
			case AddProfileParts parts:
				return new LineDescription(Texts.Format(K.Line_Parts, parts.Profile.Name), Texts.Get(K.Line_PartsDetail));
			case CreateVault create:
				return new LineDescription(create.Name, create.Profile is null
					? Texts.Get(K.Line_EmptyVault)
					: Texts.Count(K.Plural.Plugins, create.PluginSources.Count) + ", " + Texts.Count(K.Plural.Folders, create.Profile.Folders.Count));
			case RegisterVault register:
				return new LineDescription(Path.GetFileName(register.NewVault.TrimEnd('\\', '/')), Texts.Get(K.Line_Register));
			default:
				return new LineDescription(item.VaultPath, string.Empty);
		}
	}
}
