---
type: index
status: active
priority: medium
tags: [service, core, sqlite]
aliases: [VaultManager.Core.Store]
summary: "AppDatabase: the app's own memory in SQLite. Outside every vault and never the truth about one."
related:
  - "[[Vault Manager Architecture]]"
  - "[[Changes]]"
  - "[[Profiles as files, everything else in SQLite]]"
created: 2026-11-02
updated: 2026-11-03
---

# Store

## Purpose

The app's own memory: journal, undo records, waiting lines, sync bases, ignored items and catalog cache. It lives outside every vault and is never the truth about a vault; the disk is.

## Responsibilities

- Keep operations and their undo records, so Undo survives a restart.
- Keep lines that wait for a gate, so they survive a restart too.
- Keep what the user decided: ignored items, settings, a vault's chosen profile.
- Keep sync bases and the catalog cache.

## Dependencies

`Microsoft.Data.Sqlite` 8.0.10, the last line that still targets net6.0, and `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12, which lifts the native SQLite above the version NuGet flags as vulnerable.

## Key Types

`AppDatabase`, `OperationRow`, `PendingRow`, `CachedCatalogEntry`, `OnlineVersion`.

## Details

File: `vault-manager.db` in the app's data folder, which is `%LOCALAPPDATA%\TechTeaStudio\VaultManager` on Windows and `~/.local/share/TechTeaStudio/VaultManager` on Linux. WAL journal, connection pooling off, one connection per call.

### Schema version 2

Version 1 made every table below except `pending_changes`, and version 2 adds that one. A version 1 file upgrades in place and keeps what it held.

| Table | Holds |
| :-- | :-- |
| `operations` | Id, time, undone flag |
| `operation_items` | One `undo_json` per line of an operation |
| `ignored` | Keys the user chose to ignore |
| `settings` | Key and value |
| `sync_bases` | Hash per `(source, target, path)` |
| `vault_profiles` | Profile id per vault id |
| `catalog` | Id, name, author, description, repo |
| `online_versions` | Latest version per plugin, with the check time |
| `pending_changes` | One waiting line per row, in order: `seq`, `id`, `item_json` |

### Rules

- **A newer file is refused.** If `user_version` is above this build's schema, the constructor throws: guessing at its shape could destroy undo history.
- **All or nothing.** An operation and all its undo records land in one transaction.
- **Pruning.** `PruneOperations` returns ids beyond the newest 20 so the executor can delete their snapshots. The count includes operations already undone.
- **The journal holds what can be undone.** An operation is saved only if at least one line produced an undo record. Failed and waiting lines, `CreateVault` and `RegisterVault` leave no row.
- **Undo updates the journal.** `ReplaceUndoRecords` keeps the lines that wait for a retry, and marks the operation undone when none remain. `RestoreSyncBase` puts a base back, or deletes it if the copy created it.
- **Only waiting lines are pending.** The window calls `SavePending` when a line reports `Waiting` and `RemovePending` on any other result or when a person removes it. Failed and needs-review lines leave no row. The text is the form written by `ChangeItemJson`, described in [[Changes]].

The window sets the keys in `ignored` and `settings`:

- `settings`: `language` and `online.checkedAt`.
- `ignored`: `older:<plugin>`, `differs:<plugin>`, `online:<plugin>:<version>`, and `off:`, `never:`, `gap:`, `list:`, `scan:` followed by a vault id. The version is in the key so that ignoring 1.1.0 does not hide 1.2.0.

The window also writes a sync base directly when a person answers a conflict with `Keep target`. See [[Sync]].

## Notable Decisions

- [[Profiles as files, everything else in SQLite]]

## Links

- [[Changes]]
- [[Sync]]
- [[Catalog]]
- [[Vault Manager Architecture]]
