using VaultManager.Core.Changes;
using VaultManager.Core.Obsidian;
using VaultManager.Core.Profiles;
using VaultManager.Core.Store;
using VaultManager.Core.Sync;

namespace VaultManager.App.Logic;

internal sealed record ProfileFill(IReadOnlyList<ChangeItem> Items, IReadOnlyList<string> Unavailable);

/// <summary>Builds change lines from what a scan saw. Running them is the caller's step.</summary>
internal static class Plans
{
	public static IEnumerable<CopyPlugin> UpdateBehind(PluginRow row, WorkspaceSnapshot snapshot)
	{
		if (row.NewestVersion is null || row.SourceVaultPath is null)
		{
			yield break;
		}

		foreach (PluginInstall install in PluginFacts.Behind(row))
		{
			VaultState? vault = snapshot.Vaults.FirstOrDefault(v => v.Id == install.VaultId);
			if (vault is { ListUnreadable: true })
			{
				continue;
			}

			yield return new CopyPlugin(install.VaultPath, row.Id, row.Name, install.Version, row.NewestVersion, row.SourceVaultPath, false, install.MainHash);
		}
	}

	public static CopyPlugin? UpdateOne(PluginRow row, PluginInstall install) =>
		row.NewestVersion is null || row.SourceVaultPath is null
			? null
			: new CopyPlugin(install.VaultPath, row.Id, row.Name, install.Version, row.NewestVersion, row.SourceVaultPath, false, install.MainHash);

	public static CopyPlugin? AddTo(PluginRow row, string vaultPath, bool copySettings)
	{
		if (row.NewestVersion is null || row.SourceVaultPath is null || VaultManager.Core.Foundation.PathRules.Same(row.SourceVaultPath, vaultPath))
		{
			return null;
		}

		PluginInstall? target = row.Installs.FirstOrDefault(i => VaultManager.Core.Foundation.PathRules.Same(i.VaultPath, vaultPath));
		return new CopyPlugin(vaultPath, row.Id, row.Name, target?.Version, row.NewestVersion, row.SourceVaultPath, copySettings, target?.MainHash);
	}

	public static DownloadPlugin Download(CachedCatalogEntry entry, string version, long size, string vaultPath, PluginInstall? target) =>
		new(vaultPath, entry.Id, entry.Name, target?.Version, version, entry.Repo, size, target?.MainHash);

	public static ProfileFill Fill(Profile profile, ProfileAudit audit, VaultState vault, WorkspaceSnapshot snapshot)
	{
		var items = new List<ChangeItem>();
		var unavailable = new List<string>();
		foreach (string id in audit.MissingPlugins)
		{
			PluginRow? row = snapshot.Plugins.FirstOrDefault(p => p.Id == id);
			PluginInstall? install = row?.In(vault.Id);
			if (row is not null && install is { HasFolder: true, Listed: false })
			{
				// The files are already there: turning it on costs nothing and keeps its settings.
				items.Add(new SetPluginEnabled(vault.Path, id, row.Name, true));
				continue;
			}

			CopyPlugin? copy = row is null ? null : AddTo(row, vault.Path, false);
			if (copy is null)
			{
				unavailable.Add(id);
				continue;
			}

			items.Add(copy);
		}

		if (audit.MissingFolders.Count > 0 || audit.VaultMapMissing)
		{
			items.Add(new AddProfileParts(vault.Path, profile));
		}

		return new ProfileFill(items, unavailable);
	}

	public static IReadOnlyDictionary<string, string> PluginSources(Profile? profile, WorkspaceSnapshot? snapshot)
	{
		var sources = new Dictionary<string, string>(StringComparer.Ordinal);
		if (profile is null || snapshot is null)
		{
			return sources;
		}

		foreach (string id in profile.Plugins)
		{
			PluginRow? row = snapshot.Plugins.FirstOrDefault(p => p.Id == id);
			if (row?.SourceVaultPath is not null)
			{
				sources[id] = row.SourceVaultPath;
			}
		}

		return sources;
	}
}

public enum AuditResult { Offline, Matches, Missing, NotUsing, UsesOther, Unknown }

/// <summary>The one plain answer a vault gets on the Profiles page. Pure, so the wording rule can be tested.</summary>
internal static class AuditRules
{
	public static AuditResult Of(ProfileAudit? audit, bool isOnline, bool boundHere, bool boundElsewhere)
	{
		if (!isOnline)
		{
			return AuditResult.Offline;
		}

		if (audit is null)
		{
			return AuditResult.Unknown;
		}

		if (audit.IsComplete)
		{
			return AuditResult.Matches;
		}

		return boundHere ? AuditResult.Missing : boundElsewhere ? AuditResult.UsesOther : AuditResult.NotUsing;
	}
}

public enum ConflictChoice { None, TakeSource, KeepTarget }

internal static class SyncRules
{
	public static bool StartsSelected(SyncDecision decision) => decision == SyncDecision.Copy;

	public static bool WillQueue(SyncDecision decision, bool selected, ConflictChoice choice) => decision switch
	{
		SyncDecision.Copy => selected,
		SyncDecision.Conflict => choice == ConflictChoice.TakeSource,
		_ => false,
	};
}

public enum NewVaultProblem { None, EmptyName, BadName, NameTaken, EmptyParent, RelativeParent, OutsideDemo, FolderNotEmpty }

internal static class NewVaultRules
{
	public static NewVaultProblem Check(string name, string parent, WorkspaceSnapshot? snapshot, bool folderHasFiles, string? confinedTo)
	{
		string trimmed = name.Trim();
		if (trimmed.Length == 0)
		{
			return NewVaultProblem.EmptyName;
		}

		if (trimmed.IndexOfAny(['/', '\\']) >= 0 || !ProfileValidator.IsSafeRelative(trimmed))
		{
			return NewVaultProblem.BadName;
		}

		if (snapshot?.Vaults.Any(v => string.Equals(Path.GetFileName(v.Path.TrimEnd('\\', '/')), trimmed, VaultManager.Core.Foundation.PathRules.Comparison)) == true)
		{
			return NewVaultProblem.NameTaken;
		}

		string folder = parent.Trim();
		if (folder.Length == 0)
		{
			return NewVaultProblem.EmptyParent;
		}

		if (!Path.IsPathFullyQualified(folder))
		{
			return NewVaultProblem.RelativeParent;
		}

		if (confinedTo is not null && !IsInside(folder, confinedTo))
		{
			return NewVaultProblem.OutsideDemo;
		}

		return folderHasFiles ? NewVaultProblem.FolderNotEmpty : NewVaultProblem.None;
	}

	static bool IsInside(string path, string root)
	{
		string full = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
		string top = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
		return full.StartsWith(top, VaultManager.Core.Foundation.PathRules.Comparison);
	}
}

internal static class ProfileNames
{
	public static string Free(string wanted, IEnumerable<string> taken)
	{
		var names = new HashSet<string>(taken, VaultManager.Core.Foundation.PathRules.Comparer);
		string name = wanted;
		for (int n = 2; names.Contains(name); n++)
		{
			name = $"{wanted} {n}";
		}

		return name;
	}
}

internal static class PluginFilter
{
	/// <summary>Anywhere in the name counts; a match at the start of a word sorts first, the shorter name winning a tie.</summary>
	public static IEnumerable<T> Apply<T>(IEnumerable<T> items, string filter, Func<T, string> name)
	{
		string query = filter.Trim();
		if (query.Length == 0)
		{
			return items;
		}

		return items
			.Select(i => (Item: i, Name: name(i), Rank: Rank(name(i), query)))
			.Where(x => x.Rank >= 0)
			.OrderBy(x => x.Rank)
			.ThenBy(x => x.Name.Length)
			.Select(x => x.Item);
	}

	static int Rank(string name, string query)
	{
		int at = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
		if (at < 0)
		{
			return -1;
		}

		return at == 0 || !char.IsLetterOrDigit(name[at - 1]) ? 0 : 1;
	}
}

internal static class PathText
{
	/// <summary>Both ends of a path say the most: the drive and the vault. The middle goes first.</summary>
	public static string Middle(string path, int max)
	{
		if (path.Length <= max || max < 8)
		{
			return path;
		}

		const string dots = "...";
		int room = max - dots.Length;
		int left = room * 2 / 5;
		return path[..left] + dots + path[^(room - left)..];
	}
}

internal static class Links
{
	public static string OpenVault(string vaultPath) =>
		"obsidian://open?vault=" + Uri.EscapeDataString(Path.GetFileName(vaultPath.TrimEnd('\\', '/')));

	public static string CatalogPage(string pluginId) => "https://community.obsidian.md/plugins/" + Uri.EscapeDataString(pluginId);
}

internal sealed record StartOptions(bool Demo, bool Smoke)
{
	public static StartOptions Parse(IReadOnlyList<string> args) => new(
		args.Contains("--demo", StringComparer.OrdinalIgnoreCase),
		args.Contains("--smoke", StringComparer.OrdinalIgnoreCase));
}
