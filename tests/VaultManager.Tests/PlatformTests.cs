using VaultManager.Core.Foundation;
using VaultManager.Core.Obsidian;

namespace VaultManager.Tests;

/// <summary>What differs on Linux. Written so it runs on any machine: the platform is a parameter, not a guess.</summary>
public sealed class PlatformTests
{
	[Theory]
	[InlineData("Obsidian", true)]
	[InlineData("obsidian", true)]
	[InlineData("OBSIDIAN", true)]
	[InlineData("obsidian-cli", false)]
	[InlineData("Obsidian Helper", false)]
	[InlineData("electron", false)]
	public void Obsidian_is_recognised_by_name_whatever_the_case(string processName, bool expected) =>
		Assert.Equal(expected, SystemObsidianProcess.IsObsidian(processName));

	static void Settings(string dir) {
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, "obsidian.json"), """{"vaults":{}}""");
	}

	[Fact]
	public void Windows_reads_Obsidian_from_app_data()
	{
		using var f = new Fixture();
		string appData = Path.Combine(f.Root, "AppData", "Roaming");

		ObsidianConfigLocation found = ObsidianConfigLocator.Find(ObsidianConfigLocator.Candidates(HostOs.Windows, appData, f.Root, null));

		Assert.Equal(Path.Combine(appData, "obsidian"), found.Dir);
		Assert.Equal(ObsidianInstall.Standard, found.Install);
	}

	[Fact]
	public void Linux_with_nothing_installed_falls_back_to_the_standard_place()
	{
		using var f = new Fixture();
		ObsidianConfigLocation found = ObsidianConfigLocator.Find(ObsidianConfigLocator.Candidates(HostOs.Linux, "unused", f.Root, null));
		Assert.Equal(Path.Combine(f.Root, ".config", "obsidian"), found.Dir);
		Assert.Equal(ObsidianInstall.Standard, found.Install);
	}

	[Fact]
	public void Linux_honours_XDG_CONFIG_HOME()
	{
		using var f = new Fixture();
		string xdg = Path.Combine(f.Root, "xdg");
		Settings(Path.Combine(xdg, "obsidian"));
		ObsidianConfigLocation found = ObsidianConfigLocator.Find(ObsidianConfigLocator.Candidates(HostOs.Linux, "unused", f.Root, xdg));
		Assert.Equal(Path.Combine(xdg, "obsidian"), found.Dir);
	}

	[Theory]
	[InlineData(ObsidianInstall.Flatpak, ".var/app/md.obsidian.Obsidian/config/obsidian")]
	[InlineData(ObsidianInstall.Snap, "snap/obsidian/current/.config/obsidian")]
	public void Linux_finds_a_sandboxed_install(ObsidianInstall install, string relative)
	{
		using var f = new Fixture();
		string dir = Path.Combine([f.Root, .. relative.Split('/')]);
		Settings(dir);

		ObsidianConfigLocation found = ObsidianConfigLocator.Find(ObsidianConfigLocator.Candidates(HostOs.Linux, "unused", f.Root, null));

		Assert.Equal(dir, found.Dir);
		Assert.Equal(install, found.Install);
	}

	[Fact]
	public void With_two_installs_the_one_used_last_wins()
	{
		using var f = new Fixture();
		string standard = Path.Combine(f.Root, ".config", "obsidian");
		string flatpak = Path.Combine(f.Root, ".var", "app", "md.obsidian.Obsidian", "config", "obsidian");
		Settings(standard);
		Settings(flatpak);
		File.SetLastWriteTimeUtc(Path.Combine(standard, "obsidian.json"), DateTime.UtcNow.AddDays(-30));

		ObsidianConfigLocation found = ObsidianConfigLocator.Find(ObsidianConfigLocator.Candidates(HostOs.Linux, "unused", f.Root, null));

		Assert.Equal(ObsidianInstall.Flatpak, found.Install);
	}

	[Fact]
	public void MacOS_reads_Obsidian_from_application_support()
	{
		using var f = new Fixture();
		ObsidianConfigLocation found = ObsidianConfigLocator.Find(ObsidianConfigLocator.Candidates(HostOs.MacOS, "unused", f.Root, null));
		Assert.Equal(Path.Combine(f.Root, "Library", "Application Support", "obsidian"), found.Dir);
	}

	[Fact]
	public void Two_vaults_that_differ_only_in_case_are_two_vaults_on_Linux_and_one_elsewhere()
	{
		Assert.Equal(!OperatingSystem.IsLinux(), PathRules.Same("/home/a/Notes", "/home/a/notes"));
		Assert.True(PathRules.Same("/home/a/Notes", "/home/a/Notes"));
		Assert.Equal(OperatingSystem.IsLinux(), PathRules.Key("/home/a/Notes") != PathRules.Key("/home/a/notes"));
	}
}
