namespace VaultManager.Core.Obsidian;

public enum HostOs { Windows, Linux, MacOS }

/// <summary>How Obsidian was installed, which decides where it keeps its own settings.</summary>
public enum ObsidianInstall { Standard, Flatpak, Snap }

public sealed record ObsidianConfigLocation(string Dir, ObsidianInstall Install);

/// <summary>
/// Finds Obsidian's settings folder, the one with obsidian.json and the browser store. Flatpak and Snap keep it inside
/// their sandbox, away from ~/.config, so on Linux each place is tried. Checked against Obsidian's documented
/// locations, not against a real Flatpak or Snap install.
/// </summary>
public static class ObsidianConfigLocator
{
	public static HostOs Current =>
		OperatingSystem.IsWindows() ? HostOs.Windows : OperatingSystem.IsMacOS() ? HostOs.MacOS : HostOs.Linux;

	public static IReadOnlyList<ObsidianConfigLocation> Candidates(HostOs os, string appData, string home, string? xdgConfigHome) => os switch
	{
		HostOs.Windows => [new(Path.Combine(appData, "obsidian"), ObsidianInstall.Standard)],
		HostOs.MacOS => [new(Path.Combine(home, "Library", "Application Support", "obsidian"), ObsidianInstall.Standard)],
		_ =>
		[
			new(Path.Combine(string.IsNullOrEmpty(xdgConfigHome) ? Path.Combine(home, ".config") : xdgConfigHome, "obsidian"), ObsidianInstall.Standard),
			new(Path.Combine(home, ".var", "app", "md.obsidian.Obsidian", "config", "obsidian"), ObsidianInstall.Flatpak),
			new(Path.Combine(home, "snap", "obsidian", "current", ".config", "obsidian"), ObsidianInstall.Snap),
		],
	};

	/// <summary>The place whose vault list was written last, so a second, forgotten install does not win. None found: the standard place.</summary>
	public static ObsidianConfigLocation Find(IReadOnlyList<ObsidianConfigLocation> candidates)
	{
		ObsidianConfigLocation? best = null;
		DateTime newest = DateTime.MinValue;
		foreach (ObsidianConfigLocation candidate in candidates)
		{
			string file = Path.Combine(candidate.Dir, "obsidian.json");
			try
			{
				if (File.Exists(file) && File.GetLastWriteTimeUtc(file) > newest)
				{
					newest = File.GetLastWriteTimeUtc(file);
					best = candidate;
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// An unreadable place is not the one in use.
			}
		}

		return best ?? candidates[0];
	}

	public static ObsidianConfigLocation ForThisUser() => Find(Candidates(
		Current,
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
		Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")));
}
