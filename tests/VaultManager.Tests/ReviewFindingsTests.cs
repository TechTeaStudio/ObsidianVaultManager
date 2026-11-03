using VaultManager.Core.Changes;
using VaultManager.Core.Foundation;
using VaultManager.Core.Obsidian;
using VaultManager.Core.Profiles;
using VaultManager.Core.Sync;

namespace VaultManager.Tests;

/// <summary>What an independent review of v0.6.0.0 proved could go wrong, each case pinned.</summary>
public sealed class ReviewFindingsTests
{
	static CopyPlugin Copy(string target, string source, string id, string? from, string to, bool settings = false) =>
		new(target, id, id, from, to, source, settings, Fixture.MainHash(target, id));

	static string Data(string vault, string id) => Path.Combine(VaultLayout.PluginDir(vault, id), VaultLayout.DataFile);

	// Undo tells the truth and takes nothing it did not put there.

	[Fact]
	public async Task Undo_keeps_a_note_edited_after_it_was_added_and_says_so()
	{
		using var f = new Fixture();
		string v = f.Vault("v");
		await f.Host().Executor.ApplyAsync([new AddProfileParts(v, ProfileStore.BuiltIn())]);
		string map = Path.Combine(v, "00 System", "Vault Map.md");
		File.WriteAllText(map, "my own words");

		var report = f.Host().Executor.UndoLast();

		Assert.Equal("my own words", File.ReadAllText(map));
		Assert.False(File.Exists(Path.Combine(v, "03 Plugins", "03 Plugins.md")));
		Assert.Equal(ChangeFailure.TargetChanged, report.Single().Failure);
		Assert.Null(f.Host().Database.LastUndoable());
	}

	[Fact]
	public async Task Undo_refuses_when_a_plugin_moved_on_after_apply()
	{
		using var f = new Fixture();
		string a = f.Vault("a"), b = f.Vault("b");
		Fixture.Plugin(a, "p", "2.0.0");
		Fixture.Plugin(b, "p", "1.0.0");
		await f.Host().Executor.ApplyAsync([Copy(b, a, "p", "1.0.0", "2.0.0")]);
		Fixture.Plugin(b, "p", "3.0.0");

		var report = f.Host().Executor.UndoLast();

		Assert.Equal(ChangeFailure.TargetChanged, report.Single().Failure);
		Assert.Equal("3.0.0", Fixture.VersionOf(b, "p"));
		Assert.Null(f.Host().Database.LastUndoable());
	}

	[Fact]
	public async Task Undo_of_an_update_keeps_settings_changed_since()
	{
		using var f = new Fixture();
		string a = f.Vault("a"), b = f.Vault("b");
		Fixture.Plugin(a, "p", "2.0.0");
		Fixture.Plugin(b, "p", "1.0.0", data: """{"v":"before"}""");
		await f.Host().Executor.ApplyAsync([Copy(b, a, "p", "1.0.0", "2.0.0")]);
		File.WriteAllText(Data(b, "p"), """{"v":"written after"}""");

		f.Host().Executor.UndoLast();

		Assert.Equal("1.0.0", Fixture.VersionOf(b, "p"));
		Assert.Contains("written after", File.ReadAllText(Data(b, "p")));
	}

	[Fact]
	public async Task Undo_of_copied_settings_brings_the_old_ones_back_when_nothing_changed_since()
	{
		using var f = new Fixture();
		string a = f.Vault("a"), b = f.Vault("b");
		Fixture.Plugin(a, "p", "1.0.0", data: """{"v":"source"}""");
		Fixture.Plugin(b, "p", "1.0.0", data: """{"v":"mine"}""");
		await f.Host().Executor.ApplyAsync([Copy(b, a, "p", "1.0.0", "1.0.0", settings: true)]);
		Assert.Contains("source", File.ReadAllText(Data(b, "p")));

		f.Host().Executor.UndoLast();

		Assert.Contains("mine", File.ReadAllText(Data(b, "p")));
	}

	[Fact]
	public async Task Undo_with_a_lost_snapshot_leaves_the_plugin_whole()
	{
		using var f = new Fixture();
		string a = f.Vault("a"), b = f.Vault("b");
		Fixture.Plugin(a, "p", "2.0.0");
		Fixture.Plugin(b, "p", "1.0.0");
		await f.Host().Executor.ApplyAsync([Copy(b, a, "p", "1.0.0", "2.0.0")]);
		Folder.Delete(f.Paths.SnapshotsDir);

		var report = f.Host().Executor.UndoLast();

		Assert.Equal(ChangeFailure.SourceMissing, report.Single().Failure);
		Assert.Equal("2.0.0", Fixture.VersionOf(b, "p"));
	}

	[Fact]
	public async Task Undo_of_an_added_plugin_keeps_a_copy_of_settings_made_since()
	{
		using var f = new Fixture();
		string a = f.Vault("a"), b = f.Vault("b");
		Fixture.Plugin(a, "p", "1.0.0");
		await f.Host().Executor.ApplyAsync([Copy(b, a, "p", null, "1.0.0")]);
		File.WriteAllText(Data(b, "p"), """{"v":"hours of tuning"}""");

		f.Host().Executor.UndoLast();

		Assert.False(Directory.Exists(VaultLayout.PluginDir(b, "p")));
		string kept = Assert.Single(Directory.GetFiles(Path.Combine(f.Paths.SnapshotsDir, "undone")));
		Assert.Contains("hours of tuning", File.ReadAllText(kept));
	}

	[Fact]
	public async Task Undo_of_a_settings_copy_refuses_a_file_changed_since_and_restores_the_sync_history_otherwise()
	{
		using var f = new Fixture();
		string a = f.Vault("a"), b = f.Vault("b");
		string fileA = Path.Combine(VaultLayout.ConfigDir(a), "app.json"), fileB = Path.Combine(VaultLayout.ConfigDir(b), "app.json");
		File.WriteAllText(fileA, "{\"a\":1}");
		File.WriteAllText(fileB, "{\"b\":1}");
		var host = f.Host();
		SyncEntry entry = host.Sync.Preview(a, b, [SettingsCategory.Editor]).Single();
		Assert.Equal(SyncDecision.Conflict, entry.Decision);

		await host.Executor.ApplyAsync([SettingsSync.ToChange(a, b, entry)]);
		host.Executor.UndoLast();

		Assert.Equal("{\"b\":1}", File.ReadAllText(fileB));
		Assert.Equal(SyncDecision.Conflict, host.Sync.Preview(a, b, [SettingsCategory.Editor]).Single().Decision);

		await host.Executor.ApplyAsync([SettingsSync.ToChange(a, b, host.Sync.Preview(a, b, [SettingsCategory.Editor]).Single())]);
		File.WriteAllText(fileB, "{\"b\":2}");
		Assert.Equal(ChangeFailure.TargetChanged, host.Executor.UndoLast().Single().Failure);
		Assert.Equal("{\"b\":2}", File.ReadAllText(fileB));
	}

	// An update replaces the plugin's code and nothing else.

	[Fact]
	public async Task An_update_keeps_files_the_new_build_does_not_have_but_drops_stale_code()
	{
		using var f = new Fixture();
		string a = f.Vault("a"), b = f.Vault("b");
		Fixture.Plugin(a, "p", "2.0.0");
		string dir = Fixture.Plugin(b, "p", "1.0.0");
		File.WriteAllText(Path.Combine(dir, "history.json"), "mine");
		Directory.CreateDirectory(Path.Combine(dir, ".git"));
		File.WriteAllText(Path.Combine(dir, ".git", "config"), "mine");
		File.WriteAllText(Path.Combine(dir, "styles.css"), "old build");

		await f.Host().Executor.ApplyAsync([Copy(b, a, "p", "1.0.0", "2.0.0")]);

		Assert.Equal("mine", File.ReadAllText(Path.Combine(dir, "history.json")));
		Assert.Equal("mine", File.ReadAllText(Path.Combine(dir, ".git", "config")));
		Assert.False(File.Exists(Path.Combine(dir, "styles.css")));
	}

	[Fact]
	public async Task A_failed_list_write_puts_the_old_plugin_back()
	{
		using var f = new Fixture();
		string a = f.Vault("a"), b = f.Vault("b");
		Fixture.Plugin(a, "p", "2.0.0");
		Fixture.Plugin(b, "p", "1.0.0", listed: false, data: """{"v":"mine"}""");
		// A folder where the list file belongs makes the write fail on every system. A read-only file did that on
		// Windows only: Linux replaces it by rename and the write succeeded.
		Directory.CreateDirectory(VaultLayout.CommunityPluginsFile(b));

		ChangeResult result = (await f.Host().Executor.ApplyAsync([Copy(b, a, "p", "1.0.0", "2.0.0")])).Results.Single();

		Assert.Equal(ChangeStatus.Failed, result.Status);
		Assert.Equal("1.0.0", Fixture.VersionOf(b, "p"));
		Assert.Contains("mine", File.ReadAllText(Data(b, "p")));
	}

	[Fact]
	public void When_the_old_plugin_cannot_be_put_back_the_saved_folder_is_not_touched()
	{
		using var f = new Fixture();
		string target = Path.Combine(f.Root, "target"), retired = Path.Combine(f.Root, "retired");
		Directory.CreateDirectory(target);
		Directory.CreateDirectory(retired);
		File.WriteAllText(Path.Combine(retired, "old.txt"), "old");
		File.WriteAllText(Path.Combine(target, "main.js"), "new");

		// What blocks a delete differs: an open file on Windows, a folder without write permission on Linux.
		FileStream? hold = OperatingSystem.IsWindows() ? new FileStream(Path.Combine(target, "main.js"), FileMode.Open, FileAccess.Read, FileShare.None) : null;
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserExecute);
		}

		try
		{
			bool restored = ChangeExecutor.TryPutBack(target, retired, hadOld: true);

			Assert.False(restored);
			Assert.Equal("old", File.ReadAllText(Path.Combine(retired, "old.txt")));
		}
		finally
		{
			hold?.Dispose();
			if (!OperatingSystem.IsWindows())
			{
				File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}
		}
	}

	// A new vault is all or nothing, and only a real one gets registered.

	[Fact]
	public async Task Registration_does_not_follow_a_failed_create()
	{
		using var f = new Fixture();
		f.Register();
		string taken = Path.Combine(f.Root, "taken");
		Directory.CreateDirectory(taken);
		File.WriteAllText(Path.Combine(taken, "note.md"), "mine");

		var results = (await f.Host().Executor.ApplyAsync([new CreateVault(f.Root, "taken", null, new Dictionary<string, string>()), new RegisterVault(taken)])).Results;

		Assert.Equal(ChangeStatus.Failed, results[1].Status);
		Assert.Equal(ChangeFailure.TargetNotEmpty, results[1].Failure);
		Assert.Empty(f.Host().Registry.Read().Value!);
	}

	[Fact]
	public async Task A_folder_that_is_not_a_vault_is_not_registered()
	{
		using var f = new Fixture();
		f.Register();
		ChangeResult result = (await f.Host().Executor.ApplyAsync([new RegisterVault(Path.Combine(f.Root, "nothing-here"))])).Results.Single();
		Assert.Equal(ChangeFailure.SourceMissing, result.Failure);
		Assert.Empty(f.Host().Registry.Read().Value!);
	}

	[Fact]
	public async Task A_new_vault_whose_plugin_source_vanished_fails_and_can_be_retried()
	{
		using var f = new Fixture();
		string source = f.Vault("source");
		var profile = new Profile { Id = "x", Name = "x", Plugins = ["p"] };
		var item = new CreateVault(f.Root, "fresh", profile, new Dictionary<string, string> { ["p"] = source });

		ChangeResult failed = (await f.Host().Executor.ApplyAsync([item])).Results.Single();

		Assert.Equal(ChangeFailure.SourceMissing, failed.Failure);
		Assert.False(Directory.Exists(item.NewVaultPath));

		Fixture.Plugin(source, "p", "1.0.0");
		Assert.Equal(ChangeStatus.Done, (await f.Host().Executor.ApplyAsync([item])).Results.Single().Status);
		Assert.Equal(["p"], CommunityPluginList.Read(item.NewVaultPath).Value);
	}

	[Fact]
	public async Task Profile_parts_that_fail_halfway_leave_nothing_behind()
	{
		using var f = new Fixture();
		string v = f.Vault("v");
		File.WriteAllText(Path.Combine(v, "b-file"), "a file where a folder must go");
		var profile = new Profile { Id = "x", Name = "x", Folders = ["a-folder", "b-file"], FolderNotes = true };

		ChangeResult result = (await f.Host().Executor.ApplyAsync([new AddProfileParts(v, profile)])).Results.Single();

		Assert.Equal(ChangeStatus.Failed, result.Status);
		Assert.False(Directory.Exists(Path.Combine(v, "a-folder")));
	}

	// Secrets inside containers count.

	[Theory]
	[InlineData("""{"apiKeys":["sk-live-1234567890"]}""", "apiKeys")]
	[InlineData("""{"tokens":[{"value":"sk-live-1234567890"}]}""", "tokens")]
	[InlineData("""{"token":{"value":"sk-live-1234567890"}}""", "token")]
	[InlineData("""{"nested":{"password":"correct horse"}}""", "password")]
	public void A_secret_held_in_a_list_or_an_object_is_found(string json, string key) =>
		Assert.Contains(key, SecretScanner.FindKeys(json));

	[Theory]
	[InlineData("""{"tokens":[1,2,3]}""")]
	[InlineData("""{"token":{"enabled":true,"length":32}}""")]
	[InlineData("""{"apiKeys":[]}""")]
	public void A_container_with_no_text_in_it_is_not_a_secret(string json) => Assert.Empty(SecretScanner.FindKeys(json));

	// Odd files from elsewhere are data, not crashes.

	[Theory]
	[InlineData("""{"id":"p","name":"P","version":"1.0.0","author":{"name":"n"}}""")]
	[InlineData("""{"id":"p","name":"P","version":"1.0.0","isDesktopOnly":"yes"}""")]
	[InlineData("""{"id":"p","name":{"a":1},"version":"1.0.0","description":[1]}""")]
	[InlineData("""{"id":1,"name":2,"version":3}""")]
	public void An_unusual_manifest_does_not_break_a_scan(string manifest)
	{
		using var f = new Fixture();
		string v = f.Vault("v");
		string dir = VaultLayout.PluginDir(v, "p");
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, VaultLayout.ManifestFile), manifest);
		f.Register(("1", v, false));

		PluginRow row = f.Host().Scanner.Scan().Plugins.Single();

		Assert.False(string.IsNullOrEmpty(row.Name));
	}

	[Theory]
	[InlineData("""{"Id":"a","Name":"a","Plugins":null}""")]
	[InlineData("""{"Id":"a","Name":"a","Folders":null}""")]
	[InlineData("""{"Id":"a","Name":"a","Settings":null}""")]
	[InlineData("""{"Id":"a","Name":"a","Templates":null}""")]
	[InlineData("""{"Id":"a","Name":"a","Plugins":[null]}""")]
	[InlineData("""{"Id":"a","Name":"a","Folders":[null]}""")]
	[InlineData("""{"Id":"a","Name":"a","Templates":{"a.md":null}}""")]
	[InlineData("""{"Id":"a","Name":"a","Settings":{"app.json":null}}""")]
	[InlineData("""{"Id":"../../evil","Name":"a"}""")]
	[InlineData("""{"Id":"a\\b","Name":"a"}""")]
	public void A_profile_with_holes_or_a_hostile_id_is_rejected_cleanly(string json)
	{
		using var f = new Fixture();
		string file = Path.Combine(f.Root, "x.json");
		File.WriteAllText(file, json);
		Assert.Throws<ProfileRejectedException>(() => f.Host().Profiles.Import(file));
	}

	[Fact]
	public void A_broken_file_in_the_profiles_folder_does_not_stop_the_app()
	{
		using var f = new Fixture();
		Directory.CreateDirectory(f.Paths.ProfilesDir);
		File.WriteAllText(Path.Combine(f.Paths.ProfilesDir, "bad" + ProfileFile.Extension), """{"Id":"a","Name":"a","Plugins":null}""");
		Assert.Single(f.Host().Profiles.List());
	}
}
