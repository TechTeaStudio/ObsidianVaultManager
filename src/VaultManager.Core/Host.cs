using VaultManager.Core.Catalog;
using VaultManager.Core.Changes;
using VaultManager.Core.Obsidian;
using VaultManager.Core.Profiles;
using VaultManager.Core.Store;
using VaultManager.Core.Sync;

namespace VaultManager.Core;

/// <summary>Where one running app reads and writes. A demo points all of it at a throwaway folder.</summary>
public sealed record AppPaths(string ObsidianConfigDir, string DataDir, ObsidianInstall Install = ObsidianInstall.Standard)
{
	public string DatabaseFile => Path.Combine(DataDir, "vault-manager.db");
	public string ProfilesDir => Path.Combine(DataDir, "profiles");
	public string SnapshotsDir => Path.Combine(DataDir, "snapshots");

	/// <summary>On Linux the app's own data goes to ~/.local/share, the place SpecialFolder.LocalApplicationData names there.</summary>
	public static AppPaths ForThisUser()
	{
		ObsidianConfigLocation obsidian = ObsidianConfigLocator.ForThisUser();
		return new AppPaths(
			obsidian.Dir,
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TechTeaStudio", "VaultManager"),
			obsidian.Install);
	}
}

/// <summary>Everything the window needs, wired once.</summary>
public sealed class CoreHost
{
	public CoreHost(AppPaths paths, IObsidianProcess process, ICatalogClient catalog)
	{
		Paths = paths;
		Process = process;
		Catalog = catalog;
		Directory.CreateDirectory(paths.DataDir);
		Database = new AppDatabase(paths.DatabaseFile);
		Registry = new VaultRegistry(paths.ObsidianConfigDir);
		Scanner = new WorkspaceScanner(Registry, new CommunityPluginsReader(paths.ObsidianConfigDir), process);
		Profiles = new ProfileStore(paths.ProfilesDir);
		Executor = new ChangeExecutor(Database, Registry, process, catalog, paths.SnapshotsDir);
		Sync = new SettingsSync(Database);
		Online = new OnlineChecker(catalog, Database);
	}

	public AppPaths Paths { get; }
	public IObsidianProcess Process { get; }
	public ICatalogClient Catalog { get; }
	public AppDatabase Database { get; }
	public VaultRegistry Registry { get; }
	public WorkspaceScanner Scanner { get; }
	public ProfileStore Profiles { get; }
	public ChangeExecutor Executor { get; }
	public SettingsSync Sync { get; }
	public OnlineChecker Online { get; }
}
