using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VaultManager.App.Controls;
using VaultManager.App.Localization;
using VaultManager.App.Logic;
using VaultManager.App.Services;
using VaultManager.Core;
using VaultManager.Core.Foundation;
using VaultManager.Core.Obsidian;
using VaultManager.Core.Profiles;
using VaultManager.Core.Store;

namespace VaultManager.App.ViewModels;

internal enum MainView { Plugins, Vault, Profiles, NewVault, Settings }

/// <summary>One line of the small menu behind the three dots.</summary>
internal sealed partial class MenuItemVm(string text, Action action, ShellViewModel shell, bool enabled = true, bool isCurrent = false) : ObservableObject
{
	public string Text { get; } = text;
	public bool IsEnabled { get; } = enabled;
	public bool IsCurrent { get; } = isCurrent;

	[RelayCommand]
	public void Invoke()
	{
		shell.CloseMenu();
		action();
	}
}

internal enum PluginsMode { Attention, All }

/// <summary>What the app knows between scans. Everything the window shows is derived from this and the queue.</summary>
internal sealed class WorkspaceData
{
	public WorkspaceSnapshot? Snapshot { get; set; }
	public IReadOnlySet<string> Ignored { get; set; } = new HashSet<string>();
	public IReadOnlyDictionary<string, BoundAudit> Audits { get; set; } = new Dictionary<string, BoundAudit>();
	public IReadOnlyList<Profile> Profiles { get; set; } = [];
	public IReadOnlyList<OnlineVersion> Online { get; set; } = [];
	public IReadOnlyList<CachedCatalogEntry> Catalog { get; set; } = [];
	public DateTimeOffset? CheckedAt { get; set; }

	/// <summary>As of the last look. Asking Windows for the process list on every redraw would be wasteful; the executor asks again before it writes.</summary>
	public bool ObsidianRunning { get; set; }

	public CachedCatalogEntry? CatalogEntry(string pluginId) => Catalog.FirstOrDefault(e => e.Id == pluginId);

	public string? OnlineVersionOf(string pluginId) => Online.FirstOrDefault(o => o.PluginId == pluginId)?.Version;
}

/// <summary>A button that a row may or may not offer right now. The command stays, so a rebuilt row keeps its focus.</summary>
internal sealed partial class ActionSlot : ObservableObject
{
	Action? _action;

	[ObservableProperty] string _text = string.Empty;
	[ObservableProperty] bool _isVisible;
	[ObservableProperty] bool _isEnabled = true;

	public ActionSlot() => Command = new RelayCommand(() => _action?.Invoke());

	public IRelayCommand Command { get; }

	public void Set(string text, Action action, bool enabled = true)
	{
		Text = text;
		_action = action;
		IsEnabled = enabled;
		IsVisible = true;
	}

	public void Hide()
	{
		_action = null;
		IsVisible = false;
	}

	public void Invoke() => _action?.Invoke();
}

internal sealed partial class VaultRowVm(string id, ShellViewModel shell) : ObservableObject
{
	public string Id { get; } = id;

	[ObservableProperty] string _name = string.Empty;
	[ObservableProperty] string _tip = string.Empty;
	[ObservableProperty] StateKind _glyph;
	[ObservableProperty] bool _hasGlyph;
	[ObservableProperty] string _note = string.Empty;
	[ObservableProperty] bool _noteIsDrift;
	[ObservableProperty] bool _isSelected;

	[RelayCommand]
	void Open() => shell.ShowVault(Id);
}

internal sealed partial class ShellViewModel : ObservableObject
{
	public ShellViewModel(AppServices services)
	{
		Services = services;
		Log = new LogViewModel();
		Data = new WorkspaceData();
		Changes = new ChangesViewModel(this);
		Attention = new AttentionViewModel(this);
		Matrix = new MatrixViewModel(this);
		VaultPage = new VaultPageViewModel(this);
		Picker = new PickerViewModel(this);
		Online = new OnlineViewModel(this);
		Profiles = new ProfilesViewModel(this);
		NewVault = new NewVaultViewModel(this);
		Settings = new SettingsSyncViewModel(this);
		LoadStored();
		Rebuild();
	}

	public AppServices Services { get; }
	public CoreHost Host => Services.Host;
	public WorkspaceData Data { get; }
	public LogViewModel Log { get; }
	public ChangesViewModel Changes { get; }
	public AttentionViewModel Attention { get; }
	public MatrixViewModel Matrix { get; }
	public VaultPageViewModel VaultPage { get; }
	public PickerViewModel Picker { get; }
	public OnlineViewModel Online { get; }
	public ProfilesViewModel Profiles { get; }
	public NewVaultViewModel NewVault { get; }
	public SettingsSyncViewModel Settings { get; }
	public ObservableCollection<VaultRowVm> Vaults { get; } = [];

	/// <summary>The window rebuilds its view on this. State stays in the view models.</summary>
	public event Action? LanguageChanged;

	public ObservableCollection<MenuItemVm> MenuItems { get; } = [];

	public string Version => Services.Version;
	public bool IsDemo => Services.IsDemo;

	public string? CurrentVaultId { get; private set; }

	public MainView View { get; private set; } = MainView.Plugins;

	public PluginsMode Mode { get; private set; } = PluginsMode.Attention;

	public bool IsPluginsView => View == MainView.Plugins;
	public bool IsVaultView => View == MainView.Vault;
	public bool IsProfilesView => View == MainView.Profiles;
	public bool IsNewVaultView => View == MainView.NewVault;
	public bool IsSettingsView => View == MainView.Settings;
	public bool IsAttentionMode => Mode == PluginsMode.Attention;
	public bool IsAllMode => Mode == PluginsMode.All;
	public bool ShowRefresh => IsPluginsView || IsVaultView;
	public bool CanGoBack => (IsPluginsView && IsAllMode) || IsSettingsView;
	public bool ShowProfileEdit => IsProfilesView && Profiles.IsEditable && !Profiles.IsEditing;
	public bool ShowProfileDone => IsProfilesView && Profiles.IsEditing;
	public bool ShowAttentionList => IsPluginsView && IsAttentionMode && HasWorkspace;
	public bool ShowMatrix => IsPluginsView && IsAllMode && HasWorkspace;
	public bool HasWorkspace => Data.Snapshot is { RegistryStatus: ReadStatus.Ok, Vaults.Count: > 0 };

	[ObservableProperty] bool _isScanning;
	[ObservableProperty] bool _scanFailed;
	[ObservableProperty] string _headTitle = string.Empty;
	[ObservableProperty] string _headTail = string.Empty;
	[ObservableProperty] string _headSub = string.Empty;
	[ObservableProperty] bool _hasHeadSub;
	[ObservableProperty] string _headSubTip = string.Empty;
	[ObservableProperty] string _scanTip = string.Empty;
	[ObservableProperty] bool _showUpdateAll;
	[ObservableProperty] string _updateAllText = string.Empty;
	[ObservableProperty] bool _isMenuOpen;
	[ObservableProperty] string _emptyTitle = string.Empty;
	[ObservableProperty] bool _showEmpty;
	[ObservableProperty] bool _emptyCanAddFolder;
	[ObservableProperty] bool _emptyCanRetry;
	[ObservableProperty] string _vaultsNote = string.Empty;
	[ObservableProperty] bool _hasVaultsNote;
	[ObservableProperty] string _demoObsidianText = string.Empty;

	// One view is shown at a time; the others stay alive so their scroll, text and selection survive.
	public void RaiseViewFlags()
	{
		OnPropertyChanged(nameof(IsPluginsView));
		OnPropertyChanged(nameof(IsVaultView));
		OnPropertyChanged(nameof(IsProfilesView));
		OnPropertyChanged(nameof(IsNewVaultView));
		OnPropertyChanged(nameof(IsSettingsView));
		OnPropertyChanged(nameof(IsAttentionMode));
		OnPropertyChanged(nameof(IsAllMode));
		OnPropertyChanged(nameof(ShowRefresh));
		OnPropertyChanged(nameof(CanGoBack));
		OnPropertyChanged(nameof(ShowProfileEdit));
		OnPropertyChanged(nameof(ShowProfileDone));
		OnPropertyChanged(nameof(ShowAttentionList));
		OnPropertyChanged(nameof(ShowMatrix));
		OnPropertyChanged(nameof(HasWorkspace));
	}

	void LoadStored()
	{
		AppDatabase db = Host.Database;
		Data.Ignored = db.Ignored();
		Data.Online = db.OnlineVersions();
		Data.Catalog = db.Catalog();
		Data.Profiles = Host.Profiles.List();
		Online.LoadCheckedAt();
		Changes.RefreshUndo();
		Changes.LoadPersisted();
	}

	public async Task StartAsync()
	{
		// Off Windows Obsidian may live in a sandbox; the log says which settings this run reads.
		if (!OperatingSystem.IsWindows() || Host.Paths.Install != VaultManager.Core.Obsidian.ObsidianInstall.Standard)
		{
			Log.Add(LogKind.Plain, Texts.Format(K.Log_ObsidianFrom, EnumTexts.Of(Host.Paths.Install), Host.Paths.ObsidianConfigDir));
		}

		await RescanAsync(log: true);

		// Lines from the last run of the app go as soon as their gate is open.
		await Changes.RecheckWaitingAsync();
	}

	[RelayCommand]
	Task Rescan() => RescanAsync(log: true);

	public async Task RescanAsync(bool log)
	{
		if (IsScanning)
		{
			// The running scan may have read a file before the change that asked for this one.
			_scanAgain = true;
			return;
		}

		IsScanning = true;
		UpdateRail();
		UpdateHead();
		try
		{
			ScanOutcome outcome = await Task.Run(ScanEverything);
			Data.Snapshot = outcome.Snapshot;
			Data.Profiles = outcome.Profiles;
			Data.Audits = outcome.Audits;
			Data.ObsidianRunning = outcome.Running;
			ScanFailed = false;
			if (log)
			{
				LogScanResult(outcome.Snapshot);
			}
		}
		catch (Exception ex)
		{
			// Whatever a vault's files hold, the window must say so instead of staying on "not scanned".
			ScanFailed = true;
			Log.Add(LogKind.Problem, Texts.Format(K.Log_ScanFailed, ex.GetType().Name));
		}
		finally
		{
			IsScanning = false;
		}

		Rebuild();
		await Profiles.RefreshAuditAsync();
		if (_scanAgain)
		{
			_scanAgain = false;
			await RescanAsync(log: false);
		}
	}

	bool _scanAgain;

	void LogScanResult(WorkspaceSnapshot snapshot)
	{
		switch (snapshot.RegistryStatus)
		{
			case ReadStatus.Ok:
				Log.Add(LogKind.Plain, Texts.Format(K.Log_Scanned, Texts.Count(K.Plural.Vaults, snapshot.Vaults.Count), Texts.Count(K.Plural.Plugins, snapshot.Plugins.Count)));
				break;
			case ReadStatus.Unreadable:
				Log.Add(LogKind.Problem, Texts.Get(K.Empty_Unreadable));
				break;
			default:
				Log.Add(LogKind.Plain, Texts.Get(K.Empty_NoVaults));
				break;
		}
	}

	sealed record ScanOutcome(WorkspaceSnapshot Snapshot, IReadOnlyList<Profile> Profiles, IReadOnlyDictionary<string, BoundAudit> Audits, bool Running);

	// Runs on a background thread, one vault after another: the external drives here drop under parallel load.
	ScanOutcome ScanEverything()
	{
		bool running = Host.Process.IsRunning;
		WorkspaceSnapshot snapshot = Host.Scanner.Scan();
		IReadOnlyList<Profile> profiles = Host.Profiles.List();
		return new ScanOutcome(snapshot, profiles, BoundAudits(snapshot, profiles), running);
	}

	IReadOnlyDictionary<string, BoundAudit> BoundAudits(WorkspaceSnapshot snapshot, IReadOnlyList<Profile> profiles)
	{
		var audits = new Dictionary<string, BoundAudit>();
		foreach (VaultState vault in snapshot.Vaults.Where(v => v.IsOnline))
		{
			string? profileId = Host.Database.VaultProfile(vault.Id);
			Profile? profile = profiles.FirstOrDefault(p => p.Id == profileId);
			if (profile is not null)
			{
				audits[vault.Id] = new BoundAudit(profile, ProfileAudit.For(profile, vault.Path, PluginFacts.EnabledIn(snapshot, vault.Id).ToList()));
			}
		}

		return audits;
	}

	Task _auditChain = Task.CompletedTask;

	/// <summary>
	/// After a profile changed or a vault was bound. Reads folders, so it runs off the UI thread,
	/// and requests take turns: the external drives here drop under parallel load.
	/// </summary>
	public Task RefreshAuditsAsync()
	{
		_auditChain = RunAfter(_auditChain);
		return _auditChain;

		async Task RunAfter(Task before)
		{
			await before.ContinueWith(_ => { }, TaskScheduler.Default);
			await RefreshAuditsNowAsync();
		}
	}

	async Task RefreshAuditsNowAsync()
	{
		WorkspaceSnapshot? snapshot = Data.Snapshot;
		IReadOnlyList<Profile> profiles = Data.Profiles = Host.Profiles.List();
		if (snapshot is not null)
		{
			Data.Audits = await Task.Run(() => BoundAudits(snapshot, profiles));
		}

		Rebuild();
		await Profiles.RefreshAuditAsync();
	}

	/// <summary>Brings every row in line with the data. Rows are updated in place, never recreated.</summary>
	public void Rebuild()
	{
		if (View == MainView.Vault && Data.Snapshot is not null && CurrentVault is null)
		{
			// The vault left Obsidian's list since the last scan; there is no page to stay on.
			View = MainView.Plugins;
			CurrentVaultId = null;
			Picker.Close();
		}

		UpdateRail();
		Changes.Rebuild();
		Log.Rebuild();
		Attention.Rebuild();
		Matrix.Rebuild();
		VaultPage.Rebuild();
		Picker.Rebuild();
		Profiles.Rebuild();
		NewVault.Rebuild();
		Settings.Rebuild();
		UpdateHead();
		UpdateMenu();
		RaiseViewFlags();
	}

	void UpdateRail()
	{
		WorkspaceSnapshot? snapshot = Data.Snapshot;
		IReadOnlyList<VaultState> vaults = snapshot?.Vaults ?? [];
		CollectionSync.Sync(Vaults, vaults, r => r.Id, v => v.Id, v => new VaultRowVm(v.Id, this), (_, _) => { });
		foreach (VaultRowVm row in Vaults)
		{
			if (snapshot is not null && vaults.FirstOrDefault(v => v.Id == row.Id) is { } vault)
			{
				UpdateVaultRow(row, vault);
			}
		}

		HasVaultsNote = vaults.Count == 0;
		VaultsNote = snapshot is null
			? Texts.Get(IsScanning ? K.Rail_Scanning : K.Rail_NotScanned)
			: snapshot.RegistryStatus == ReadStatus.Unreadable ? Texts.Get(K.Rail_Unreadable) : Texts.Get(K.Rail_NoVaults);
		DemoObsidianText = Services.Demo?.IsRunning == true ? Texts.Get(K.Demo_ObsidianRunning) : Texts.Get(K.Demo_ObsidianClosed);
	}

	void UpdateVaultRow(VaultRowVm row, VaultState vault)
	{
		WorkspaceSnapshot snapshot = Data.Snapshot!;
		int older = snapshot.Plugins.Count(p => p.In(vault.Id) is { } i && PluginFacts.IsBehind(p, i));
		row.Name = vault.DisplayName;
		row.Tip = vault.Path;
		row.IsSelected = View == MainView.Vault && CurrentVaultId == vault.Id;
		(row.Glyph, row.HasGlyph, row.Note, row.NoteIsDrift) = VaultRowState(vault, older, Data.ObsidianRunning);
	}

	static (StateKind Glyph, bool HasGlyph, string Note, bool Drift) VaultRowState(VaultState vault, int older, bool running)
	{
		if (!vault.IsOnline)
		{
			return (StateKind.Offline, true, Texts.Get(K.Vault_Offline), false);
		}

		if (vault.ListUnreadable || vault.ScanError is not null)
		{
			return (StateKind.Blocked, true, Texts.Get(K.Vault_Unreadable), false);
		}

		// The scan saw it open; the slow timer may know since that Obsidian closed.
		string open = vault.IsOpen && running ? Texts.Get(K.Vault_Open) : string.Empty;
		string olderText = older > 0 ? Texts.Format(K.Vault_OlderCount, older) : string.Empty;
		string note = open.Length > 0 && olderText.Length > 0 ? open + " · " + olderText : open + olderText;
		if (vault.CommunityPlugins is CommunityPluginsState.Off or CommunityPluginsState.NeverEnabled)
		{
			return (StateKind.Blocked, true, note, older > 0);
		}

		return older > 0 ? (StateKind.Older, true, note, true) : (StateKind.None, false, note, false);
	}

	void UpdateHead()
	{
		WorkspaceSnapshot? snapshot = Data.Snapshot;
		HeadTail = string.Empty;
		HeadSub = string.Empty;
		HeadSubTip = string.Empty;
		switch (View)
		{
			case MainView.Vault when CurrentVault is { } vault:
				int total = snapshot!.Plugins.Count(p => p.In(vault.Id) is not null);
				int older = snapshot.Plugins.Count(p => p.In(vault.Id) is { } i && PluginFacts.IsBehind(p, i));
				HeadTitle = vault.DisplayName;
				HeadTail = "· " + Texts.Count(K.Plural.Plugins, total) + (older > 0 ? ", " + Texts.Format(K.Vault_OlderCount, older) : string.Empty);
				HeadSub = PathText.Middle(vault.Path, 96);
				HeadSubTip = vault.Path;
				break;
			case MainView.Profiles:
				HeadTitle = Profiles.HeadLine();
				break;
			case MainView.NewVault:
				HeadTitle = Texts.Get(K.NewVault_Title);
				break;
			case MainView.Settings:
				HeadTitle = Settings.Target is { } target ? Texts.Format(K.Sync_TitleTo, target.Name) : Texts.Get(K.Sync_Title);
				break;
			default:
				HeadTitle = IsAllMode ? Texts.Get(K.Head_AllPlugins) : Attention.HeadLine();
				break;
		}

		HasHeadSub = HeadSub.Length > 0;
		string scan = IsScanning
			? Texts.Get(K.Head_Scanning)
			: ScanFailed
				? Texts.Get(K.Head_ScanFailed)
				: snapshot is null
					? Texts.Get(K.Head_NotScanned)
					: Texts.Format(K.Head_Scanned, Formats.Time(snapshot.ScannedAt));
		ScanTip = scan + " " + (Data.CheckedAt is { } at ? Texts.Format(K.Head_CheckedOnline, Formats.Time(at)) : Texts.Get(K.Head_NotCheckedOnline));

		int updates = IsPluginsView && IsAttentionMode ? Attention.UpdateAllPlan.Count : 0;
		ShowUpdateAll = updates > 0;
		UpdateAllText = Texts.Get(K.Action_UpdateAll);
		UpdateEmpty(snapshot);
	}

	public void RefreshHead() => UpdateHead();

	/// <summary>Every older plugin with a newer copy on this PC, except what was set aside. It only copies, so it never needs the network.</summary>
	[RelayCommand]
	public Task UpdateAll() => Changes.Run(Attention.UpdateAllPlan);

	[RelayCommand]
	void ToggleMenu() => IsMenuOpen = !IsMenuOpen;

	public void CloseMenu() => IsMenuOpen = false;

	void UpdateMenu()
	{
		// A rebuild from a background result must not pull the items out from under the pointer.
		if (IsMenuOpen)
		{
			return;
		}

		MenuItems.Clear();
		switch (View)
		{
			case MainView.Vault:
				MenuItems.Add(new MenuItemVm(Texts.Get(K.Action_OpenInObsidian), OpenInObsidian, this, VaultPage.CanOpen));
				MenuItems.Add(new MenuItemVm(Texts.Get(K.Menu_CopySettings), () => ShowSettingsFor(CurrentVaultId!), this, VaultPage.CanOpen));
				break;
			case MainView.Profiles:
				MenuItems.Add(new MenuItemVm(Texts.Get(K.Profile_New), Profiles.NewProfile, this));
				MenuItems.Add(new MenuItemVm(Texts.Get(K.Profile_Duplicate), Profiles.Duplicate, this));
				MenuItems.Add(new MenuItemVm(Texts.Get(K.Profile_Import), () => _ = Profiles.Import(), this));
				MenuItems.Add(new MenuItemVm(Texts.Get(K.Profile_Export), () => _ = Profiles.Export(), this));
				MenuItems.Add(new MenuItemVm(Texts.Get(K.Profile_Delete), Profiles.AskDelete, this, Profiles.IsEditable));
				break;
			case MainView.Plugins:
				MenuItems.Add(new MenuItemVm(Texts.Get(K.Head_CheckOnline), () => _ = Online.Start(), this));
				if (Changes.OlderUndo is { } older)
				{
					MenuItems.Add(new MenuItemVm(Texts.Format(K.Menu_UndoLast, Formats.Time(older.At)), () => _ = Changes.Undo(), this));
				}

				MenuItems.Add(IsAllMode
					? new MenuItemVm(Texts.Get(K.Menu_Home), ShowAttention, this)
					: new MenuItemVm(Texts.Get(K.Head_AllPlugins), ShowAll, this));
				MenuItems.Add(new MenuItemVm(Texts.Get(K.Language_English), () => UseLanguage(AppLanguage.English), this, isCurrent: Texts.Current == AppLanguage.English));
				MenuItems.Add(new MenuItemVm(Texts.Get(K.Language_Russian), () => UseLanguage(AppLanguage.Russian), this, isCurrent: Texts.Current == AppLanguage.Russian));
				break;
		}

		OnPropertyChanged(nameof(HasMenu));
	}

	public bool HasMenu => MenuItems.Count > 0;

	void UpdateEmpty(WorkspaceSnapshot? snapshot)
	{
		ShowEmpty = !HasWorkspace && View is MainView.Plugins or MainView.Vault;
		if (snapshot is null)
		{
			EmptyTitle = Texts.Get(IsScanning ? K.Rail_Scanning : K.Rail_NotScanned);
			EmptyCanAddFolder = false;
			EmptyCanRetry = false;
			return;
		}

		bool unreadable = snapshot.RegistryStatus == ReadStatus.Unreadable;
		EmptyTitle = Texts.Get(unreadable ? K.Empty_Unreadable : K.Empty_NoVaults);
		EmptyCanRetry = unreadable;
		EmptyCanAddFolder = !unreadable;
	}

	public VaultState? CurrentVault => Data.Snapshot?.Vaults.FirstOrDefault(v => v.Id == CurrentVaultId);

	void Go(MainView view, string? vaultId = null)
	{
		View = view;
		CurrentVaultId = vaultId;
		if (view != MainView.Vault)
		{
			Picker.Close();
		}

		Online.CloseQuestion();
		CloseMenu();
		Rebuild();
	}

	public void ShowVault(string id)
	{
		Picker.Close();
		Go(MainView.Vault, id);
	}

	[RelayCommand]
	public void ShowPlugins()
	{
		Mode = PluginsMode.Attention;
		Go(MainView.Plugins);
	}

	[RelayCommand]
	public void ShowProfiles() => Go(MainView.Profiles);

	[RelayCommand]
	public void ShowNewVault() => Go(MainView.NewVault);

	[RelayCommand]
	public void ShowSettings() => Go(MainView.Settings);

	/// <summary>Copy settings from... on a vault page: this vault is the target, and Back returns to it.</summary>
	public void ShowSettingsFor(string vaultId)
	{
		_settingsFrom = vaultId;
		Go(MainView.Settings);
		Settings.SetFixedTarget(vaultId);
		Rebuild();
	}

	string? _settingsFrom;

	[RelayCommand]
	public void Back()
	{
		if (IsSettingsView && _settingsFrom is { } vault)
		{
			ShowVault(vault);
		}
		else
		{
			ShowAttention();
		}
	}

	[RelayCommand]
	public void ShowAttention()
	{
		Mode = PluginsMode.Attention;
		Go(MainView.Plugins);
	}

	[RelayCommand]
	public void ShowAll()
	{
		Mode = PluginsMode.All;
		Go(MainView.Plugins);
	}

	[RelayCommand]
	public void OpenInObsidian()
	{
		if (CurrentVault is { } vault)
		{
			OpenVaultInObsidian(vault);
		}
	}

	public void OpenVaultInObsidian(VaultState vault)
	{
		string uri = Links.OpenVault(vault.Path);
		OpenLink(uri);
	}

	/// <summary>The only way out to the browser or Obsidian, and it only ever follows a click.</summary>
	public void OpenLink(string uri)
	{
		try
		{
			Services.Shell.Open(uri);
			Log.Add(LogKind.Plain, Texts.Format(IsDemo ? K.Demo_WouldOpen : K.Log_Opened, uri));
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			Log.Add(LogKind.Problem, Texts.Format(K.Log_OpenFailed, uri));
		}
	}

	public void UseLanguage(AppLanguage language)
	{
		if (Texts.Current == language)
		{
			return;
		}

		Texts.Use(language);
		Host.Database.SetSetting(Texts.SettingKey, Texts.CodeOf(language));
		Rebuild();
		LanguageChanged?.Invoke();
	}

	public static void LoadLanguage(AppDatabase database) => Texts.Use(Texts.FromCode(database.Setting(Texts.SettingKey)));

	// Add a folder from the empty state: it goes through the queue like any other write to Obsidian's list.
	[RelayCommand]
	async Task AddFolder()
	{
		string? folder = await Services.Files.PickFolderAsync();
		if (folder is not null)
		{
			await Changes.Run(new VaultManager.Core.Changes.RegisterVault(folder));
		}
	}

	[RelayCommand]
	public async Task ToggleDemoObsidian()
	{
		if (Services.Demo is not { } demo)
		{
			return;
		}

		demo.IsRunning = !demo.IsRunning;
		Log.Add(LogKind.Plain, Texts.Get(demo.IsRunning ? K.Demo_LogOpened : K.Demo_LogClosed));
		await RescanAsync(log: false);
		await Changes.RecheckWaitingAsync();
	}

	/// <summary>
	/// Called from a slow timer. Asking Windows for its process list is not a disk read; everything
	/// beyond that happens only while lines wait, because the drives here are slow and may be asleep.
	/// </summary>
	public async Task WatchObsidianAsync()
	{
		bool running = await Task.Run(() => Host.Process.IsRunning);
		bool changed = running != Data.ObsidianRunning;
		Data.ObsidianRunning = running;
		if (Changes.Queue.WaitingCount > 0)
		{
			await Changes.RecheckWaitingAsync();
		}

		if (changed)
		{
			Rebuild();
		}
	}
}

internal enum LogKind { Plain, Ok, Problem }

internal sealed record LogEntry(string Time, string Text, LogKind Kind)
{
	public bool IsOk => Kind == LogKind.Ok;
	public bool IsProblem => Kind == LogKind.Problem;
	public bool IsPlain => Kind == LogKind.Plain;
}

/// <summary>The bottom strip: the latest event on one line, the whole log on a click.</summary>
internal sealed partial class LogViewModel : ObservableObject
{
	const int Keep = 300;

	public ObservableCollection<LogEntry> Entries { get; } = [];

	[ObservableProperty] bool _isExpanded;
	[ObservableProperty] string _latestTime = string.Empty;
	[ObservableProperty] string _latestText = string.Empty;
	[ObservableProperty] bool _latestIsProblem;
	[ObservableProperty] bool _latestIsOk;
	[ObservableProperty] bool _showLatest = true;
	[ObservableProperty] string _toggleTip = string.Empty;

	partial void OnIsExpandedChanged(bool value) => Rebuild();

	[RelayCommand]
	public void Toggle() => IsExpanded = !IsExpanded;

	public void Add(LogKind kind, string text)
	{
		var entry = new LogEntry(Formats.Clock(DateTimeOffset.Now), text, kind);
		Entries.Add(entry);
		while (Entries.Count > Keep)
		{
			Entries.RemoveAt(0);
		}

		LatestTime = entry.Time;
		LatestText = entry.Text;
		LatestIsProblem = entry.IsProblem;
		LatestIsOk = entry.IsOk;
	}

	public void Rebuild()
	{
		ShowLatest = !IsExpanded;
		ToggleTip = Texts.Get(IsExpanded ? K.Log_Collapse : K.Log_Expand);
	}
}
