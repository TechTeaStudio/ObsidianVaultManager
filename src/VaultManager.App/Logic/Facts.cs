using VaultManager.Core.Changes;
using VaultManager.Core.Obsidian;

namespace VaultManager.App.Logic;

public enum CellKind { Current, Older, Off, Broken, Empty, Waiting, Offline, Blocked }

/// <summary>What a change does to a plugin's settings in the target vault.</summary>
public enum SettingsFate { NotTouched, Kept, None, Default, Copied }

/// <summary>What the scan says about one plugin, without any words.</summary>
internal static class PluginFacts
{
	public static IReadOnlyList<PluginInstall> Present(PluginRow row) =>
		row.Installs.Where(i => i.HasFolder && i.Version is not null).ToList();

	public static IReadOnlyList<PluginInstall> Behind(PluginRow row) =>
		Present(row).Where(i => PluginVersion.Compare(i.Version, row.NewestVersion) < 0).ToList();

	public static bool IsBehind(PluginRow row, PluginInstall install) =>
		install.HasFolder && install.Version is not null && PluginVersion.Compare(install.Version, row.NewestVersion) < 0;

	public static CellKind Cell(PluginRow row, VaultState vault, bool queued)
	{
		if (!vault.IsOnline)
		{
			return CellKind.Offline;
		}

		if (queued)
		{
			return CellKind.Waiting;
		}

		PluginInstall? install = row.In(vault.Id);
		if (install is null)
		{
			return vault.ListUnreadable || vault.ScanError is not null ? CellKind.Blocked : CellKind.Empty;
		}

		if (!install.HasFolder || install.Version is null)
		{
			return CellKind.Broken;
		}

		if (IsBehind(row, install))
		{
			return CellKind.Older;
		}

		return install.Listed ? CellKind.Current : CellKind.Off;
	}

	public static SettingsFate FateOf(ChangeItem item, PluginInstall? target) => item switch
	{
		CopyPlugin { CopySettings: true } => SettingsFate.Copied,
		CopyPlugin { FromVersion: null } or DownloadPlugin { FromVersion: null } => SettingsFate.Default,
		CopyPlugin or DownloadPlugin when target is null || !target.HasFolder => SettingsFate.Default,
		CopyPlugin or DownloadPlugin => target.HasData ? SettingsFate.Kept : SettingsFate.None,
		_ => SettingsFate.NotTouched,
	};

	public static VaultState? FindVault(WorkspaceSnapshot? snapshot, string path) =>
		snapshot?.Vaults.FirstOrDefault(v => VaultManager.Core.Foundation.PathRules.Same(v.Path, path));

	public static string VaultName(WorkspaceSnapshot? snapshot, string path) =>
		FindVault(snapshot, path)?.DisplayName ?? Path.GetFileName(path.TrimEnd('\\', '/'));

	public static IEnumerable<string> EnabledIn(WorkspaceSnapshot snapshot, string vaultId) =>
		snapshot.Plugins.Where(p => p.In(vaultId) is { Listed: true, HasFolder: true }).Select(p => p.Id);
}
