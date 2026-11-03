using VaultManager.Core.Changes;
using VaultManager.Core.Foundation;
using VaultManager.Core.Obsidian;

namespace VaultManager.Tests;

/// <summary>The bugs of v0.2.0.1, each one pinned. These are the ones that could lose a user's settings.</summary>
public sealed class SafetyTests
{
	static CopyPlugin Copy(string target, string source, string id, string? from, string to, bool settings = false) =>
		new(target, id, id, from, to, source, settings, Fixture.MainHash(target, id));

	[Fact]
	public async Task Importing_settings_for_an_installed_but_disabled_plugin_does_not_copy_a_file_onto_itself()
	{
		using var f = new Fixture();
		string a = f.Vault("a"), b = f.Vault("b");
		Fixture.Plugin(a, "p", "1.0.0", data: """{"x":1}""");
		Fixture.Plugin(b, "p", "1.0.0", listed: false, data: """{"x":2}""");

		ApplyReport report = await f.Host().Executor.ApplyAsync([Copy(b, a, "p", "1.0.0", "1.0.0", settings: true)]);

		Assert.Equal(ChangeStatus.Done, report.Results.Single().Status);
		Assert.Contains("\"x\":1", File.ReadAllText(Path.Combine(VaultLayout.PluginDir(b, "p"), VaultLayout.DataFile)));
		Assert.Contains("p", CommunityPluginList.Read(b).Value!);
	}

	[Theory]
	[InlineData("[\"dataview\",")]
	[InlineData("")]
	[InlineData("{\"not\":\"a list\"}")]
	[InlineData("[1,2]")]
	public async Task A_damaged_enabled_list_is_refused_and_left_byte_for_byte(string content)
	{
		using var f = new Fixture();
		string a = f.Vault("a"), b = f.Vault("b");
		Fixture.Plugin(a, "p", "1.0.0");
		string list = VaultLayout.CommunityPluginsFile(b);
		File.WriteAllText(list, content);

		ChangeResult result = (await f.Host().Executor.ApplyAsync([Copy(b, a, "p", null, "1.0.0")])).Results.Single();

		Assert.Equal(ChangeStatus.Failed, result.Status);
		Assert.Equal(ChangeFailure.ListUnreadable, result.Failure);
		Assert.Equal(content, File.ReadAllText(list));
		Assert.False(Directory.Exists(VaultLayout.PluginDir(b, "p")));
	}

	[Fact]
	public void Reading_a_damaged_list_is_not_the_same_as_an_empty_one()
	{
		using var f = new Fixture();
		string v = f.Vault("v");
		File.WriteAllText(VaultLayout.CommunityPluginsFile(v), "[\"cut");
		Assert.Equal(ReadStatus.Unreadable, CommunityPluginList.Read(v).Status);
		Assert.Throws<ChangeRefusedException>(() => CommunityPluginList.Set(v, "p", true));
		Assert.Equal("[\"cut", File.ReadAllText(VaultLayout.CommunityPluginsFile(v)));
	}

	[Fact]
	public void The_newest_copy_is_the_source_whatever_order_the_vaults_are_in()
	{
		using var f = new Fixture();
		string old = f.Vault("first"), fresh = f.Vault("second");
		Fixture.Plugin(old, "git", "2.8.3");
		Fixture.Plugin(fresh, "git", "2.15.2");
		f.Register(("1", old, false), ("2", fresh, false));

		PluginRow row = f.Host().Scanner.Scan().Plugins.Single();

		Assert.Equal("2.15.2", row.NewestVersion);
		Assert.Equal(fresh, row.SourceVaultPath);
		Assert.True(row.Drifted);
	}

	[Fact]
	public void A_vault_on_an_unplugged_drive_stays_on_the_list_as_offline()
	{
		using var f = new Fixture();
		string live = f.Vault("live");
		f.Register(("1", live, false), ("2", Path.Combine(f.Root, "gone", "vault"), false));

		VaultState gone = f.Host().Scanner.Scan().Vaults.Single(v => v.Id == "2");

		Assert.False(gone.IsOnline);
		Assert.Equal("vault", gone.DisplayName);
	}

	[Fact]
	public void An_unreadable_vault_list_is_reported_rather_than_shown_as_no_vaults()
	{
		using var f = new Fixture();
		File.WriteAllText(f.Host().Registry.FilePath, "{ cut");
		Assert.Equal(ReadStatus.Unreadable, f.Host().Scanner.Scan().RegistryStatus);
		Assert.Throws<ChangeRefusedException>(() => f.Host().Registry.Register(f.Vault("x")));
		Assert.Equal("{ cut", File.ReadAllText(f.Host().Registry.FilePath));
	}

	[Fact]
	public void Restricted_mode_comes_from_the_browser_store_and_app_json_does_not_matter()
	{
		using var f = new Fixture();
		string store = Path.Combine(f.Paths.ObsidianConfigDir, "Local Storage", "leveldb");
		Directory.CreateDirectory(store);
		File.WriteAllText(Path.Combine(store, "000003.log"), "x_app://obsidian.md\0\u0001enable-plugin-aaaaaaaaaaaaaaaa\u0001true y enable-plugin-bbbbbbbbbbbbbbbb\u0001false");

		IReadOnlyDictionary<string, bool>? all = new CommunityPluginsReader(f.Paths.ObsidianConfigDir).ReadAll();

		Assert.Equal(CommunityPluginsState.On, CommunityPluginsReader.StateOf(all, "aaaaaaaaaaaaaaaa"));
		Assert.Equal(CommunityPluginsState.Off, CommunityPluginsReader.StateOf(all, "bbbbbbbbbbbbbbbb"));
		Assert.Equal(CommunityPluginsState.NeverEnabled, CommunityPluginsReader.StateOf(all, "cccccccccccccccc"));
		Assert.Equal(CommunityPluginsState.Unknown, CommunityPluginsReader.StateOf(null, "cccccccccccccccc"));
	}

	[Fact]
	public void The_newest_write_of_a_restricted_mode_key_wins()
	{
		using var f = new Fixture();
		string store = Path.Combine(f.Paths.ObsidianConfigDir, "Local Storage", "leveldb");
		Directory.CreateDirectory(store);
		File.WriteAllText(Path.Combine(store, "000003.log"), "enable-plugin-aaaaaaaaaaaaaaaa\u0001true enable-plugin-aaaaaaaaaaaaaaaa\u0001false");
		Assert.False(new CommunityPluginsReader(f.Paths.ObsidianConfigDir).ReadAll()!["aaaaaaaaaaaaaaaa"]);
	}

	[Fact]
	public async Task A_change_to_an_open_vault_waits_and_writes_nothing()
	{
		using var f = new Fixture();
		string a = f.Vault("a"), open = f.Vault("open");
		Fixture.Plugin(a, "p", "2.0.0");
		Fixture.Plugin(open, "p", "1.0.0");
		f.Register(("1", a, false), ("2", open, true));
		f.Process.IsRunning = true;

		ChangeResult result = (await f.Host().Executor.ApplyAsync([Copy(open, a, "p", "1.0.0", "2.0.0")])).Results.Single();

		Assert.Equal(ChangeStatus.Waiting, result.Status);
		Assert.Equal(ChangeFailure.VaultOpen, result.Failure);
		Assert.Equal("1.0.0", Fixture.VersionOf(open, "p"));
	}

	[Fact]
	public async Task With_Obsidian_running_and_its_list_unreadable_a_vault_counts_as_open()
	{
		using var f = new Fixture();
		string a = f.Vault("a"), b = f.Vault("b");
		Fixture.Plugin(a, "p", "2.0.0");
		Fixture.Plugin(b, "p", "1.0.0");
		File.WriteAllText(f.Host().Registry.FilePath, "{ cut");
		f.Process.IsRunning = true;

		ChangeResult result = (await f.Host().Executor.ApplyAsync([Copy(b, a, "p", "1.0.0", "2.0.0")])).Results.Single();

		Assert.Equal(ChangeFailure.VaultOpen, result.Failure);
		Assert.Equal("1.0.0", Fixture.VersionOf(b, "p"));
	}

	[Fact]
	public async Task Lines_that_ran_before_a_cancel_stay_undoable()
	{
		using var f = new Fixture();
		string a = f.Vault("a"), b = f.Vault("b"), c = f.Vault("c");
		Fixture.Plugin(a, "p", "1.0.0");
		var cancel = new CancellationTokenSource();
		var progress = new SyncProgress(_ => cancel.Cancel());

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Host().Executor.ApplyAsync([Copy(b, a, "p", null, "1.0.0"), Copy(c, a, "p", null, "1.0.0")], progress, cancel.Token));

		Assert.Equal("1.0.0", Fixture.VersionOf(b, "p"));
		Assert.Null(Fixture.VersionOf(c, "p"));
		f.Host().Executor.UndoLast();
		Assert.Null(Fixture.VersionOf(b, "p"));
	}

	sealed class SyncProgress(Action<ChangeResult> report) : IProgress<ChangeResult>
	{
		public void Report(ChangeResult value) => report(value);
	}

	[Fact]
	public async Task A_vault_marked_open_is_not_open_when_Obsidian_is_not_running()
	{
		using var f = new Fixture();
		string a = f.Vault("a"), last = f.Vault("last-open");
		Fixture.Plugin(a, "p", "2.0.0");
		Fixture.Plugin(last, "p", "1.0.0");
		f.Register(("1", a, false), ("2", last, true));

		Assert.False(f.Host().Scanner.Scan().Vaults.Single(v => v.Id == "2").IsOpen);
		Assert.Equal(ChangeStatus.Done, (await f.Host().Executor.ApplyAsync([Copy(last, a, "p", "1.0.0", "2.0.0")])).Results.Single().Status);
	}

	[Fact]
	public async Task Updating_a_plugin_keeps_the_target_settings()
	{
		using var f = new Fixture();
		string a = f.Vault("a"), b = f.Vault("b");
		Fixture.Plugin(a, "p", "2.0.0", data: """{"from":"source"}""");
		Fixture.Plugin(b, "p", "1.0.0", data: """{"from":"target"}""");

		await f.Host().Executor.ApplyAsync([Copy(b, a, "p", "1.0.0", "2.0.0")]);

		Assert.Equal("2.0.0", Fixture.VersionOf(b, "p"));
		Assert.Contains("target", File.ReadAllText(Path.Combine(VaultLayout.PluginDir(b, "p"), VaultLayout.DataFile)));
	}

	[Fact]
	public async Task A_new_plugin_gets_default_settings_unless_asked_and_secrets_never_travel()
	{
		using var f = new Fixture();
		string a = f.Vault("a"), plain = f.Vault("plain"), asked = f.Vault("asked");
		Fixture.Plugin(a, "p", "1.0.0", data: """{"apiKey":"sk-live-1234567890"}""");

		ApplyReport report = await f.Host().Executor.ApplyAsync([Copy(plain, a, "p", null, "1.0.0"), Copy(asked, a, "p", null, "1.0.0", settings: true)]);

		Assert.False(File.Exists(Path.Combine(VaultLayout.PluginDir(plain, "p"), VaultLayout.DataFile)));
		Assert.False(File.Exists(Path.Combine(VaultLayout.PluginDir(asked, "p"), VaultLayout.DataFile)));
		Assert.Equal(ChangeNote.None, report.Results[0].Note);
		Assert.Equal(ChangeNote.SettingsSkippedSecret, report.Results[1].Note);
	}

	[Fact]
	public async Task A_plugin_that_changed_after_it_was_queued_goes_back_for_review()
	{
		using var f = new Fixture();
		string a = f.Vault("a"), b = f.Vault("b");
		Fixture.Plugin(a, "p", "2.0.0");
		Fixture.Plugin(b, "p", "1.0.0");
		CopyPlugin queued = Copy(b, a, "p", "1.0.0", "2.0.0");
		Fixture.Plugin(b, "p", "1.5.0");

		ChangeResult result = (await f.Host().Executor.ApplyAsync([queued])).Results.Single();

		Assert.Equal(ChangeStatus.NeedsReview, result.Status);
		Assert.Equal(ChangeFailure.TargetChanged, result.Failure);
		Assert.Equal("1.5.0", Fixture.VersionOf(b, "p"));
	}

	[Fact]
	public async Task One_failed_line_does_not_stop_the_others_and_each_reports_for_itself()
	{
		using var f = new Fixture();
		string a = f.Vault("a"), good = f.Vault("good"), damaged = f.Vault("damaged");
		Fixture.Plugin(a, "p", "1.0.0");
		File.WriteAllText(VaultLayout.CommunityPluginsFile(damaged), "[");
		string gone = Path.Combine(f.Root, "gone");

		IReadOnlyList<ChangeResult> results = (await f.Host().Executor.ApplyAsync([Copy(damaged, a, "p", null, "1.0.0"), Copy(gone, a, "p", null, "1.0.0"), Copy(good, a, "p", null, "1.0.0")])).Results;

		Assert.Equal(ChangeFailure.ListUnreadable, results[0].Failure);
		Assert.Equal(ChangeStatus.Waiting, results[1].Status);
		Assert.Equal(ChangeFailure.VaultOffline, results[1].Failure);
		Assert.Equal(ChangeStatus.Done, results[2].Status);
		Assert.Equal("1.0.0", Fixture.VersionOf(good, "p"));
	}

	[Fact]
	public async Task A_copy_leaves_no_staging_folder_behind_and_skips_a_developer_repository()
	{
		using var f = new Fixture();
		string a = f.Vault("a"), b = f.Vault("b");
		string dir = Fixture.Plugin(a, "p", "1.0.0");
		Directory.CreateDirectory(Path.Combine(dir, ".git"));
		File.WriteAllText(Path.Combine(dir, ".git", "config"), "x");

		await f.Host().Executor.ApplyAsync([Copy(b, a, "p", null, "1.0.0")]);

		Assert.False(Directory.Exists(Path.Combine(VaultLayout.PluginDir(b, "p"), ".git")));
		Assert.False(Directory.Exists(Path.Combine(VaultLayout.ConfigDir(b), ".vault-manager-staging")));
	}

	[Fact]
	public void Atomic_write_leaves_only_the_target()
	{
		using var f = new Fixture();
		string path = Path.Combine(f.Root, "dir", "file.json");
		AtomicFile.WriteAllText(path, "one");
		AtomicFile.WriteAllText(path, "two");
		Assert.Equal("two", File.ReadAllText(path));
		Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
	}

	[Theory]
	[InlineData("2.8.3", "2.15.2", -1)]
	[InlineData("2.15.2", "2.8.3", 1)]
	[InlineData("1.0", "1.0.0", 0)]
	[InlineData("1.0.0-beta", "1.0.0", -1)]
	[InlineData("v1.2.0", "1.2.0", 0)]
	[InlineData(null, "0.0.1", -1)]
	public void Versions_compare_as_numbers(string? a, string? b, int sign) => Assert.Equal(sign, Math.Sign(PluginVersion.Compare(a, b)));

	[Fact]
	public void Secret_scan_flags_live_values_and_not_the_settings_that_only_sound_like_one()
	{
		// Real keys from this machine's vaults: none of them holds a secret.
		Assert.Empty(SecretScanner.FindKeys("""{"rememberPassword":false,"tokenizeUrls":true,"openAIDefaultTextModelMaxTokens":4096,"confirmPassword":true}"""));
		Assert.Equal(["apiKey"], SecretScanner.FindKeys("""{"nested":{"apiKey":"sk-1234567890"},"name":"x"}"""));
		Assert.NotEmpty(SecretScanner.FindKeys("{ cut"));
	}

	[Fact]
	public void Same_folder_names_are_told_apart_by_their_parent()
	{
		// Real separators of this machine: on Linux a backslash is part of a name, not a folder break.
		string root = Path.Combine(Path.GetTempPath(), "Obsidian");
		IReadOnlyDictionary<string, string> names = WorkspaceScanner.DisplayNames([new("1", Path.Combine(root, "PC"), false), new("2", Path.Combine(root, "CopyPc", "PC"), false), new("3", Path.Combine(Path.GetTempPath(), "Other"), false)]);
		Assert.Equal("PC (Obsidian)", names["1"]);
		Assert.Equal("PC (CopyPc)", names["2"]);
		Assert.Equal("Other", names["3"]);
	}

	[Fact]
	public void Registering_keeps_other_keys_and_does_not_add_a_vault_twice()
	{
		using var f = new Fixture();
		string v = f.Vault("v");
		File.WriteAllText(f.Host().Registry.FilePath, """{"vaults":{"aa":{"path":"C:\\x","ts":1}},"frame":"hidden"}""");

		string first = f.Host().Registry.Register(v);
		string second = f.Host().Registry.Register(v);

		Assert.Equal(first, second);
		string text = File.ReadAllText(f.Host().Registry.FilePath);
		Assert.Contains("\"frame\":\"hidden\"", text);
		Assert.Contains("aa", text);
		Assert.Equal(16, first.Length);
	}
}
