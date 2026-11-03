using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VaultManager.App.Controls;
using VaultManager.App.Localization;
using VaultManager.App.Logic;
using VaultManager.App.ViewModels;
using VaultManager.App.Views;
using VaultManager.Core.Changes;
using VaultManager.Core.Foundation;
using VaultManager.Core.Obsidian;
using VaultManager.Core.Profiles;
using VaultManager.Core.Sync;

namespace VaultManager.Tests.App;

/// <summary>
/// Every view and state, drawn against the demo world. It always renders, so a binding or template that
/// throws fails the build. With VM_SCREENSHOTS set it also writes a PNG per state for a person to look at.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class GalleryTests
{
	static readonly string? Output = Environment.GetEnvironmentVariable("VM_SCREENSHOTS");

	static void Draw(Window window, string name, double? width = null, double? height = null)
	{
		if (width is not null)
		{
			window.Width = width.Value;
		}

		if (height is not null)
		{
			window.Height = height.Value;
		}

		for (int i = 0; i < 4; i++)
		{
			Dispatcher.UIThread.RunJobs();
			AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		}

		Dispatcher.UIThread.RunJobs();
		using WriteableBitmap? frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		if (Output is not null && !name.StartsWith("z-temp"))
		{
			Directory.CreateDirectory(Output);
			frame!.Save(Path.Combine(Output, name + ".png"));
		}
	}

	static MainWindow Show(TestWorld world, double width = 1180, double height = 780)
	{
		var window = new MainWindow(world.Shell) { Width = width, Height = height };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		return window;
	}

	static void ToBottom<TView>(Window window)
		where TView : Control
	{
		Dispatcher.UIThread.RunJobs();
		window.GetVisualDescendants().OfType<TView>().First().GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
	}

	static AttentionRowVm Row(TestWorld world, string text) => world.Shell.Attention.Rows.First(r => r.Line.Any(s => s.Text == text));

	/// <summary>One line of each kind the panel can show: a real missing source, a moved target, an open vault.</summary>
	static async Task MakePanelLinesAsync(TestWorld world)
	{
		ShellViewModel shell = world.Shell;
		string notes = world.VaultNamed("Notes (Vaults)").Path;
		await shell.Changes.Run(Plans.AddTo(world.Plugin("calendar"), world.Vault("ChronosDocs"), false)!);
		CopyPlugin review = Plans.UpdateBehind(world.Plugin("obsidian-excalidraw-plugin"), shell.Data.Snapshot!).First(c => c.TargetVault == notes);
		File.AppendAllText(Path.Combine(VaultLayout.PluginDir(notes, "obsidian-excalidraw-plugin"), VaultLayout.MainFile), "// edited");
		await shell.Changes.Run([
			review,
			new CopyPlugin(world.Vault("HyperionDocs"), "gone", "Gone", null, "1.0.0", Path.Combine(world.Root, "nowhere"), false, null),
			Plans.UpdateBehind(world.Plugin("obsidian-git"), shell.Data.Snapshot!).First()]);
	}

	[Fact]
	public Task The_home_screen_and_its_states_render() => Ui.Run(async () =>
	{
		// The first thing the owner sees.
		using (TestWorld world = await TestWorld.StartAsync(demoFiles: true))
		{
			MainWindow window = Show(world);
			Draw(window, "01-home");

			Row(world, "Excalidraw").Toggle();
			Draw(window, "02-home-row-open");
			Row(world, "Excalidraw").Toggle();

			world.Shell.ToggleMenuCommand.Execute(null);
			Draw(window, "03-home-menu");
			world.Shell.CloseMenu();

			world.Shell.Log.ToggleCommand.Execute(null);
			Draw(window, "04-home-log-open");
			world.Shell.Log.ToggleCommand.Execute(null);

			world.Shell.Log.Add(LogKind.Problem, EnumTexts.Of(ChangeFailure.WriteFailed));
			Draw(window, "05-home-log-problem");

			Draw(window, "06-home-minimum-size", 1040, 640);
			window.Close();
		}

		// Update all with one vault open: it runs what it can, the rest waits.
		using (TestWorld world = await TestWorld.StartAsync(demoFiles: true))
		{
			MainWindow window = Show(world);
			world.Shell.Attention.Rows.First(r => r.Kind == AttentionKind.Older).Keep.Invoke();
			await world.Shell.UpdateAll();
			Draw(window, "07-update-all-some-wait");
			foreach (ChangeLineVm line in world.Shell.Changes.Lines.ToList())
			{
				line.RemoveCommand.Execute(null);
			}

			Draw(window, "08-undo-row-only");
			window.Close();
		}

		// Running: held on a real download mid-way.
		using (TestWorld world = await TestWorld.StartAsync(demoFiles: true))
		{
			MainWindow window = Show(world);
			world.Catalog.HoldDownload = new TaskCompletionSource();
			world.Catalog.DownloadStarted = new TaskCompletionSource();
			string target = world.Vault("ChronosDocs");
			Task run = world.Shell.Changes.Run([
				new SetPluginEnabled(target, "dataview", "Dataview", false),
				new DownloadPlugin(target, "calendar", "Calendar", null, "1.5.10", "demo/calendar", 742_357, null),
				new SetPluginEnabled(target, "waypoint", "Waypoint", false)]);
			await world.Catalog.DownloadStarted.Task;
			Dispatcher.UIThread.RunJobs();
			Draw(window, "09-running");
			world.Catalog.HoldDownload.SetResult();
			world.Catalog.HoldDownload = null;
			await run;
			window.Close();
		}

		// Every kind of line the panel can show.
		using (TestWorld world = await TestWorld.StartAsync(demoFiles: true))
		{
			MainWindow window = Show(world);
			await MakePanelLinesAsync(world);
			Draw(window, "10-panel-wait-fail-review");
			Draw(window, "11-panel-minimum-size", 1040, 640);
			window.Close();
		}

		// Many waiting lines: the panel scrolls inside itself, the vault list keeps its place.
		using (TestWorld world = await TestWorld.StartAsync(demoFiles: true))
		{
			MainWindow window = Show(world);
			string open = world.Vault(DemoName.Open);
			await world.Shell.Changes.Run(new[] { "omnisearch", "waypoint", "folder-notes", "obsidian-linter", "metadata-menu", "templater-obsidian" }.Select(id => Plans.AddTo(world.Plugin(id), open, false)!).ToList());
			Draw(window, "12-many-waiting");
			window.Close();
		}

		// Ignored rows, and the calm sentence.
		using (TestWorld world = await TestWorld.StartAsync(demoFiles: true))
		{
			MainWindow window = Show(world);
			world.Shell.Attention.Rows[0].Keep.Invoke();
			world.Shell.Attention.ToggleIgnoredCommand.Execute(null);
			Draw(window, "13-home-ignored");

			foreach (AttentionItem item in world.Shell.Attention.Items)
			{
				world.Host.Database.SetIgnored(item.Key, true);
			}

			world.Shell.Data.Ignored = world.Host.Database.Ignored();
			world.Shell.Attention.ToggleIgnoredCommand.Execute(null);
			world.Shell.Rebuild();
			Draw(window, "14-home-nothing-needed");
			window.Close();
		}
	});

	[Fact]
	public Task Online_all_plugins_and_vault_pages_render() => Ui.Run(async () =>
	{
		using TestWorld world = await TestWorld.StartAsync(demoFiles: true);
		ShellViewModel shell = world.Shell;
		MainWindow window = Show(world);

		await shell.Online.Start();
		Draw(window, "20-online-question");
		await shell.Online.Confirm();
		Draw(window, "21-online-result");
		await shell.Online.AskSizeAsync(shell.Data.CatalogEntry("obsidian-git")!, "2.39.0");
		shell.Attention.Rows.First(r => r.Kind == AttentionKind.NewerOnline).Toggle();
		Draw(window, "22-online-size-known");

		shell.ShowAll();
		Draw(window, "23-all-plugins");
		shell.Matrix.Filter = "data";
		Draw(window, "24-all-plugins-filter");
		shell.Matrix.Filter = string.Empty;

		Button cell = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Theme == Application.Current!.FindResource("AddCellButton"));
		cell.Focus(NavigationMethod.Tab);
		Draw(window, "25-all-plugins-add-cell-focused");
		Draw(window, "26-all-plugins-minimum-size", 1040, 640);
		window.Width = 1180;
		window.Height = 780;

		shell.ShowVault(world.VaultNamed(DemoName.Open).Id);
		Draw(window, "30-vault-open-with-older");
		shell.VaultPage.OlderRows[0].ExpandCommand.Execute(null);
		Draw(window, "31-vault-row-open");
		shell.ToggleMenuCommand.Execute(null);
		Draw(window, "32-vault-menu");
		shell.CloseMenu();
		Draw(window, "33-vault-minimum-size", 1040, 640);
		window.Width = 1180;
		window.Height = 780;

		shell.ShowVault(world.VaultNamed("Family").Id);
		Draw(window, "34-vault-normal");
		shell.Picker.Toggle();
		Draw(window, "35-picker-with-catalog");
		shell.Picker.Filter = "cal";
		await shell.Online.AskSizeAsync(shell.Data.CatalogEntry("calendar")!, null);
		Draw(window, "36-picker-filter");
		shell.Picker.Close();

		shell.ShowVault(world.VaultNamed("Family-restricted").Id);
		Draw(window, "37-vault-plugins-off");
		shell.ShowVault(world.VaultNamed("Future").Id);
		Draw(window, "38-vault-plugins-seem-off");
		shell.ShowVault(world.VaultNamed(DemoName.Offline).Id);
		Draw(window, "39-vault-offline");
		shell.ShowVault(world.VaultNamed(DemoName.Damaged).Id);
		Draw(window, "40-vault-unreadable-list");
		window.Close();
	});

	[Fact]
	public Task Profiles_new_vault_and_settings_render() => Ui.Run(async () =>
	{
		using TestWorld world = await TestWorld.StartAsync(demoFiles: true);
		ShellViewModel shell = world.Shell;
		MainWindow window = Show(world);

		world.Host.Database.SetVaultProfile(world.VaultNamed("ChronosDocs").Id, ProfileStore.BuiltInId);
		shell.ShowProfiles();
		await shell.RefreshAuditsAsync();
		Draw(window, "50-profiles");
		shell.Profiles.AuditRows.First(r => r.Name == "ChronosDocs").ToggleExpandCommand.Execute(null);
		Draw(window, "51-profiles-row-open");
		shell.ToggleMenuCommand.Execute(null);
		Draw(window, "52-profiles-menu");
		shell.CloseMenu();
		Draw(window, "53-profiles-minimum-size", 1040, 640);
		window.Width = 1180;
		window.Height = 780;

		shell.Profiles.Duplicate();
		await shell.RefreshAuditsAsync();
		Draw(window, "54-profile-editor");
		shell.Profiles.NewFolder = "..\\bad";
		shell.Profiles.AddFolderCommand.Execute(null);
		Draw(window, "55-profile-editor-problem");
		shell.Profiles.DoneEditing();
		shell.Profiles.AskDelete();
		Draw(window, "56-profile-delete-question");
		shell.Profiles.CancelDeleteCommand.Execute(null);

		string badFile = Path.Combine(world.Root, "exports", "bad" + ProfileFile.Extension);
		Directory.CreateDirectory(Path.GetDirectoryName(badFile)!);
		File.WriteAllText(badFile, "not json");
		await shell.Profiles.Import();
		Draw(window, "57-profile-import-rejected");

		shell.ShowNewVault();
		Draw(window, "60-new-vault-empty");
		shell.NewVault.Name = "TurbineDocs";
		await shell.NewVault.ProbeAsync();
		Draw(window, "61-new-vault-preview");
		shell.NewVault.Name = "Family";
		await shell.NewVault.ProbeAsync();
		Draw(window, "62-new-vault-name-taken");
		shell.NewVault.Choice = shell.NewVault.Choices.Last();
		shell.NewVault.Name = "EmptyOne";
		await shell.NewVault.ProbeAsync();
		Draw(window, "63-new-vault-empty-profile");

		SyncScene(world);
		shell.ShowVault(world.VaultNamed("HyperionDocs").Id);
		shell.MenuItems.First(m => m.Text == Texts.Get(K.Menu_CopySettings)).Invoke();
		Draw(window, "70-settings-start");
		shell.Settings.Source = shell.Settings.Sources.First(s => s.Name == "ChronosDocs");
		await shell.Settings.Compare();
		Draw(window, "71-settings-compared");

		// Keyboard focus shows.
		shell.ShowPlugins();
		Dispatcher.UIThread.RunJobs();
		for (int i = 0; i < 9; i++)
		{
			window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
			window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
		}

		Draw(window, "72-keyboard-focus");
		window.Close();
	});

	[Fact]
	public Task Russian_renders() => Ui.Run(async () =>
	{
		using TestWorld world = await TestWorld.StartAsync(demoFiles: true);
		ShellViewModel shell = world.Shell;
		MainWindow window = Show(world);
		world.Host.Database.SetVaultProfile(world.VaultNamed("ChronosDocs").Id, ProfileStore.BuiltInId);
		await shell.RefreshAuditsAsync();
		shell.UseLanguage(AppLanguage.Russian);
		Draw(window, "80-ru-home");
		Row(world, "Excalidraw").Toggle();
		Draw(window, "81-ru-home-row-open");
		Row(world, "Excalidraw").Toggle();
		shell.ToggleMenuCommand.Execute(null);
		Draw(window, "82-ru-home-menu");
		shell.CloseMenu();
		Draw(window, "83-ru-home-minimum-size", 1040, 640);
		window.Width = 1180;
		window.Height = 780;

		shell.ShowAll();
		Draw(window, "84-ru-all-plugins");
		shell.ShowVault(world.VaultNamed(DemoName.Open).Id);
		Draw(window, "85-ru-vault");
		shell.ShowVault(world.VaultNamed("Family").Id);
		shell.Picker.Toggle();
		Draw(window, "86-ru-picker");
		shell.Picker.Close();
		shell.ShowProfiles();
		await shell.Profiles.RefreshAuditAsync();
		Draw(window, "87-ru-profiles");
		Draw(window, "88-ru-profiles-minimum-size", 1040, 640);
		window.Width = 1180;
		window.Height = 780;
		shell.ShowNewVault();
		shell.NewVault.Name = "Новое";
		await shell.NewVault.ProbeAsync();
		Draw(window, "89-ru-new-vault");
		SyncScene(world);
		shell.ShowVault(world.VaultNamed("HyperionDocs").Id);
		shell.MenuItems.First(m => m.Text == Texts.Get(K.Menu_CopySettings)).Invoke();
		shell.Settings.Source = shell.Settings.Sources.First(s => s.Name == "ChronosDocs");
		await shell.Settings.Compare();
		Draw(window, "90-ru-settings");

		shell.ShowPlugins();
		await MakePanelLinesAsync(world);
		Draw(window, "91-ru-panel");
		window.Close();
	});

	sealed class HeldProcess : IObsidianProcess
	{
		public ManualResetEventSlim Release { get; } = new();

		public bool IsRunning
		{
			get
			{
				Release.Wait();
				return false;
			}
		}
	}

	[Fact]
	public Task States_outside_the_demo_data_render() => Ui.Run(async () =>
	{
		// Not scanned yet, and scanning: the first scan is held mid-way on a process check.
		var held = new HeldProcess();
		using (TestWorld world = TestWorld.Create(foreignProcess: held))
		{
			MainWindow window = Show(world);
			Draw(window, "A0-before-first-scan");
			Task scan = world.Shell.StartAsync();
			Draw(window, "A1-first-scan-running");
			Assert.True(world.Shell.IsScanning);
			held.Release.Set();
			await scan;
			Assert.False(world.Shell.IsScanning);
			Assert.False(world.Shell.IsDemo);
			Draw(window, "A2-normal-run-no-demo-mark");
			window.Close();
		}

		using (TestWorld world = TestWorld.Create())
		{
			File.Delete(Path.Combine(world.Paths.ObsidianConfigDir, "obsidian.json"));
			await world.Shell.StartAsync();
			MainWindow window = Show(world);
			Draw(window, "A3-no-vaults");
			world.Shell.ShowProfiles();
			Draw(window, "A4-no-vaults-profiles");
			window.Close();
		}

		using (TestWorld world = TestWorld.Create())
		{
			File.WriteAllText(Path.Combine(world.Paths.ObsidianConfigDir, "obsidian.json"), "{ not json");
			await world.Shell.StartAsync();
			MainWindow window = Show(world);
			Draw(window, "A5-vault-list-unreadable");
			window.Close();
		}

		// A vault whose scan failed, and community plugins state unknown.
		using (TestWorld world = TestWorld.Create())
		{
			Folder.Delete(Path.Combine(world.Paths.ObsidianConfigDir, "Local Storage"));
			await world.Shell.StartAsync();
			WorkspaceSnapshot snapshot = world.Shell.Data.Snapshot!;
			List<VaultState> vaults = snapshot.Vaults.Select(v => v.DisplayName == "Family" ? v with { ScanError = nameof(IOException) } : v).ToList();
			world.Shell.Data.Snapshot = snapshot with { Vaults = vaults };
			world.Shell.Rebuild();
			MainWindow window = Show(world);
			Draw(window, "A6-scan-error-home");
			world.Shell.ShowVault(world.VaultNamed("Family").Id);
			Draw(window, "A7-scan-error-vault");
			world.Shell.ShowVault(world.VaultNamed("ChronosDocs").Id);
			Assert.Equal(CommunityPluginsState.Unknown, world.VaultNamed("ChronosDocs").CommunityPlugins);
			Draw(window, "A8-community-plugins-unknown");
			window.Close();
		}

		using (TestWorld world = await TestWorld.StartAsync())
		{
			world.Catalog.Unreachable = true;
			await world.Shell.Online.Start();
			MainWindow window = Show(world);
			Draw(window, "A9-online-unreachable");
			window.Close();
		}
	});

	[Fact]
	public Task The_picker_answers_the_keyboard_in_a_real_window() => Ui.Run(async () =>
	{
		using TestWorld world = await TestWorld.StartAsync();
		ShellViewModel shell = world.Shell;
		shell.ShowVault(world.VaultNamed("Family").Id);
		MainWindow window = Show(world);

		shell.Picker.Toggle();
		Dispatcher.UIThread.RunJobs();
		TextBox filter = window.GetVisualDescendants().OfType<PickerView>().First().GetVisualDescendants().OfType<TextBox>().First();
		Assert.Same(filter, window.FocusManager!.GetFocusedElement());

		window.KeyTextInput("git");
		Dispatcher.UIThread.RunJobs();
		Assert.Equal("git", shell.Picker.Filter);
		Assert.Equal(["obsidian-git"], shell.Picker.Rows.Select(r => r.Id));

		window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
		window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
		Dispatcher.UIThread.RunJobs();
		Assert.True(shell.Picker.IsOpen);
		Assert.Same(filter, window.FocusManager.GetFocusedElement());
		// The copy starts at once, and on a fast machine it can finish and leave the queue inside RunJobs,
		// so the test checks what Enter did, not the line on its way.
		await shell.Changes.WhenIdle;
		Assert.Equal("2.38.2", Fixture.VersionOf(world.Vault("Family"), "obsidian-git"));

		window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
		window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
		Dispatcher.UIThread.RunJobs();
		Assert.False(shell.Picker.IsOpen);
		window.Close();
	});

	[Fact]
	public Task Navigation_works_by_real_clicks_and_a_second_click_keeps_the_section_pressed() => Ui.Run(async () =>
	{
		using TestWorld world = await TestWorld.StartAsync();
		MainWindow window = Show(world);
		Draw(window, "z-temp-nav");
		ToggleButton profiles = window.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Content as string == Texts.Get(K.Nav_Profiles));
		Point at = profiles.TranslatePoint(new Point(20, 10), window)!.Value;

		for (int i = 0; i < 2; i++)
		{
			window.MouseDown(at, MouseButton.Left);
			window.MouseUp(at, MouseButton.Left);
			Dispatcher.UIThread.RunJobs();
			Assert.True(world.Shell.IsProfilesView);
			Assert.True(profiles.IsChecked);
		}

		window.Close();
	});

	[Fact]
	public Task The_rail_holds_two_sections_and_the_new_vault_action() => Ui.Run(async () =>
	{
		using TestWorld world = await TestWorld.StartAsync();
		MainWindow window = Show(world);
		Draw(window, "z-temp-rail");
		List<string?> nav = window.GetVisualDescendants().OfType<ToggleButton>().Select(b => b.Content as string).ToList();
		Assert.Contains(Texts.Get(K.Nav_Plugins), nav);
		Assert.Contains(Texts.Get(K.Nav_Profiles), nav);
		Assert.Contains(Texts.Get(K.Rail_NewVault), nav);
		Assert.Equal(3, nav.Count);
		window.Close();
	});

	[Fact]
	public Task The_only_filled_button_on_the_home_screen_is_Update_all() => Ui.Run(async () =>
	{
		using TestWorld world = await TestWorld.StartAsync();
		MainWindow window = Show(world);
		Draw(window, "z-temp-filled");
		List<Button> filled = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("go")).ToList();
		Assert.Equal(Texts.Format(K.Action_UpdateAll, 4), Assert.Single(filled).Content);
		window.Close();
	});

	[Fact]
	public Task A_language_switch_makes_a_new_view_over_the_same_state() => Ui.Run(async () =>
	{
		using TestWorld world = await TestWorld.StartAsync();
		MainWindow window = Show(world);
		object? before = window.Content;

		world.Shell.UseLanguage(AppLanguage.Russian);
		Dispatcher.UIThread.RunJobs();

		Assert.NotSame(before, window.Content);
		Assert.Contains(window.GetVisualDescendants().OfType<ToggleButton>(), b => b.Content as string == "Плагины");
		window.Close();
	});

	[Fact]
	public Task The_log_opens_by_keyboard_and_the_strip_keeps_its_place() => Ui.Run(async () =>
	{
		using TestWorld world = await TestWorld.StartAsync();
		MainWindow window = Show(world);
		Draw(window, "z-temp-log");
		LogView view = window.GetVisualDescendants().OfType<LogView>().First();
		Button toggle = view.GetVisualDescendants().OfType<Button>().First();
		Assert.Equal(29, view.Bounds.Height);

		toggle.Focus(NavigationMethod.Tab);
		window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
		window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
		Draw(window, "z-temp-log-open");

		Assert.True(world.Shell.Log.IsExpanded);
		Assert.Equal(105, view.Bounds.Height);
		Assert.Same(toggle, window.FocusManager!.GetFocusedElement());

		window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
		window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
		Draw(window, "z-temp-log-closed");
		Assert.False(world.Shell.Log.IsExpanded);
		Assert.Equal(29, view.Bounds.Height);
		window.Close();
	});

	[Fact]
	public Task A_home_row_is_one_line_about_34_high_and_opens_by_keyboard() => Ui.Run(async () =>
	{
		using TestWorld world = await TestWorld.StartAsync();
		MainWindow window = Show(world);
		Draw(window, "z-temp-row");
		AttentionView view = window.GetVisualDescendants().OfType<AttentionView>().First();
		Button row = view.GetVisualDescendants().OfType<Button>().First(b => b.Command == world.Shell.Attention.Rows[0].ToggleCommand);
		Assert.Equal(34, row.Bounds.Height);

		row.Focus(NavigationMethod.Tab);
		window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
		window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
		Dispatcher.UIThread.RunJobs();

		Assert.True(world.Shell.Attention.Rows[0].IsExpanded);
		window.Close();
	});

	[Fact]
	public Task A_press_outside_closes_the_picker_except_on_the_Changes_panel() => Ui.Run(async () =>
	{
		using TestWorld world = await TestWorld.StartAsync();
		ShellViewModel shell = world.Shell;
		await shell.ToggleDemoObsidian();
		await shell.Changes.Run(Plans.AddTo(world.Plugin("calendar"), world.Vault("ChronosDocs"), false)!);
		shell.ShowVault(world.VaultNamed("Family").Id);
		MainWindow window = Show(world);
		shell.Picker.Toggle();
		Draw(window, "z-temp-picker-open");
		Point panel = window.GetVisualDescendants().OfType<ChangesPanel>().First().TranslatePoint(new Point(5, 5), window)!.Value;

		window.MouseDown(panel, MouseButton.Left);
		window.MouseUp(panel, MouseButton.Left);
		Dispatcher.UIThread.RunJobs();
		Assert.True(shell.Picker.IsOpen);

		window.MouseDown(new Point(500, 500), MouseButton.Left);
		window.MouseUp(new Point(500, 500), MouseButton.Left);
		Dispatcher.UIThread.RunJobs();
		Assert.False(shell.Picker.IsOpen);
		window.Close();
	});

	[Fact]
	public Task The_menu_closes_on_a_press_outside_and_on_Escape_and_takes_the_keyboard_along() => Ui.Run(async () =>
	{
		using TestWorld world = await TestWorld.StartAsync();
		ShellViewModel shell = world.Shell;
		MainWindow window = Show(world);
		Draw(window, "z-temp-menu");

		shell.ToggleMenuCommand.Execute(null);
		Draw(window, "z-temp-menu-open");
		Assert.IsType<Button>(window.FocusManager!.GetFocusedElement());

		window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
		window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
		Dispatcher.UIThread.RunJobs();
		Assert.False(shell.IsMenuOpen);

		shell.ToggleMenuCommand.Execute(null);
		Dispatcher.UIThread.RunJobs();
		window.MouseDown(new Point(500, 500), MouseButton.Left);
		window.MouseUp(new Point(500, 500), MouseButton.Left);
		Dispatcher.UIThread.RunJobs();
		Assert.False(shell.IsMenuOpen);
		window.Close();
	});

	[Fact]
	public Task An_empty_cell_offers_Add_on_keyboard_focus_and_the_table_scrolls_as_one() => Ui.Run(async () =>
	{
		using TestWorld world = await TestWorld.StartAsync();
		world.Shell.ShowAll();
		MainWindow window = Show(world);
		Draw(window, "z-temp-table");

		Button cell = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Theme == Application.Current!.FindResource("AddCellButton"));
		TextBlock word = cell.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "Word");
		Assert.False(word.IsVisible);

		cell.Focus(NavigationMethod.Tab);
		Dispatcher.UIThread.RunJobs();
		Assert.True(word.IsVisible);

		ScrollViewer body = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "BodyScroller");
		ScrollViewer head = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "HeaderScroller");
		body.Offset = new Vector(120, 0);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(120, head.Offset.X);
		window.Close();
	});

	/// <summary>Files that make Preview answer Same, Copy, KeepTarget, Conflict and BlockedSecret for ChronosDocs to HyperionDocs.</summary>
	internal static void SyncScene(TestWorld world)
	{
		string source = world.Vault("ChronosDocs"), target = world.Vault("HyperionDocs");
		string sourceConfig = VaultLayout.ConfigDir(source), targetConfig = VaultLayout.ConfigDir(target);

		void Both(string name, string text)
		{
			File.WriteAllText(Path.Combine(sourceConfig, name), text);
			File.WriteAllText(Path.Combine(targetConfig, name), text);
		}

		// Same.
		Both("core-plugins.json", """{"graph":true}""");
		// Copy: only the source has it.
		File.WriteAllText(Path.Combine(sourceConfig, "hotkeys.json"), """{"a":[]}""");
		File.Delete(Path.Combine(targetConfig, "hotkeys.json"));
		// Conflict: differs with no history (appearance.json is already different in the demo).
		// KeepTarget: agreed on the source's hash, then the target moved.
		File.WriteAllText(Path.Combine(sourceConfig, "app.json"), """{"x":1}""");
		File.WriteAllText(Path.Combine(targetConfig, "app.json"), """{"x":2}""");
		world.Host.Database.SetSyncBase(source, target, "app.json", SharedFile.Hash(Path.Combine(sourceConfig, "app.json")));
		// BlockedSecret.
		Directory.CreateDirectory(Path.Combine(sourceConfig, "snippets"));
		File.WriteAllText(Path.Combine(sourceConfig, "snippets", "keys.json"), """{"apiKey":"demo-not-a-real-key-000000"}""");
	}

	public static IEnumerable<object[]> EnumMembers()
	{
		foreach (ChangeStatus v in Enum.GetValues<ChangeStatus>())
		{
			yield return [EnumTexts.KeyOf(v)];
		}

		foreach (ChangeFailure v in Enum.GetValues<ChangeFailure>())
		{
			yield return [EnumTexts.KeyOf(v)];
		}

		foreach (ChangeNote v in Enum.GetValues<ChangeNote>())
		{
			yield return [EnumTexts.KeyOf(v)];
		}

		foreach (SyncDecision v in Enum.GetValues<SyncDecision>())
		{
			yield return [EnumTexts.KeyOf(v)];
		}

		foreach (ProfileProblem v in Enum.GetValues<ProfileProblem>())
		{
			yield return [EnumTexts.KeyOf(v)];
		}

		foreach (CommunityPluginsState v in Enum.GetValues<CommunityPluginsState>())
		{
			yield return [EnumTexts.KeyOf(v)];
		}

		foreach (SettingsCategory v in Enum.GetValues<SettingsCategory>())
		{
			yield return [EnumTexts.KeyOf(v)];
		}

		foreach (SettingsFate v in Enum.GetValues<SettingsFate>())
		{
			yield return [EnumTexts.KeyOf(v)];
		}

		foreach (NewVaultProblem v in Enum.GetValues<NewVaultProblem>())
		{
			yield return [EnumTexts.KeyOf(v)];
		}

		foreach (CellKind v in Enum.GetValues<CellKind>())
		{
			yield return [EnumTexts.KeyOf(v)];
		}
	}

	[Theory]
	[MemberData(nameof(EnumMembers))]
	public void Every_state_member_has_words_in_both_languages(string key)
	{
		Assert.False(string.IsNullOrWhiteSpace(Texts.Find(key, AppLanguage.English)));
		Assert.False(string.IsNullOrWhiteSpace(Texts.Find(key, AppLanguage.Russian)));
	}

	[Fact]
	public void Every_state_kind_has_a_colour_token() => Assert.All(Enum.GetValues<StateKind>(), kind => Assert.StartsWith("Brush.", StateGlyph.TokenOf(kind)));
}

static class DemoName
{
	public const string Open = VaultManager.Core.Demo.DemoWorkspace.OpenVault;
	public const string Offline = VaultManager.Core.Demo.DemoWorkspace.OfflineVault;
	public const string Damaged = VaultManager.Core.Demo.DemoWorkspace.DamagedVault;
}
