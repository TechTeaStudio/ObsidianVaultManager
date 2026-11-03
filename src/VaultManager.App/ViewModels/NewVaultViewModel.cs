using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VaultManager.App.Localization;
using VaultManager.App.Logic;
using VaultManager.Core.Changes;
using VaultManager.Core.Foundation;
using VaultManager.Core.Obsidian;
using VaultManager.Core.Profiles;

namespace VaultManager.App.ViewModels;

internal sealed record NewVaultProfileChoice(string? Id, string Name);

/// <summary>A new vault from a profile, previewed as you type. It is one more line in the queue, nothing more.</summary>
internal sealed partial class NewVaultViewModel(ShellViewModel shell) : ObservableObject
{
	int _probe;
	bool _folderHasFiles;
	bool _parentFilled;
	bool _updatingChoices;

	public ObservableCollection<NewVaultProfileChoice> Choices { get; } = [];

	[ObservableProperty] string _name = string.Empty;
	[ObservableProperty] string _parent = string.Empty;
	[ObservableProperty] NewVaultProfileChoice? _choice;
	[ObservableProperty] bool _register = true;
	[ObservableProperty] string _hint = string.Empty;
	[ObservableProperty] bool _hasHint;
	[ObservableProperty] bool _canAdd;
	[ObservableProperty] string _addText = string.Empty;
	[ObservableProperty] string _pluginsText = string.Empty;
	[ObservableProperty] string _sizeText = string.Empty;
	[ObservableProperty] string _downloadText = string.Empty;
	[ObservableProperty] string _unavailable = string.Empty;
	[ObservableProperty] bool _hasUnavailable;
	[ObservableProperty] string _tree = string.Empty;

	WorkspaceData Data => shell.Data;

	Profile? ChosenProfile => Choice?.Id is { } id ? Data.Profiles.FirstOrDefault(p => p.Id == id) : null;

	string VaultPath => Path.Combine(Parent.Trim(), Name.Trim());

	partial void OnNameChanged(string value) => _ = ProbeThenRebuildAsync();

	partial void OnParentChanged(string value) => _ = ProbeThenRebuildAsync();

	partial void OnChoiceChanged(NewVaultProfileChoice? value)
	{
		if (!_updatingChoices)
		{
			Rebuild();
		}
	}

	partial void OnRegisterChanged(bool value) => Rebuild();

	/// <summary>The drive may be slow or gone, so the check for an occupied folder runs off the UI thread; only the newest answer counts.</summary>
	async Task ProbeThenRebuildAsync()
	{
		int probe = ++_probe;
		string name = Name.Trim();
		string parent = Parent.Trim();
		bool occupied = false;
		if (name.Length > 0 && Path.IsPathFullyQualified(parent) && ProfileValidator.IsSafeRelative(name))
		{
			string path = Path.Combine(parent, name);
			occupied = await Task.Run(() =>
			{
				try
				{
					return File.Exists(path) || (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any());
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					return false;
				}
			});
		}

		if (probe == _probe)
		{
			_folderHasFiles = occupied;
			Rebuild();
		}
	}

	public Task ProbeAsync() => ProbeThenRebuildAsync();

	public void Rebuild()
	{
		UpdateChoices();
		if (!_parentFilled && Data.Snapshot?.Vaults.FirstOrDefault(v => v.IsOnline) is { } first)
		{
			_parentFilled = true;
			if (Parent.Length == 0)
			{
				Parent = Path.GetDirectoryName(first.Path.TrimEnd('\\', '/')) ?? string.Empty;
			}
		}

		NewVaultProblem problem = NewVaultRules.Check(Name, Parent, Data.Snapshot, _folderHasFiles, shell.Services.DemoRoot);
		bool queued = Name.Trim().Length > 0 && Path.IsPathFullyQualified(Parent.Trim()) && shell.Changes.Queue.IsCreatePending(VaultPath);
		HasHint = problem != NewVaultProblem.None;
		Hint = HasHint ? EnumTexts.Of(problem) : string.Empty;
		CanAdd = problem == NewVaultProblem.None && !queued;
		AddText = Texts.Get(queued ? K.Status_Waiting : K.NewVault_Add);

		Profile? profile = ChosenProfile;
		WorkspaceSnapshot? snapshot = Data.Snapshot;
		if (profile is null)
		{
			PluginsText = Texts.Get(K.NewVault_NoPlugins);
			SizeText = "0";
			HasUnavailable = false;
		}
		else
		{
			IReadOnlyDictionary<string, string> sources = Plans.PluginSources(profile, snapshot);
			List<string> names = sources.Values.Distinct(VaultManager.Core.Foundation.PathRules.Comparer).Select(v => PluginFacts.VaultName(snapshot, v)).ToList();
			PluginsText = sources.Count == 0
				? Texts.Get(K.NewVault_NoPlugins)
				: Texts.Format(K.NewVault_PluginsFrom, sources.Count, Texts.Join(names));
			long size = snapshot?.Plugins.Where(p => sources.ContainsKey(p.Id)).Sum(p => p.SizeBytes) ?? 0;
			SizeText = Formats.Size(size);
			List<string> absent = profile.Plugins.Where(p => !sources.ContainsKey(p)).ToList();
			HasUnavailable = absent.Count > 0;
			Unavailable = Texts.Format(K.NewVault_Unavailable, string.Join(", ", absent));
		}

		DownloadText = Texts.Get(K.NewVault_NothingDownload);
		Tree = BuildTree(profile, Plans.PluginSources(profile, snapshot).Count);
	}

	void UpdateChoices()
	{
		var wanted = new List<NewVaultProfileChoice>(Data.Profiles.Select(p => new NewVaultProfileChoice(p.Id, p.Name)))
		{
			new(null, Texts.Get(K.NewVault_EmptyProfile)),
		};
		string? selected = Choice?.Id;
		bool hadChoice = Choice is not null;
		_updatingChoices = true;
		CollectionSync.Sync(Choices, wanted, c => c.Id ?? string.Empty, c => c.Id ?? string.Empty, c => c, (_, _) => { });
		for (int i = 0; i < wanted.Count; i++)
		{
			if (Choices[i].Name != wanted[i].Name)
			{
				Choices[i] = wanted[i];
			}
		}

		Choice = hadChoice ? Choices.FirstOrDefault(c => c.Id == selected) ?? Choices[0] : Choices[0];
		_updatingChoices = false;
	}

	string BuildTree(Profile? profile, int pluginCount)
	{
		string root = Name.Trim().Length > 0 ? Name.Trim() : Texts.Get(K.NewVault_TreeRoot);
		var lines = new List<string> { root + "\\" };

		static string Pad(string text) => "  " + text.PadRight(18);

		if (profile is null)
		{
			lines.Add(Pad(".obsidian\\") + Texts.Get(K.NewVault_TreeDefaults));
		}
		else
		{
			lines.Add(Pad(".obsidian\\") + Texts.Format(K.NewVault_TreeSettings, Texts.Count(K.Plural.Plugins, pluginCount)));
			foreach (string folder in profile.Folders)
			{
				bool system = folder == VaultBuilder.SystemFolder;
				string note = system && profile.VaultMap
					? Texts.Get(K.NewVault_TreeMap)
					: profile.FolderNotes ? Texts.Get(K.NewVault_TreeNote) : string.Empty;
				lines.Add((Pad(folder + "\\") + note).TrimEnd());
			}
		}

		if (Register)
		{
			lines.Add(string.Empty);
			lines.Add(Texts.Get(K.NewVault_TreeList));
		}

		return string.Join("\n", lines);
	}

	[RelayCommand]
	public async Task Browse()
	{
		string? folder = await shell.Services.Files.PickFolderAsync();
		if (folder is not null)
		{
			Parent = folder;
		}
	}

	[RelayCommand]
	public void Add() => _ = AddAsync();

	/// <summary>Creates at once. When the folder is there, the form starts over; a failed create stays, with its reason in the panel.</summary>
	public async Task AddAsync()
	{
		if (!CanAdd)
		{
			return;
		}

		Profile? profile = ChosenProfile;
		var create = new CreateVault(Parent.Trim(), Name.Trim(), profile, Plans.PluginSources(profile, Data.Snapshot));
		var items = new List<ChangeItem> { create };
		if (Register)
		{
			items.Add(new RegisterVault(create.NewVaultPath));
		}

		await shell.Changes.Run(items);
		if (shell.Changes.Queue.Find(create.Id) is null)
		{
			Name = string.Empty;
			await ProbeAsync();
		}
	}
}
