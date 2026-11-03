---
type: index
status: active
priority: high
tags: [service, core]
aliases: [VaultManager.Core.Obsidian]
summary: "Reads Obsidian's own state per system: where its settings are, vault registry, enabled list, Restricted mode, versions, secrets and the workspace scan."
related:
  - "[[Vault Manager Architecture]]"
  - "[[Changes]]"
  - "[[Restricted mode read from browser storage]]"
  - "[[Registration waits while Obsidian runs]]"
  - "[[Linux support]]"
created: 2026-11-02
updated: 2026-11-03
---

# Obsidian

## Purpose

Everything Core knows about Obsidian's files and process. It reads the disk on each scan and keeps no state of its own, because the disk is the truth about a vault.

## Responsibilities

- Find Obsidian's settings folder, which differs by system and install, and read and extend the vault list in it, `obsidian.json`.
- Read and change one vault's `community-plugins.json`, refusing a file it could not read.
- Read Restricted mode from browser storage.
- Compare plugin versions as numbers and find the newest copy.
- Find settings that hold a live secret.
- Scan all vaults into one snapshot for the window.
- Detect whether Obsidian runs, and decide whether two paths name one vault.

## Dependencies

`Foundation` for atomic writes, shared reads, `ReadResult` and `PathRules`. Newtonsoft.Json.

## Key Types

| Type | What it does |
| :-- | :-- |
| `VaultLayout` | Paths inside a vault: `.obsidian`, `plugins`, `main.js`, `manifest.json`, `data.json` |
| `IObsidianProcess` | Whether Obsidian runs. `SystemObsidianProcess` matches a process named `obsidian` in any case |
| `ObsidianConfigLocator` | The settings folder. `Candidates` lists the places per system and install, `Find` picks the one written last |
| `HostOs`, `ObsidianInstall` | The system (`Windows`, `Linux`, `MacOS`) and the install (`Standard`, `Flatpak`, `Snap`) |
| `PathRules` (in `Foundation`) | Whether two paths name one vault. Case counts on Linux only |
| `VaultRegistry` | `Read` returns entries. `Register` adds a vault, keeps every other key, and returns the existing id if the path is already listed |
| `CommunityPluginList` | The enabled list. `Set` throws `ListUnreadable` rather than rewrite a file it could not read |
| `CommunityPluginsReader` | Whether community plugins are on, from browser storage: `On`, `Off` (Restricted mode), `NeverEnabled` (no key seen), `Unknown` |
| `PluginManifest` | Reads `manifest.json`; a bad file gives null, and a field of the wrong type reads as absent |
| `PluginVersion` | Numeric compare: 2.15.2 is newer than 2.8.3. A pre-release sorts below its release |
| `SecretScanner` | Walks JSON and returns key names only |
| `WorkspaceScanner` | Produces `WorkspaceSnapshot` from `VaultState` and `PluginRow` |

## Where Obsidian keeps its settings

`ObsidianConfigLocator` finds the folder that holds `obsidian.json` and the browser store, `Local Storage/leveldb`, which Restricted mode is read from. The folder depends on the system and the install:

| System | Install | Folder |
| :-- | :-- | :-- |
| Windows | Standard | `%APPDATA%\obsidian` |
| Linux | Standard, which the README lists as AppImage and deb | `$XDG_CONFIG_HOME/obsidian`, or `~/.config/obsidian` when `XDG_CONFIG_HOME` is unset or empty |
| Linux | Flatpak | `~/.var/app/md.obsidian.Obsidian/config/obsidian` |
| Linux | Snap | `~/snap/obsidian/current/.config/obsidian` |
| macOS | Standard | `~/Library/Application Support/obsidian`, in the code but in no release |

Flatpak and Snap keep their settings inside a sandbox, away from `~/.config`, so on Linux every place is tried. The one whose `obsidian.json` was written last wins, which keeps a second, forgotten install from winning. The write time is a stand-in for "used last", not a record of use. If no place has the file, the standard one is used. A place that cannot be read does not win.

The Flatpak and Snap paths follow Obsidian's documented locations. They have not been checked against a real install.

At start the log names the install and the folder on Linux, and on any system when the install is not standard.

## Process detection

`SystemObsidianProcess.IsRunning` reads each process name and passes it to `IsObsidian`. The match is the whole name `obsidian`, without case: `obsidian-cli` and `Obsidian Helper` do not count. `PlatformTests` pins this.

The name differs by system. Windows names the process `Obsidian`, and Linux names it `obsidian` and compares names by case. Before 0.6.0.3 the check looked for `Obsidian` case-sensitively, so on Linux it found nothing. An open vault looked closed, and a change could be written into it. See [[Linux support]].

## Path comparison

`PathRules` in `Foundation/Files.cs` decides whether two paths name one vault. On Linux the compare is ordinal, so `/home/a/Notes` and `/home/a/notes` are two vaults. Windows and macOS ignore case, as their usual file systems do.

`VaultRegistry.Register` uses the rule to find a vault that is already listed. The change executor, the change queue and the new vault checks use it too. The queue builds a line's dedupe key with `PathRules.Key`, so one vault's line is not queued twice.

## Details

- **What open means.** A vault is open only when `open` is true in `obsidian.json` and Obsidian also runs. The flag alone also marks the vault that was open when the app last closed. With Obsidian running and the list unreadable, `GateVault` counts every vault as open, because nothing proves it closed.
- **What `NeverEnabled` means.** No `enable-plugin-<vault id>` key was seen in a store that could be read. A compacted LevelDB file may compress a key so that it looks absent, so this is likely, not a fact. The window words it that way: `plugins seem to be off` on a row of the Plugins home, and `Community plugins seem to be turned off here` on the vault page, with the same hedge in Russian. The home row is shared with `Off`, so it hedges there too, while the vault page says `Community plugins are turned off here` for `Off`.
- **What a secret is.** The key name must match words such as token, password or api key, and the value must be a string of at least 8 characters, or a list or object holding one at any depth. So `"apiKeys": ["sk-..."]` and `"token": {"value": "sk-..."}` are found, while `rememberPassword` as a flag, `tokenizeUrls` and `"tokens": [1, 2, 3]` are not secrets. Unparsable settings cannot be cleared as safe and report `(unreadable)`.
- **Odd manifests.** A manifest is someone else's file. A field of the wrong type reads as absent instead of crashing a scan. A plugin whose `version` reads as absent is not a candidate for the newest copy, and the table shows it as broken.
- **What a scan finds.** `PluginRow` carries `NewestVersion` and `SourceVaultPath` (the newest copy, never the first vault), `Drifted`, and `SameVersionDifferentFiles` (same version with a different `main.js` hash).
- **One vault after another.** The scan is sequential on purpose, because external drives here drop under parallel load.
- **Offline vaults stay.** A vault whose folder is gone stays in the list with `IsOnline` false.
- **Display names.** Two folders with one name are told apart by their parent: `PC (CopyPc)`.

## Notable Decisions

- [[Restricted mode read from browser storage]]
- [[Registration waits while Obsidian runs]]
- [[Linux support]]

## Links

- [[Changes]] writes through these types.
- [[Sync]] and [[Profiles]] use `SecretScanner`.
