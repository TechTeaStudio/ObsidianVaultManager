using System.Collections.ObjectModel;
using VaultManager.App.Localization;
using VaultManager.App.Logic;
using VaultManager.Core.Changes;
using VaultManager.Core.Foundation;
using VaultManager.Core.Obsidian;
using VaultManager.Core.Profiles;
using VaultManager.Core.Store;
using VaultManager.Core.Sync;

namespace VaultManager.Tests.App;

static class Sample
{
	public static VaultState Vault(string id, string? path = null, bool online = true, bool open = false, CommunityPluginsState plugins = CommunityPluginsState.On, bool listUnreadable = false, string? error = null) =>
		new(id, path ?? @"D:\v\" + id, id, online, open, plugins, listUnreadable, error);

	public static PluginInstall Install(string vault, string? version = "1.0.0", bool listed = true, bool folder = true, bool data = false, string? hash = "h") =>
		new(vault, @"D:\v\" + vault, version, listed, folder, data, hash, 1000);

	public static PluginRow Row(string id, params PluginInstall[] installs) => WorkspaceRow(id, installs);

	public static PluginRow WorkspaceRow(string id, IReadOnlyList<PluginInstall> installs)
	{
		List<PluginInstall> present = installs.Where(i => i.HasFolder && i.Version is not null).ToList();
		PluginInstall? newest = null;
		foreach (PluginInstall install in present)
		{
			if (newest is null || PluginVersion.Compare(install.Version, newest.Version) > 0)
			{
				newest = install;
			}
		}

		bool drifted = present.Select(i => i.Version).Distinct().Count() > 1;
		bool differs = present.Where(i => i.MainHash is not null).GroupBy(i => i.Version).Any(g => g.Select(i => i.MainHash).Distinct().Count() > 1);
		return new PluginRow(id, id + " name", null, null, newest?.Version, newest?.VaultPath, drifted, differs, installs);
	}

	public static WorkspaceSnapshot Snapshot(IReadOnlyList<VaultState> vaults, params PluginRow[] plugins) =>
		new(ReadStatus.Ok, vaults, plugins, DateTimeOffset.Now);

	public static Profile Profile(string[]? plugins = null, string[]? folders = null) =>
		new() { Id = "p", Name = "P", Plugins = plugins ?? [], Folders = folders ?? [], VaultMap = true };
}

public sealed class PluralTests
{
	[Theory]
	[InlineData(1, PluralForm.One)]
	[InlineData(21, PluralForm.One)]
	[InlineData(101, PluralForm.One)]
	[InlineData(2, PluralForm.Few)]
	[InlineData(4, PluralForm.Few)]
	[InlineData(22, PluralForm.Few)]
	[InlineData(5, PluralForm.Many)]
	[InlineData(0, PluralForm.Many)]
	[InlineData(11, PluralForm.Many)]
	[InlineData(12, PluralForm.Many)]
	[InlineData(14, PluralForm.Many)]
	[InlineData(111, PluralForm.Many)]
	[InlineData(112, PluralForm.Many)]
	public void Russian_uses_three_forms(int n, PluralForm expected) => Assert.Equal(expected, Plural.Choose(n, AppLanguage.Russian));

	[Theory]
	[InlineData(1, PluralForm.One)]
	[InlineData(0, PluralForm.Many)]
	[InlineData(2, PluralForm.Many)]
	[InlineData(21, PluralForm.Many)]
	public void English_uses_two_forms(int n, PluralForm expected) => Assert.Equal(expected, Plural.Choose(n, AppLanguage.English));
}

public sealed class AttentionTests
{
	static AttentionInput Input(WorkspaceSnapshot snapshot, ISet<string>? ignored = null, IReadOnlyDictionary<string, BoundAudit>? audits = null, IReadOnlyList<OnlineVersion>? online = null) =>
		new(snapshot, new HashSet<string>(ignored ?? new HashSet<string>()), audits ?? new Dictionary<string, BoundAudit>(), online ?? []);

	[Fact]
	public void Older_versions_come_from_drifted_plugins_only()
	{
		PluginRow drifted = Sample.Row("a", Sample.Install("x", "2.0.0"), Sample.Install("y", "1.0.0"));
		PluginRow same = Sample.Row("b", Sample.Install("x"), Sample.Install("y"));
		IReadOnlyList<AttentionItem> items = AttentionBuilder.Build(Input(Sample.Snapshot([Sample.Vault("x"), Sample.Vault("y")], drifted, same)));

		AttentionItem item = Assert.Single(items);
		Assert.Equal(AttentionKind.Older, item.Kind);
		Assert.Equal("a", item.Plugin!.Id);
	}

	[Fact]
	public void Same_version_with_different_files_is_its_own_warning()
	{
		PluginRow row = Sample.Row("a", Sample.Install("x", "1.0.0", hash: "one"), Sample.Install("y", "1.0.0", hash: "two"));
		IReadOnlyList<AttentionItem> items = AttentionBuilder.Build(Input(Sample.Snapshot([Sample.Vault("x"), Sample.Vault("y")], row)));
		Assert.Equal(AttentionKind.Differs, Assert.Single(items).Kind);
	}

	[Fact]
	public void Plugins_off_and_never_enabled_are_listed_and_unknown_is_not()
	{
		WorkspaceSnapshot snapshot = Sample.Snapshot([
			Sample.Vault("off", plugins: CommunityPluginsState.Off),
			Sample.Vault("never", plugins: CommunityPluginsState.NeverEnabled),
			Sample.Vault("unknown", plugins: CommunityPluginsState.Unknown)]);
		IReadOnlyList<AttentionItem> items = AttentionBuilder.Build(Input(snapshot));
		Assert.Equal([AttentionKind.PluginsOff, AttentionKind.PluginsNeverEnabled], items.Select(i => i.Kind));
	}

	[Fact]
	public void An_offline_vault_is_not_called_off()
	{
		WorkspaceSnapshot snapshot = Sample.Snapshot([Sample.Vault("gone", online: false, plugins: CommunityPluginsState.NeverEnabled)]);
		Assert.Empty(AttentionBuilder.Build(Input(snapshot)));
	}

	[Fact]
	public void A_profile_gap_shows_only_for_a_bound_vault_with_something_missing()
	{
		var missing = new BoundAudit(Sample.Profile(), new ProfileAudit(["p1"], [], [], [], [], false));
		var complete = new BoundAudit(Sample.Profile(), new ProfileAudit([], ["p1"], [], [], [], false));
		var audits = new Dictionary<string, BoundAudit> { ["x"] = missing, ["y"] = complete };
		IReadOnlyList<AttentionItem> items = AttentionBuilder.Build(Input(Sample.Snapshot([Sample.Vault("x"), Sample.Vault("y"), Sample.Vault("z")]), audits: audits));
		AttentionItem item = Assert.Single(items);
		Assert.Equal(AttentionKind.ProfileGap, item.Kind);
		Assert.Equal("x", item.Vault!.Id);
	}

	[Fact]
	public void A_newer_online_version_shows_only_when_newer_than_the_newest_copy()
	{
		PluginRow row = Sample.Row("a", Sample.Install("x", "1.0.0"));
		PluginRow current = Sample.Row("b", Sample.Install("x", "2.0.0"));
		IReadOnlyList<AttentionItem> items = AttentionBuilder.Build(Input(Sample.Snapshot([Sample.Vault("x")], row, current),
			online: [new OnlineVersion("a", "1.1.0", DateTimeOffset.Now), new OnlineVersion("b", "2.0.0", DateTimeOffset.Now)]));
		AttentionItem item = Assert.Single(items);
		Assert.Equal(AttentionKind.NewerOnline, item.Kind);
		Assert.Equal("1.1.0", item.OnlineVersion);
	}

	[Fact]
	public void An_ignored_online_version_comes_back_when_a_newer_one_appears()
	{
		PluginRow row = Sample.Row("a", Sample.Install("x", "1.0.0"));
		string ignored = AttentionBuilder.Build(Input(Sample.Snapshot([Sample.Vault("x")], row),
			online: [new OnlineVersion("a", "1.1.0", DateTimeOffset.Now)])).Single().Key;

		IReadOnlyList<AttentionItem> later = AttentionBuilder.Build(Input(Sample.Snapshot([Sample.Vault("x")], row), new HashSet<string> { ignored },
			online: [new OnlineVersion("a", "1.2.0", DateTimeOffset.Now)]));
		Assert.False(Assert.Single(later).Ignored);
	}

	[Fact]
	public void Ignored_items_stay_in_the_list_marked_so_they_can_be_restored()
	{
		PluginRow row = Sample.Row("a", Sample.Install("x", "2.0.0"), Sample.Install("y", "1.0.0"));
		WorkspaceSnapshot snapshot = Sample.Snapshot([Sample.Vault("x"), Sample.Vault("y")], row);
		string key = AttentionBuilder.Build(Input(snapshot)).Single().Key;
		Assert.True(AttentionBuilder.Build(Input(snapshot, new HashSet<string> { key })).Single().Ignored);
	}

	[Fact]
	public void The_list_runs_from_most_serious_to_least()
	{
		PluginRow older = Sample.Row("a", Sample.Install("x", "2.0.0"), Sample.Install("y", "1.0.0"));
		PluginRow differs = Sample.Row("b", Sample.Install("x", "1.0.0", hash: "one"), Sample.Install("y", "1.0.0", hash: "two"));
		WorkspaceSnapshot snapshot = Sample.Snapshot(
			[Sample.Vault("off", plugins: CommunityPluginsState.Off), Sample.Vault("x", listUnreadable: true), Sample.Vault("y", error: "IOException")], older, differs);

		Assert.Equal(
			[AttentionKind.ScanError, AttentionKind.ListUnreadable, AttentionKind.Differs, AttentionKind.Older, AttentionKind.PluginsOff],
			AttentionBuilder.Build(Input(snapshot)).Select(i => i.Kind));
	}

	[Fact]
	public void Unreadable_lists_and_scan_errors_are_reported()
	{
		WorkspaceSnapshot snapshot = Sample.Snapshot([Sample.Vault("x", listUnreadable: true), Sample.Vault("y", error: "IOException")]);
		Assert.Equal([AttentionKind.ScanError, AttentionKind.ListUnreadable], AttentionBuilder.Build(Input(snapshot)).Select(i => i.Kind));
	}
}

public sealed class FactsTests
{
	[Fact]
	public void Behind_lists_older_copies_with_files_only()
	{
		PluginRow row = Sample.Row("a", Sample.Install("x", "2.8.3"), Sample.Install("y", "2.15.2"), Sample.Install("z", null, folder: false));
		Assert.Equal(["x"], PluginFacts.Behind(row).Select(i => i.VaultId));
	}

	[Theory]
	[InlineData("2.0.0", CellKind.Current)]
	[InlineData("1.0.0", CellKind.Older)]
	public void A_cell_says_how_a_copy_compares(string version, CellKind expected)
	{
		PluginRow row = Sample.Row("a", Sample.Install("x", "2.0.0"), Sample.Install("y", version));
		Assert.Equal(expected, PluginFacts.Cell(row, Sample.Vault("y"), queued: false));
	}

	[Fact]
	public void A_cell_tells_every_other_state_apart()
	{
		PluginRow row = Sample.Row("a", Sample.Install("x", "2.0.0"), Sample.Install("off", "2.0.0", listed: false), Sample.Install("gone", null, folder: false));
		Assert.Equal(CellKind.Off, PluginFacts.Cell(row, Sample.Vault("off"), false));
		Assert.Equal(CellKind.Broken, PluginFacts.Cell(row, Sample.Vault("gone"), false));
		Assert.Equal(CellKind.Empty, PluginFacts.Cell(row, Sample.Vault("none"), false));
		Assert.Equal(CellKind.Waiting, PluginFacts.Cell(row, Sample.Vault("none"), true));
		Assert.Equal(CellKind.Offline, PluginFacts.Cell(row, Sample.Vault("none", online: false), false));
		Assert.Equal(CellKind.Blocked, PluginFacts.Cell(row, Sample.Vault("none", listUnreadable: true), false));
		Assert.Equal(CellKind.Blocked, PluginFacts.Cell(row, Sample.Vault("none", error: "IOException"), false));
	}

	[Fact]
	public void Settings_fate_follows_what_the_target_holds()
	{
		var copy = new CopyPlugin(@"D:\v\y", "a", "A", "1.0.0", "2.0.0", @"D:\v\x", false, "h");
		Assert.Equal(SettingsFate.Kept, PluginFacts.FateOf(copy, Sample.Install("y", data: true)));
		Assert.Equal(SettingsFate.None, PluginFacts.FateOf(copy, Sample.Install("y", data: false)));
		Assert.Equal(SettingsFate.Default, PluginFacts.FateOf(copy, null));
		Assert.Equal(SettingsFate.Copied, PluginFacts.FateOf(copy with { CopySettings = true }, null));
		Assert.Equal(SettingsFate.NotTouched, PluginFacts.FateOf(new SetPluginEnabled(@"D:\v\y", "a", "A", false), null));
	}
}

public sealed class QueueTests
{
	static CopyPlugin Copy(string vault = "y", string id = "a") => new(@"D:\v\" + vault, id, id, "1.0.0", "2.0.0", @"D:\v\x", false, "h");

	[Fact]
	public void The_same_request_twice_stays_one_line()
	{
		var queue = new ChangeQueue();
		Assert.True(queue.Add(Copy()));
		Assert.False(queue.Add(Copy()));
		Assert.Single(queue.Lines);
	}

	[Fact]
	public void A_toggle_and_a_copy_of_one_plugin_are_two_lines()
	{
		var queue = new ChangeQueue();
		queue.Add(Copy());
		queue.Add(new SetPluginEnabled(@"D:\v\y", "a", "a", false));
		Assert.Equal(2, queue.Lines.Count);
	}

	[Fact]
	public void A_new_request_replaces_a_line_that_failed_but_not_one_on_its_way()
	{
		var queue = new ChangeQueue();
		CopyPlugin first = Copy();
		queue.Add(first);
		queue.Record(new ChangeResult(first.Id, ChangeStatus.Waiting, ChangeFailure.VaultOpen));
		Assert.False(queue.Add(Copy()));
		Assert.Equal(first.Id, queue.Lines.Single().Item.Id);

		queue.Record(new ChangeResult(first.Id, ChangeStatus.Failed, ChangeFailure.WriteFailed));
		CopyPlugin second = Copy();
		Assert.True(queue.Add(second));
		Assert.Equal(second.Id, queue.Lines.Single().Item.Id);
	}

	[Fact]
	public void Each_result_lands_on_its_line_and_only_what_needs_a_person_is_shown()
	{
		var queue = new ChangeQueue();
		CopyPlugin done = Copy("a"), failed = Copy("b"), waiting = Copy("c"), review = Copy("d");
		foreach (ChangeItem item in new ChangeItem[] { done, failed, waiting, review })
		{
			queue.Add(item);
		}

		queue.Record(new ChangeResult(done.Id, ChangeStatus.Done, Note: ChangeNote.SettingsSkippedSecret));
		queue.Record(new ChangeResult(failed.Id, ChangeStatus.Failed, ChangeFailure.WriteFailed));
		queue.Record(new ChangeResult(waiting.Id, ChangeStatus.Waiting, ChangeFailure.VaultOpen));
		queue.Record(new ChangeResult(review.Id, ChangeStatus.NeedsReview, ChangeFailure.TargetChanged));

		Assert.Equal([LineState.Done, LineState.Failed, LineState.Waiting, LineState.NeedsReview], queue.Lines.Select(l => l.State));
		Assert.Equal(ChangeNote.SettingsSkippedSecret, queue.Lines[0].Note);
		Assert.Equal(ChangeFailure.WriteFailed, queue.Lines[1].Failure);
		Assert.Equal([failed.Id, waiting.Id, review.Id], queue.Lines.Where(l => l.NeedsShowing).Select(l => l.Item.Id));
		Assert.Empty(queue.NextBatch());
		Assert.Equal([waiting.Id], queue.Waiting().Select(l => l.Item.Id));
	}

	[Fact]
	public void The_next_batch_is_what_was_asked_and_a_released_line_joins_it()
	{
		var queue = new ChangeQueue();
		CopyPlugin line = Copy();
		queue.Restore(line);
		Assert.Empty(queue.NextBatch());
		Assert.Equal(1, queue.WaitingCount);

		queue.Release(line.Id);

		Assert.Equal([line.Id], queue.NextBatch().Select(i => i.Id));
		Assert.Equal(0, queue.WaitingCount);
	}

	[Fact]
	public void Finished_lines_stay_until_a_scan_has_seen_them_so_a_second_click_cannot_run_on_stale_data()
	{
		var queue = new ChangeQueue();
		CopyPlugin done = Copy("a", "p"), failed = Copy("b", "q");
		queue.Add(done);
		queue.Add(failed);
		queue.Record(new ChangeResult(done.Id, ChangeStatus.Done));
		queue.Record(new ChangeResult(failed.Id, ChangeStatus.Failed, ChangeFailure.WriteFailed));

		Assert.True(queue.IsPluginPending(@"D:\v\a", "p"));
		Assert.False(queue.IsPluginPending(@"D:\v\b", "q"));
		Assert.False(queue.Add(Copy("a", "p")));

		queue.DropFinished();

		Assert.False(queue.IsPluginPending(@"D:\v\a", "p"));
		Assert.Equal([failed.Id], queue.Lines.Select(l => l.Item.Id));
	}

	[Fact]
	public void The_panel_shows_only_when_something_waits_went_wrong_runs_or_can_be_undone()
	{
		var queue = new ChangeQueue();
		Assert.False(QueueRules.ShowPanel(queue.Lines, running: false, canUndo: false));
		Assert.True(QueueRules.ShowPanel(queue.Lines, running: true, canUndo: false));
		Assert.True(QueueRules.ShowPanel(queue.Lines, running: false, canUndo: true));

		CopyPlugin line = Copy();
		queue.Add(line);
		Assert.False(QueueRules.ShowPanel(queue.Lines, false, false));
		queue.Record(new ChangeResult(line.Id, ChangeStatus.Done));
		Assert.False(QueueRules.ShowPanel(queue.Lines, false, false));

		foreach ((ChangeStatus status, ChangeFailure failure) in new[] { (ChangeStatus.Waiting, ChangeFailure.VaultOpen), (ChangeStatus.Failed, ChangeFailure.WriteFailed), (ChangeStatus.NeedsReview, ChangeFailure.TargetChanged) })
		{
			queue.Record(new ChangeResult(line.Id, status, failure));
			Assert.True(QueueRules.ShowPanel(queue.Lines, false, false));
		}
	}

	[Fact]
	public void A_changed_line_can_be_renewed_from_the_latest_scan()
	{
		PluginRow row = Sample.Row("a", Sample.Install("x", "3.0.0"), Sample.Install("y", "1.5.0", hash: "new"));
		WorkspaceSnapshot snapshot = Sample.Snapshot([Sample.Vault("x"), Sample.Vault("y")], row);
		var line = new CopyPlugin(@"D:\v\y", "a", "A", "1.0.0", "2.0.0", @"D:\v\x", true, "old");

		var renewed = Assert.IsType<CopyPlugin>(QueueRules.Renew(line, snapshot));

		Assert.Equal("1.5.0", renewed.FromVersion);
		Assert.Equal("3.0.0", renewed.ToVersion);
		Assert.Equal("new", renewed.ExpectedTargetHash);
		Assert.True(renewed.CopySettings);
	}

	[Fact]
	public void A_settings_line_cannot_be_renewed()
	{
		WorkspaceSnapshot snapshot = Sample.Snapshot([Sample.Vault("x")]);
		Assert.Null(QueueRules.Renew(new CopySettingsFile(@"D:\v\y", @"D:\v\x", "app.json", "s", "t"), snapshot));
	}
}

public sealed class UpdateAllRuleTests
{
	static IReadOnlyList<AttentionItem> Items(WorkspaceSnapshot snapshot, params string[] ignored) =>
		AttentionBuilder.Build(new AttentionInput(snapshot, new HashSet<string>(ignored), new Dictionary<string, BoundAudit>(), [new OnlineVersion("online-only", "9.0.0", DateTimeOffset.Now)]));

	[Fact]
	public void It_takes_older_copies_only_and_leaves_out_ignored_and_pending_and_unreadable()
	{
		PluginRow kept = Sample.Row("kept", Sample.Install("x", "2.0.0"), Sample.Install("y", "1.0.0"));
		PluginRow ignored = Sample.Row("ignored", Sample.Install("x", "2.0.0"), Sample.Install("y", "1.0.0"));
		PluginRow pending = Sample.Row("pending", Sample.Install("x", "2.0.0"), Sample.Install("y", "1.0.0"));
		PluginRow blocked = Sample.Row("blocked", Sample.Install("x", "2.0.0"), Sample.Install("bad", "1.0.0"));
		PluginRow onlineOnly = Sample.Row("online-only", Sample.Install("x", "1.0.0"), Sample.Install("y", "1.0.0"));
		WorkspaceSnapshot snapshot = Sample.Snapshot([Sample.Vault("x"), Sample.Vault("y"), Sample.Vault("bad", listUnreadable: true)], kept, ignored, pending, blocked, onlineOnly);

		IReadOnlyList<CopyPlugin> plan = AttentionBuilder.UpdateAll(Items(snapshot, "older:ignored"), snapshot, c => c.PluginId == "pending");

		Assert.Equal(["kept"], plan.Select(c => c.PluginId));
	}
}

public sealed class AuditRuleTests
{
	static readonly ProfileAudit Complete = new([], ["a"], [], [], ["f"], false);
	static readonly ProfileAudit Short = new([], ["a"], [], ["f"], [], false);

	[Theory]
	[InlineData(true, false, false, true, AuditResult.Matches)]
	[InlineData(true, false, true, false, AuditResult.Matches)]
	[InlineData(false, true, false, false, AuditResult.Missing)]
	[InlineData(false, false, false, false, AuditResult.NotUsing)]
	[InlineData(false, false, true, false, AuditResult.UsesOther)]
	public void One_plain_answer_for_a_vault(bool complete, bool here, bool elsewhere, bool _, AuditResult expected) =>
		Assert.Equal(expected, AuditRules.Of(complete ? Complete : Short, true, here, elsewhere));

	[Fact]
	public void An_offline_or_unread_vault_gets_no_verdict()
	{
		Assert.Equal(AuditResult.Offline, AuditRules.Of(Complete, false, true, false));
		Assert.Equal(AuditResult.Unknown, AuditRules.Of(null, true, true, false));
	}
}

public sealed class PlanTests
{
	[Fact]
	public void Update_copies_from_the_newest_vault_into_every_vault_behind()
	{
		PluginRow row = Sample.Row("a", Sample.Install("x", "2.0.0"), Sample.Install("y", "1.0.0", hash: "yh"), Sample.Install("z", "1.5.0"), Sample.Install("bad", "1.0.0"));
		WorkspaceSnapshot snapshot = Sample.Snapshot([Sample.Vault("x"), Sample.Vault("y"), Sample.Vault("z"), Sample.Vault("bad", listUnreadable: true)], row);

		List<CopyPlugin> items = Plans.UpdateBehind(row, snapshot).ToList();

		Assert.Equal([@"D:\v\y", @"D:\v\z"], items.Select(i => i.TargetVault));
		Assert.All(items, i => Assert.Equal(@"D:\v\x", i.SourceVault));
		Assert.All(items, i => Assert.False(i.CopySettings));
		Assert.Equal("yh", items[0].ExpectedTargetHash);
	}

	[Fact]
	public void Adding_a_plugin_expects_nothing_at_the_target()
	{
		PluginRow row = Sample.Row("a", Sample.Install("x", "2.0.0"));
		CopyPlugin item = Plans.AddTo(row, @"D:\v\y", copySettings: true)!;
		Assert.Null(item.FromVersion);
		Assert.Null(item.ExpectedTargetHash);
		Assert.True(item.CopySettings);
	}

	[Fact]
	public void A_plugin_with_no_copy_cannot_be_added()
	{
		PluginRow row = Sample.Row("a", Sample.Install("x", null, folder: false));
		Assert.Null(Plans.AddTo(row, @"D:\v\y", false));
	}

	[Fact]
	public void Filling_a_profile_copies_turns_on_lists_what_is_unavailable_and_adds_parts()
	{
		Profile profile = Sample.Profile(plugins: ["copyme", "turnon", "nowhere"], folders: ["00 System"]);
		PluginRow copy = Sample.Row("copyme", Sample.Install("x", "1.0.0"));
		PluginRow off = Sample.Row("turnon", Sample.Install("t", "1.0.0", listed: false));
		WorkspaceSnapshot snapshot = Sample.Snapshot([Sample.Vault("x"), Sample.Vault("t")], copy, off);
		var audit = new ProfileAudit(["copyme", "turnon", "nowhere"], [], [], ["00 System"], [], true);

		ProfileFill fill = Plans.Fill(profile, audit, Sample.Vault("t"), snapshot);

		Assert.Equal(["nowhere"], fill.Unavailable);
		Assert.Single(fill.Items.OfType<CopyPlugin>());
		Assert.True(fill.Items.OfType<SetPluginEnabled>().Single().Enabled);
		Assert.Single(fill.Items.OfType<AddProfileParts>());
	}

	[Fact]
	public void A_complete_structure_queues_no_parts()
	{
		var audit = new ProfileAudit([], ["a"], [], [], ["00 System"], false);
		ProfileFill fill = Plans.Fill(Sample.Profile(), audit, Sample.Vault("t"), Sample.Snapshot([Sample.Vault("t")]));
		Assert.Empty(fill.Items);
	}
}

public sealed class SmallRulesTests
{
	[Theory]
	[InlineData("", NewVaultProblem.EmptyName)]
	[InlineData("  ", NewVaultProblem.EmptyName)]
	[InlineData("a/b", NewVaultProblem.BadName)]
	[InlineData("..", NewVaultProblem.BadName)]
	[InlineData("CON", NewVaultProblem.BadName)]
	[InlineData("name.", NewVaultProblem.BadName)]
	[InlineData("Taken", NewVaultProblem.NameTaken)]
	[InlineData("taken", NewVaultProblem.NameTaken)]
	[InlineData("Fresh", NewVaultProblem.None)]
	public void New_vault_names_are_checked(string name, NewVaultProblem expected)
	{
		WorkspaceSnapshot snapshot = Sample.Snapshot([Sample.Vault("Taken")]);
		Assert.Equal(expected, NewVaultRules.Check(name, Path.Combine(Path.GetTempPath(), "parent"), snapshot, folderHasFiles: false, confinedTo: null));
	}

	[Fact]
	public void New_vault_parent_and_folder_are_checked()
	{
		WorkspaceSnapshot snapshot = Sample.Snapshot([]);
		Assert.Equal(NewVaultProblem.EmptyParent, NewVaultRules.Check("Fresh", " ", snapshot, false, null));
		Assert.Equal(NewVaultProblem.RelativeParent, NewVaultRules.Check("Fresh", "docs", snapshot, false, null));
		// Rooted paths of this machine: on Linux "D:\p" is a relative name.
		string root = Path.GetTempPath();
		string demo = Path.Combine(root, "demo");
		Assert.Equal(NewVaultProblem.FolderNotEmpty, NewVaultRules.Check("Fresh", Path.Combine(root, "p"), snapshot, true, null));
		Assert.Equal(NewVaultProblem.OutsideDemo, NewVaultRules.Check("Fresh", Path.Combine(root, "elsewhere"), snapshot, false, demo));
		Assert.Equal(NewVaultProblem.None, NewVaultRules.Check("Fresh", Path.Combine(demo, "Vaults"), snapshot, false, demo));
		Assert.Equal(NewVaultProblem.OutsideDemo, NewVaultRules.Check("Fresh", Path.Combine(root, "demo2", "Vaults"), snapshot, false, demo));
	}

	[Fact]
	public void A_free_profile_name_counts_up()
	{
		Assert.Equal("New profile", ProfileNames.Free("New profile", ["Other"]));
		Assert.Equal("New profile 2", ProfileNames.Free("New profile", ["new profile"]));
		Assert.Equal("New profile 3", ProfileNames.Free("New profile", ["New profile", "New profile 2"]));
	}

	[Theory]
	[InlineData(SyncDecision.Copy, true, ConflictChoice.None, true)]
	[InlineData(SyncDecision.Copy, false, ConflictChoice.None, false)]
	[InlineData(SyncDecision.Conflict, false, ConflictChoice.None, false)]
	[InlineData(SyncDecision.Conflict, false, ConflictChoice.TakeSource, true)]
	[InlineData(SyncDecision.Conflict, false, ConflictChoice.KeepTarget, false)]
	[InlineData(SyncDecision.KeepTarget, true, ConflictChoice.TakeSource, false)]
	[InlineData(SyncDecision.Same, true, ConflictChoice.TakeSource, false)]
	[InlineData(SyncDecision.BlockedSecret, true, ConflictChoice.TakeSource, false)]
	public void Only_copy_lines_and_chosen_conflicts_are_queued(SyncDecision decision, bool selected, ConflictChoice choice, bool expected) =>
		Assert.Equal(expected, SyncRules.WillQueue(decision, selected, choice));

	[Fact]
	public void Copy_lines_start_selected()
	{
		Assert.True(SyncRules.StartsSelected(SyncDecision.Copy));
		foreach (SyncDecision other in Enum.GetValues<SyncDecision>().Where(d => d != SyncDecision.Copy))
		{
			Assert.False(SyncRules.StartsSelected(other));
		}
	}

	[Fact]
	public void The_filter_finds_a_substring_and_puts_word_starts_first()
	{
		List<string> names = PluginFilter.Apply(["Metadata Menu", "Data Loom", "Dataview", "Calendar"], "data", n => n).ToList();
		Assert.Equal(["Dataview", "Data Loom", "Metadata Menu"], names);
		Assert.Equal(["Calendar"], PluginFilter.Apply(["Metadata", "Calendar"], "lend", n => n));
		Assert.Equal(2, PluginFilter.Apply(["a", "b"], "  ", n => n).Count());
	}

	[Fact]
	public void A_long_path_is_cut_in_the_middle()
	{
		string path = @"D:\Smth\Reps\TechTeaStudio\HyperionOmniClient\DOCS\obsidianVault\HyperionDocs";
		string cut = PathText.Middle(path, 40);
		Assert.Equal(40, cut.Length);
		Assert.StartsWith(@"D:\Smth", cut);
		Assert.EndsWith("HyperionDocs", cut);
		Assert.Contains("...", cut);
		Assert.Equal("short", PathText.Middle("short", 40));
	}

	[Fact]
	public void Links_are_encoded()
	{
		Assert.Equal("obsidian://open?vault=My%20Vault%20%26%20Co", Links.OpenVault(Path.Combine(Path.GetTempPath(), "Vaults", "My Vault & Co") + Path.DirectorySeparatorChar));
		Assert.Equal("https://community.obsidian.md/plugins/obsidian-git", Links.CatalogPage("obsidian-git"));
		Assert.Equal("https://community.obsidian.md/plugins/a%2Fb", Links.CatalogPage("a/b"));
	}

	[Fact]
	public void Start_options_are_read_from_the_arguments()
	{
		Assert.False(StartOptions.Parse([]).Demo);
		Assert.True(StartOptions.Parse(["--demo"]).Demo);
		Assert.True(StartOptions.Parse(["--smoke"]).Smoke);
		Assert.True(StartOptions.Parse(["--DEMO"]).Demo);
	}

	sealed record Item(string Key, int Value);

	[Fact]
	public void Sync_updates_in_place_keeps_instances_and_follows_the_order()
	{
		var first = new Row("a", 1);
		var second = new Row("b", 2);
		var third = new Row("c", 3);
		var rows = new ObservableCollection<Row> { first, second, third };

		int created = 0;
		CollectionSync.Sync(rows, [new Item("c", 30), new Item("a", 10), new Item("d", 40)], r => r.Key, i => i.Key,
			i =>
			{
				created++;
				return new Row(i.Key, i.Value);
			},
			(r, i) => r.Value = i.Value);

		Assert.Equal(["c", "a", "d"], rows.Select(r => r.Key));
		Assert.Same(third, rows[0]);
		Assert.Same(first, rows[1]);
		Assert.Equal(30, rows[0].Value);
		Assert.Equal(1, created);
	}

	sealed class Row(string key, int value)
	{
		public string Key { get; } = key;
		public int Value { get; set; } = value;
	}
}
