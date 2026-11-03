---
type: note
status: active
priority: high
tags: [architecture, overview]
aliases: [Architecture Overview]
summary: "Core and App split, how a click flows to executor, snapshot and undo, how waiting lines finish by themselves, the window's zones, what SQLite holds and what stays as files."
related:
  - "[[Changes]]"
  - "[[Store]]"
  - "[[Obsidian]]"
  - "[[Profiles as files, everything else in SQLite]]"
  - "[[Run on click instead of plan then Apply]]"
created: 2026-11-02
updated: 2026-11-03
---

# Vault Manager Architecture

## Summary

A tested core holds the rules and a thin Avalonia window shows them. An action runs on click through the executor. Each line reports for itself, a copy is saved first, and Undo survives a restart. The disk is the truth about a vault; the app's own memory lives outside every vault.

## Core and App

| Project | Holds | Rule |
| :-- | :-- | :-- |
| `src/VaultManager.Core` | The rules. No UI references. Targets net6.0, net8.0, net9.0, net10.0 | Rules go here, test first |
| `src/VaultManager.App` | Avalonia window: thin views, view models, `Logic/` (queue, attention, plans), `Themes/Basalt.axaml`, resx | A decision in a view is a bug |
| `tests/VaultManager.Tests` | xunit on real temp folders, headless Avalonia for the window | Never touches the real app data, Obsidian settings or the network. On Windows that is `%APPDATA%` and `%LOCALAPPDATA%`, on Linux `~/.config` and `~/.local/share` |

`CoreHost` wires Core once from three inputs, and these are the seams that the demo and the tests replace:

- `AppPaths` - where Obsidian's config and the app's data are
- `IObsidianProcess` - whether Obsidian runs
- `ICatalogClient` - the network

`Foundation` sits under everything: `AtomicFile`, `SharedFile`, `Folder`, `ReadResult` and `ChangeFailure`. `ReadResult` separates missing from unreadable, so a failed read is never taken for an empty answer. See [[Demo Mode]] for the sealed world under `Demo`.

`DESIGN.md` explains the look and the zones of the window. `AppSession` in `src/VaultManager.App/Services/AppServices.cs` owns one run of the app, including its single `HttpClient`.

## How a change flows

1. **Click.** A screen calls `ChangesViewModel.Run`, which adds `ChangeItem` records to `ChangeQueue` in the App and starts a run at once. A line carries the hashes it saw, such as `ExpectedTargetHash`, to notice a change before it writes. A request already on its way is not added twice, and a new line for the same vault and subject replaces a failed or needs-review one. Requests made during a run or an Undo go after it, and each pass is one operation for Undo. Core has no queue type: it receives a list.
2. **Gate.** `ChangeExecutor.ApplyAsync` runs line by line. A vault line waits if the folder is gone (`VaultOffline`) or the vault is open (`VaultOpen`). With Obsidian running and `obsidian.json` unreadable, nothing proves the vault is closed, so it counts as open. `RegisterVault` waits while Obsidian runs (`ObsidianRunning`). `CreateVault` has no gate.
3. **Snapshot.** Before a plugin swap or a settings overwrite, the current content is copied to `snapshots/<operation>/<line>/`. Other lines need no copy: their undo record says what to put back.
4. **Write.** Plugins are built in `.obsidian/.vault-manager-staging/` and swapped in with a directory move. An update replaces only the build files and carries everything else over. A failure puts the old folder back, and if that fails too the line reports `RecoveryNeeded` and staging stays on disk. Single files go through `AtomicFile`: write beside the target, flush, rename.
5. **Result.** Each line reports through `IProgress`: `Done`, `Waiting`, `NeedsReview` (source or target changed since it was queued) or `Failed`. Done lines go to the log and leave the queue after the next scan.
6. **Record.** In a `finally`, so also after a cancel, the undo records are saved as JSON in one SQLite transaction. Operations beyond the newest 20 are pruned with their snapshot folders. If no line produced an undo record, no operation is saved and its folder is deleted, unless a line needs recovery.
7. **Undo.** `UndoLast` takes the newest operation not yet undone and walks its lines newest first, under the same gate. A line blocked by a closed door stays, and the next Undo retries it. A line whose target moved on since it ran is refused and dropped, because undoing it would destroy newer work. Notes edited and settings changed since it ran are kept. When none remain, the operation is marked undone and its snapshot folder is deleted.

Per-kind detail is in [[Changes]].

## Waiting lines

The executor only reports `Waiting`. The window finishes those lines by itself, with no click.

- **Saved.** `ChangesViewModel.Persist` writes a waiting line to `pending_changes` in its `ChangeItemJson` form. Any other result, or a person removing the line, deletes the row.
- **Read back.** `LoadPersisted` restores the rows as waiting lines at start. Their reasons come with the first look at the gates. A row that cannot be parsed is deleted, and the log says `A waiting change could not be read and was dropped.`
- **Checked on a timer.** A timer in `App.axaml.cs` calls `WatchObsidianAsync` every 5 seconds. It looks at the process, and while lines wait, `RecheckWaitingAsync` asks `ChangeExecutor.Gate` about each one. So a drive coming back, or one vault closing while Obsidian keeps running, is found on the next tick with no rescan. With nothing waiting, it touches no disk. `StartAsync` also checks once after the first scan.
- **Run.** A line whose gate opened goes through the executor like any other, and the executor asks again before it writes. A plugin or settings line whose target moved on while it waited reports `NeedsReview` and overwrites nothing.

Other lines that remain, for this run of the app only:

- `Failed` lines have `Retry`, which runs the line as it was.
- `NeedsReview` lines can be removed. A plugin line also offers `Retry` when it can be renewed from the last scan (`QueueRules.Renew`), which never turns it into a downgrade. A settings line goes back through Compare.

## The window

- **Rail.** The nav is Plugins and Profiles. Below it are the vaults with `+ New vault`, and Changes in the corner when needed.
- **Changes panel.** `QueueRules.ShowPanel` draws it during a run, while a line waits, failed or needs review, and while an operation made in this session can be undone. An Undo problem keeps it up too. Undo records outlive the app, but they do not keep the panel up. After a restart an older undoable operation is offered in the `⋯` menu as `Undo last change (time)`.
- **Plugins home.** One list of one-line rows, most serious first, without section headers. A click opens a row's details and its `Keep as is`. The full table is a link away.
- **Log.** A strip of 28px with the latest event on one line. A click opens it to 104px.
- **Vault page.** What this vault has. Adding a plugin is a button with a filter. `Copy settings from...` in its menu opens Settings sync, with this vault as the fixed target. See [[Sync]].
- **Profiles.** One plain result per vault, such as `1 folder missing`. Details open on click, and the editor is behind `Edit`. See [[Profiles]].

## What SQLite holds and what stays as files

The database is `vault-manager.db`. The table list is in [[Store]].

| Lives in SQLite | Lives in files |
| :-- | :-- |
| Journal of operations, undo records | Snapshots: `snapshots/`, the last 20 operations |
| Sync bases: the hash two vaults last agreed on | Profiles: `profiles/*.vaultprofile.json` |
| Ignored items, app settings, a vault's chosen profile | The built-in profile: a resource embedded in Core |
| Catalog cache, online versions | Everything Obsidian owns: `obsidian.json`, each vault's `.obsidian/` |
| Waiting lines | |

The app's data folder is `%LOCALAPPDATA%\TechTeaStudio\VaultManager` on Windows and `~/.local/share/TechTeaStudio/VaultManager` on Linux. Snapshots are files and SQLite holds pointers to them, in the `SnapshotDir` of an undo record.

Obsidian's browser storage is only ever read. See [[Restricted mode read from browser storage]].

## Known limits

- **Only waiting lines survive a restart.** Failed and needs-review lines do not, and neither does a line the executor has not reported on yet. The window has no closing handler, so closing it gives no warning about them.
- **Waiting lines finish only while the app runs.** Nothing watches the gates after the window closes. The next start checks them.

## Links

- [[Changes]], [[Store]], [[Obsidian]]
- [[Copies not symlinks]], [[Profiles as files, everything else in SQLite]]
- [[Registration waits while Obsidian runs]], [[Run on click instead of plan then Apply]]
