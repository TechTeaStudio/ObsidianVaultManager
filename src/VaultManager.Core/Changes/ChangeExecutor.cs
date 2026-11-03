using Newtonsoft.Json;
using VaultManager.Core.Catalog;
using VaultManager.Core.Foundation;
using VaultManager.Core.Obsidian;
using VaultManager.Core.Profiles;
using VaultManager.Core.Store;

namespace VaultManager.Core.Changes;

public sealed record CreatedFile(string Path, string Hash);

/// <summary>What it takes to put one change back. Stored as JSON so undo survives a restart.</summary>
public sealed record UndoRecord
{
	public required string Kind { get; init; }
	public required string Vault { get; init; }
	public string? PluginId { get; init; }
	public string? SnapshotDir { get; init; }
	public bool WasListed { get; init; }
	public string? RelativePath { get; init; }

	/// <summary>The state right after the change: if the disk no longer shows it, someone else moved on and undo stays out.</summary>
	public string? AfterHash { get; init; }
	public string? AfterDataHash { get; init; }

	public string? SourceVault { get; init; }
	public string? PreviousSyncBase { get; init; }
	public IReadOnlyList<CreatedFile>? CreatedFiles { get; init; }
	public IReadOnlyList<string>? CreatedFolders { get; init; }
}

public sealed record ApplyReport(IReadOnlyList<ChangeResult> Results, string? OperationId);

/// <summary>
/// Runs a plan one line at a time. Each line either lands whole or leaves the vault as it was,
/// and each line reports for itself, so one failed drive does not hide what happened to the rest.
/// </summary>
public sealed class ChangeExecutor(
	AppDatabase database,
	VaultRegistry registry,
	IObsidianProcess process,
	ICatalogClient catalog,
	string snapshotsRoot,
	int keepOperations = 20)
{
	const string StagingFolder = ".vault-manager-staging";

	// A new build replaces these; anything else in a plugin's folder belongs to the user or to the plugin.
	static readonly HashSet<string> BuildFiles = new(StringComparer.OrdinalIgnoreCase) { VaultLayout.MainFile, VaultLayout.ManifestFile, "styles.css", VaultLayout.DataFile };

	public async Task<ApplyReport> ApplyAsync(IReadOnlyList<ChangeItem> items, IProgress<ChangeResult>? progress = null, CancellationToken cancellationToken = default)
	{
		string operationId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..28];
		string operationDir = Path.Combine(snapshotsRoot, operationId);
		var results = new List<ChangeResult>();
		var undo = new List<string>();
		var failedCreates = new Dictionary<string, ChangeFailure>(PathRules.Comparer);
		bool keepSnapshots = false;

		try
		{
			foreach (ChangeItem item in items)
			{
				cancellationToken.ThrowIfCancellationRequested();
				ChangeResult result;
				try
				{
					ChangeFailure gate = Gate(item);
					if (item is RegisterVault register && failedCreates.TryGetValue(register.NewVault, out ChangeFailure createFailure))
					{
						// Registering a folder the create step refused would turn someone else's folder into a vault.
						result = new ChangeResult(item.Id, ChangeStatus.Failed, createFailure);
					}
					else if (gate != ChangeFailure.None)
					{
						result = new ChangeResult(item.Id, ChangeStatus.Waiting, gate);
					}
					else
					{
						(UndoRecord? record, ChangeNote note) = await RunAsync(item, Path.Combine(operationDir, item.Id.ToString("N")), cancellationToken).ConfigureAwait(false);
						if (record is not null)
						{
							undo.Add(JsonConvert.SerializeObject(record));
						}

						result = new ChangeResult(item.Id, ChangeStatus.Done, Note: note);
					}
				}
				catch (ChangeRefusedException ex)
				{
					keepSnapshots |= ex.Failure == ChangeFailure.RecoveryNeeded;
					result = new ChangeResult(item.Id, ex.Failure is ChangeFailure.TargetChanged or ChangeFailure.SourceChanged ? ChangeStatus.NeedsReview : ChangeStatus.Failed, ex.Failure);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					// A drive that dropped mid-write lands here; the swap already restored what it could.
					result = new ChangeResult(item.Id, ChangeStatus.Failed, ChangeFailure.WriteFailed);
				}
				catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
				{
					// HttpClient reports its own timeout as a cancellation nobody asked for.
					result = new ChangeResult(item.Id, ChangeStatus.Failed, ChangeFailure.DownloadFailed);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					// One line's surprise must not take the other lines' results and undo records with it.
					result = new ChangeResult(item.Id, ChangeStatus.Failed, ChangeFailure.Unexpected);
				}

				if (item is CreateVault create && result.Status != ChangeStatus.Done)
				{
					failedCreates[create.NewVaultPath] = result.Failure;
				}

				results.Add(result);
				progress?.Report(result);
			}
		}
		finally
		{
			// Also on cancellation: lines that already ran must stay undoable.
			SaveJournal(operationId, operationDir, undo, keepSnapshots);
		}

		return new ApplyReport(results, undo.Count == 0 ? null : operationId);
	}

	void SaveJournal(string operationId, string operationDir, List<string> undo, bool keepSnapshots)
	{
		if (undo.Count == 0)
		{
			// A failed put-back leaves this folder as the only copy of the old plugin.
			if (!keepSnapshots)
			{
				Folder.Delete(operationDir);
			}

			return;
		}

		database.SaveOperation(operationId, DateTimeOffset.UtcNow, undo);
		foreach (string old in database.PruneOperations(keepOperations))
		{
			Folder.Delete(Path.Combine(snapshotsRoot, old));
		}
	}

	/// <summary>
	/// Puts back the last operation. Lines blocked by a closed door (an open vault, a dropped drive) stay, and the next
	/// Undo retries them. A line whose target moved on since Apply is refused and dropped: undoing it would destroy newer work.
	/// </summary>
	public IReadOnlyList<(UndoRecord Record, ChangeFailure Failure)> UndoLast()
	{
		OperationRow? operation = database.LastUndoable();
		if (operation is null)
		{
			return [];
		}

		var remaining = new List<string>();
		var report = new List<(UndoRecord, ChangeFailure)>();
		foreach (string json in database.UndoRecords(operation.Id))
		{
			UndoRecord record = JsonConvert.DeserializeObject<UndoRecord>(json)!;
			ChangeFailure failure = GateVault(record.Vault);
			bool retry = failure != ChangeFailure.None;
			if (!retry)
			{
				try
				{
					Revert(record);
				}
				catch (ChangeRefusedException ex)
				{
					failure = ex.Failure;
					retry = ex.Failure is ChangeFailure.ListUnreadable or ChangeFailure.RecoveryNeeded;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					failure = ChangeFailure.WriteFailed;
					retry = true;
				}
			}

			if (retry)
			{
				remaining.Add(json);
			}

			report.Add((record, failure));
		}

		database.ReplaceUndoRecords(operation.Id, remaining);
		if (remaining.Count == 0)
		{
			Folder.Delete(Path.Combine(snapshotsRoot, operation.Id));
		}

		return report;
	}

	/// <summary>The questions asked before a line runs. Reads the disk, so a caller on the UI thread goes through a task.</summary>
	public ChangeFailure Gate(ChangeItem item) => item switch
	{
		CreateVault => ChangeFailure.None,
		RegisterVault => process.IsRunning ? ChangeFailure.ObsidianRunning : ChangeFailure.None,
		_ => GateVault(item.VaultPath),
	};

	ChangeFailure GateVault(string vault)
	{
		if (!Directory.Exists(vault))
		{
			return ChangeFailure.VaultOffline;
		}

		// Obsidian rewrites its config when a vault closes, which would undo a change made while it is open.
		if (!process.IsRunning)
		{
			return ChangeFailure.None;
		}

		// With Obsidian running and its list unreadable, nothing proves the vault is closed, so it counts as open.
		ReadResult<IReadOnlyList<RegistryEntry>> entries = registry.Read();
		bool mayBeOpen = entries.Status == ReadStatus.Unreadable
			|| entries.Value?.Any(e => e.Open && PathRules.Same(e.Path, vault)) == true;
		return mayBeOpen ? ChangeFailure.VaultOpen : ChangeFailure.None;
	}

	async Task<(UndoRecord?, ChangeNote)> RunAsync(ChangeItem item, string snapshotDir, CancellationToken cancellationToken)
	{
		switch (item)
		{
			case CopyPlugin copy:
				return RunCopy(copy, snapshotDir);
			case DownloadPlugin download:
				return (await RunDownloadAsync(download, snapshotDir, cancellationToken).ConfigureAwait(false), ChangeNote.None);
			case SetPluginEnabled toggle:
				return (RunToggle(toggle), ChangeNote.None);
			case CopySettingsFile settings:
				return (RunSettings(settings, snapshotDir), ChangeNote.None);
			case AddProfileParts parts:
				return (RunParts(parts), ChangeNote.None);
			case CreateVault create:
				VaultBuilder.Create(create);
				return (null, ChangeNote.None);
			case RegisterVault register:
				if (!Directory.Exists(VaultLayout.ConfigDir(register.NewVault)))
				{
					throw new ChangeRefusedException(ChangeFailure.SourceMissing);
				}

				registry.Register(register.NewVault);
				return (null, ChangeNote.None);
			default:
				throw new NotSupportedException(item.GetType().Name);
		}
	}

	(UndoRecord, ChangeNote) RunCopy(CopyPlugin item, string snapshotDir)
	{
		string source = VaultLayout.PluginDir(item.SourceVault, item.PluginId);
		PluginManifest? manifest = PluginManifest.TryRead(source);
		if (manifest is null)
		{
			throw new ChangeRefusedException(ChangeFailure.SourceMissing);
		}

		if (PluginVersion.Compare(manifest.Version, item.ToVersion) != 0)
		{
			throw new ChangeRefusedException(ChangeFailure.SourceChanged);
		}

		ChangeNote note = ChangeNote.None;
		bool copySettings = item.CopySettings && File.Exists(Path.Combine(source, VaultLayout.DataFile));
		if (copySettings && SecretScanner.FindKeysInFile(Path.Combine(source, VaultLayout.DataFile)).Count > 0)
		{
			copySettings = false;
			note = ChangeNote.SettingsSkippedSecret;
		}

		UndoRecord record = Install(item.TargetVault, item.PluginId, item.ExpectedTargetHash, snapshotDir, staging =>
		{
			Folder.Copy(source, staging, relative => IsPluginNoise(relative) || relative == VaultLayout.DataFile);
			if (copySettings)
			{
				File.Copy(Path.Combine(source, VaultLayout.DataFile), Path.Combine(staging, VaultLayout.DataFile));
			}

			string main = Path.Combine(source, VaultLayout.MainFile);
			if (File.Exists(main) && SharedFile.Hash(main) != SharedFile.Hash(Path.Combine(staging, VaultLayout.MainFile)))
			{
				throw new ChangeRefusedException(ChangeFailure.VerifyFailed);
			}
		}, keepTargetSettings: !copySettings);
		return (record, note);
	}

	async Task<UndoRecord> RunDownloadAsync(DownloadPlugin item, string snapshotDir, CancellationToken cancellationToken)
	{
		string temp = Path.Combine(Path.GetTempPath(), "vault-manager-" + Guid.NewGuid().ToString("N"));
		try
		{
			await catalog.DownloadReleaseAsync(item.Repo, item.ToVersion, temp, cancellationToken).ConfigureAwait(false);
			PluginManifest? manifest = PluginManifest.TryRead(temp);

			// The latest version is read from the repository head, the files from a release tag; they can disagree.
			if (manifest is null
				|| !string.Equals(manifest.Id, item.PluginId, StringComparison.Ordinal)
				|| PluginVersion.Compare(manifest.Version, item.ToVersion) != 0)
			{
				throw new ChangeRefusedException(ChangeFailure.VerifyFailed);
			}

			return Install(item.TargetVault, item.PluginId, item.ExpectedTargetHash, snapshotDir, staging => Folder.Copy(temp, staging), keepTargetSettings: true);
		}
		finally
		{
			Folder.Delete(temp);
		}
	}

	/// <summary>Builds the new folder beside the old one, then swaps, so a failure leaves the old plugin running.</summary>
	static UndoRecord Install(string vault, string pluginId, string? expectedHash, string snapshotDir, Action<string> fill, bool keepTargetSettings)
	{
		string target = VaultLayout.PluginDir(vault, pluginId);
		if (HashOrNull(Path.Combine(target, VaultLayout.MainFile)) != expectedHash)
		{
			throw new ChangeRefusedException(ChangeFailure.TargetChanged);
		}

		ReadResult<IReadOnlyList<string>> list = CommunityPluginList.Read(vault);
		if (list.Status == ReadStatus.Unreadable)
		{
			throw new ChangeRefusedException(ChangeFailure.ListUnreadable);
		}

		bool wasListed = list.Value?.Contains(pluginId, StringComparer.Ordinal) == true;
		string? snapshot = null;
		if (Directory.Exists(target))
		{
			snapshot = Path.Combine(snapshotDir, "plugin");
			Folder.Copy(target, snapshot);
		}

		Swap(vault, target, fresh =>
		{
			fill(fresh);
			string targetData = Path.Combine(target, VaultLayout.DataFile);
			if (keepTargetSettings && File.Exists(targetData))
			{
				File.Copy(targetData, Path.Combine(fresh, VaultLayout.DataFile), overwrite: true);
			}

			CarryOwnFiles(target, fresh);
		}, () => CommunityPluginList.Set(vault, pluginId, enabled: true));

		return new UndoRecord
		{
			Kind = "plugin",
			Vault = vault,
			PluginId = pluginId,
			SnapshotDir = snapshot,
			WasListed = wasListed,
			AfterHash = HashOrNull(Path.Combine(target, VaultLayout.MainFile)),
			AfterDataHash = HashOrNull(Path.Combine(target, VaultLayout.DataFile)),
		};
	}

	/// <summary>Obsidian's own update replaces only the build files, so a plugin's history or a developer's .git stays.</summary>
	static void CarryOwnFiles(string target, string fresh)
	{
		if (!Directory.Exists(target))
		{
			return;
		}

		foreach (string file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
		{
			string relative = Path.GetRelativePath(target, file);
			string destination = Path.Combine(fresh, relative);
			if (!BuildFiles.Contains(relative) && !File.Exists(destination))
			{
				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				File.WriteAllBytes(destination, SharedFile.ReadAllBytes(file));
			}
		}
	}

	/// <summary>The one place a plugin folder gets replaced. If even putting the old one back fails, its copy in staging stays on disk.</summary>
	static void Swap(string vault, string target, Action<string> fill, Action afterSwap)
	{
		string stagingRoot = Path.Combine(VaultLayout.ConfigDir(vault), StagingFolder, Guid.NewGuid().ToString("N"));
		string fresh = Path.Combine(stagingRoot, "new");
		string retired = Path.Combine(stagingRoot, "old");
		bool keepStaging = false;
		Directory.CreateDirectory(stagingRoot);
		try
		{
			fill(fresh);
			Directory.CreateDirectory(VaultLayout.PluginsDir(vault));
			bool hadOld = Directory.Exists(target);
			if (hadOld)
			{
				Directory.Move(target, retired);
			}

			try
			{
				Directory.Move(fresh, target);
				afterSwap();
			}
			catch
			{
				if (!TryPutBack(target, retired, hadOld))
				{
					keepStaging = true;
					throw new ChangeRefusedException(ChangeFailure.RecoveryNeeded);
				}

				throw;
			}
		}
		finally
		{
			if (!keepStaging)
			{
				Folder.Delete(stagingRoot);
				DeleteIfEmpty(Path.Combine(VaultLayout.ConfigDir(vault), StagingFolder));
			}
		}
	}

	internal static bool TryPutBack(string target, string retired, bool hadOld)
	{
		try
		{
			Folder.Delete(target);
			if (hadOld)
			{
				Directory.Move(retired, target);
			}

			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	static UndoRecord RunToggle(SetPluginEnabled item)
	{
		ReadResult<IReadOnlyList<string>> list = CommunityPluginList.Read(item.TargetVault);
		bool wasListed = list.Value?.Contains(item.PluginId, StringComparer.Ordinal) == true;
		CommunityPluginList.Set(item.TargetVault, item.PluginId, item.Enabled);
		return new UndoRecord { Kind = "toggle", Vault = item.TargetVault, PluginId = item.PluginId, WasListed = wasListed };
	}

	UndoRecord RunSettings(CopySettingsFile item, string snapshotDir)
	{
		string relative = item.RelativePath.Replace('/', Path.DirectorySeparatorChar);
		string source = Path.Combine(VaultLayout.ConfigDir(item.SourceVault), relative);
		string target = Path.Combine(VaultLayout.ConfigDir(item.TargetVault), relative);
		if (!File.Exists(source))
		{
			throw new ChangeRefusedException(ChangeFailure.SourceMissing);
		}

		if (SharedFile.Hash(source) != item.SourceHash)
		{
			throw new ChangeRefusedException(ChangeFailure.SourceChanged);
		}

		string? current = HashOrNull(target);
		if (current != item.ExpectedTargetHash)
		{
			throw new ChangeRefusedException(ChangeFailure.TargetChanged);
		}

		if (relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && SecretScanner.FindKeysInFile(source).Count > 0)
		{
			throw new ChangeRefusedException(ChangeFailure.SecretInSettings);
		}

		string? snapshot = null;
		if (current is not null)
		{
			snapshot = Path.Combine(snapshotDir, "settings", relative);
			Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
			File.Copy(target, snapshot);
		}

		string? previousBase = database.SyncBase(item.SourceVault, item.TargetVault, item.RelativePath);
		AtomicFile.Copy(source, target);
		database.SetSyncBase(item.SourceVault, item.TargetVault, item.RelativePath, item.SourceHash);
		return new UndoRecord
		{
			Kind = "settings",
			Vault = item.TargetVault,
			RelativePath = item.RelativePath,
			SnapshotDir = snapshot,
			AfterHash = item.SourceHash,
			SourceVault = item.SourceVault,
			PreviousSyncBase = previousBase,
		};
	}

	static UndoRecord RunParts(AddProfileParts item)
	{
		(IReadOnlyList<string> files, IReadOnlyList<string> folders) = VaultBuilder.AddParts(item.TargetVault, item.Profile);
		return new UndoRecord
		{
			Kind = "parts",
			Vault = item.TargetVault,
			CreatedFiles = files.Select(f => new CreatedFile(f, SharedFile.Hash(Path.Combine(item.TargetVault, f)))).ToList(),
			CreatedFolders = folders,
		};
	}

	void Revert(UndoRecord record)
	{
		switch (record.Kind)
		{
			case "plugin":
				RevertPlugin(record);
				break;
			case "toggle":
				CommunityPluginList.Set(record.Vault, record.PluginId!, record.WasListed);
				break;
			case "settings":
				RevertSettings(record);
				break;
			case "parts":
				RevertParts(record);
				break;
		}
	}

	void RevertPlugin(UndoRecord record)
	{
		string target = VaultLayout.PluginDir(record.Vault, record.PluginId!);
		string targetData = Path.Combine(target, VaultLayout.DataFile);
		if (HashOrNull(Path.Combine(target, VaultLayout.MainFile)) != record.AfterHash)
		{
			throw new ChangeRefusedException(ChangeFailure.TargetChanged);
		}

		if (CommunityPluginList.Read(record.Vault).Status == ReadStatus.Unreadable)
		{
			throw new ChangeRefusedException(ChangeFailure.ListUnreadable);
		}

		bool settingsMovedOn = File.Exists(targetData) && SharedFile.Hash(targetData) != record.AfterDataHash;
		if (record.SnapshotDir is null)
		{
			if (settingsMovedOn)
			{
				// Removing a plugin the user has since tuned would take the tuning with it, so that survives on the side.
				string kept = Path.Combine(snapshotsRoot, "undone", $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{record.PluginId}-data.json");
				Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
				File.Copy(targetData, kept);
			}

			Folder.Delete(target);
		}
		else
		{
			if (!Directory.Exists(record.SnapshotDir))
			{
				throw new ChangeRefusedException(ChangeFailure.SourceMissing);
			}

			Swap(record.Vault, target, fresh =>
			{
				Folder.Copy(record.SnapshotDir, fresh);
				if (settingsMovedOn)
				{
					// Settings written after Apply are newer than the snapshot, and they are the user's.
					File.Copy(targetData, Path.Combine(fresh, VaultLayout.DataFile), overwrite: true);
				}
			}, () => { });
		}

		CommunityPluginList.Set(record.Vault, record.PluginId!, record.WasListed);
	}

	void RevertSettings(UndoRecord record)
	{
		string file = Path.Combine(VaultLayout.ConfigDir(record.Vault), record.RelativePath!.Replace('/', Path.DirectorySeparatorChar));
		if (HashOrNull(file) != record.AfterHash)
		{
			throw new ChangeRefusedException(ChangeFailure.TargetChanged);
		}

		if (record.SnapshotDir is not null)
		{
			if (!File.Exists(record.SnapshotDir))
			{
				throw new ChangeRefusedException(ChangeFailure.SourceMissing);
			}

			AtomicFile.Copy(record.SnapshotDir, file);
		}
		else
		{
			File.Delete(file);
		}

		// The sync history goes back too, or the next Compare would read the restored file as the target's own edit.
		if (record.SourceVault is not null)
		{
			database.RestoreSyncBase(record.SourceVault, record.Vault, record.RelativePath, record.PreviousSyncBase);
		}
	}

	static void RevertParts(UndoRecord record)
	{
		bool keptEdited = false;
		foreach (CreatedFile created in record.CreatedFiles ?? [])
		{
			string path = Path.Combine(record.Vault, created.Path);
			if (!File.Exists(path))
			{
				continue;
			}

			if (SharedFile.Hash(path) == created.Hash)
			{
				File.Delete(path);
			}
			else
			{
				// The user has written in it since. It is theirs now.
				keptEdited = true;
			}
		}

		foreach (string relative in (record.CreatedFolders ?? []).Reverse())
		{
			DeleteIfEmpty(Path.Combine(record.Vault, relative));
		}

		if (keptEdited)
		{
			throw new ChangeRefusedException(ChangeFailure.TargetChanged);
		}
	}

	static string? HashOrNull(string path) => File.Exists(path) ? SharedFile.Hash(path) : null;

	// A developer's plugin folder can hold its whole repository; none of it belongs in another vault.
	static bool IsPluginNoise(string relative) =>
		relative.StartsWith(".git", StringComparison.OrdinalIgnoreCase)
		|| relative.StartsWith("node_modules", StringComparison.OrdinalIgnoreCase);

	static void DeleteIfEmpty(string path)
	{
		if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
		{
			Directory.Delete(path);
		}
	}
}
